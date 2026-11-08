using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace Qiban.Core;

/// <summary>Owns application files. Persistent user data is never treated as cache.</summary>
public sealed class PortableStore
{
    private const long MaxStateBytes = 128 * 1024 * 1024;
    private const long MaxImportBytes = MaxStateBytes;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };
    private readonly object gate = new();
    public string Root { get; }
    public string DataDirectory => Path.Combine(Root, "data");
    public string CacheDirectory => Path.Combine(Root, "cache");
    public string AssetsDirectory => Path.Combine(DataDirectory, "assets");
    public string? RecoveryNotice { get; private set; }
    public string? PersistenceWarning { get; private set; }
    private string StatePath => Path.Combine(DataDirectory, "state.json");
    private string BackupPath => Path.Combine(DataDirectory, "state.backup.json");

    public PortableStore(string root) => Root = Path.GetFullPath(root);

    public void Initialize()
    {
        foreach (var dir in new[] { Root, DataDirectory, AssetsDirectory, CacheDirectory,
                     Path.Combine(CacheDirectory, "tmp"), Path.Combine(CacheDirectory, "logs") })
        {
            EnsureSafePath(dir);
            Directory.CreateDirectory(dir);
        }
        var probe = Path.Combine(DataDirectory, $".write-test-{Guid.NewGuid():N}.tmp");
        try { using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
        // Process-only environment: never changes the user's Windows environment.
        foreach (var name in new[] { "TEMP", "TMP", "TMPDIR" })
            Environment.SetEnvironmentVariable(name, Path.Combine(CacheDirectory, "tmp"));
        foreach (var (name, child) in new[]
        {
            ("XDG_CACHE_HOME", "external"), ("HF_HOME", "huggingface"),
            ("HUGGINGFACE_HUB_CACHE", "huggingface/hub"), ("TORCH_HOME", "torch"),
            ("TRANSFORMERS_CACHE", "huggingface/transformers"), ("PYTHONPYCACHEPREFIX", "python"),
            ("PIP_CACHE_DIR", "pip"), ("NUMBA_CACHE_DIR", "numba"),
            ("MPLCONFIGDIR", "matplotlib"), ("DOTNET_BUNDLE_EXTRACT_BASE_DIR", "runtime")
        }) Environment.SetEnvironmentVariable(name, Path.Combine(CacheDirectory, child));
        // Do not remove another writer's temporary file during concurrent initialization.
        using (AcquireStoreLock())
            foreach (var file in SafeFiles(DataDirectory).Where(f => Path.GetFileName(f).StartsWith(".state-", StringComparison.Ordinal) && f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
                TryDelete(file);
    }

    public AppState Load()
    {
        lock (gate)
        {
            using var lease = AcquireStoreLock();
            RecoveryNotice = null;
            PersistenceWarning = null;
            var pair = ReadPair();
            if (pair.Latest == null) return new AppState();
            var latest = pair.Latest;
            RepairCopy(StatePath, pair.Main, latest);
            // Legacy backups contain the previous state; preserve them until the first upgraded save.
            if (latest.Revision > 0) RepairCopy(BackupPath, pair.Backup, latest);
            var state = latest.State;
            state.StorageRevision = latest.Revision;
            state.StorageToken = latest.Hash;
            foreach (var message in state.Friends.SelectMany(f => f.Messages))
                if (message.Status is "streaming" or "pending" or "sending") message.Status = "interrupted";
            return state;
        }
    }

    public void Save(AppState state, bool purgeBackup = false)
    {
        lock (gate)
        {
            using var lease = AcquireStoreLock();
            PersistenceWarning = null;
            Validate(state);
            var current = ReadPair().Latest;
            if (current != null && (state.StorageRevision != current.Revision || state.StorageToken != current.Hash)
                || current == null && (state.StorageRevision != 0 || state.StorageToken != null))
                throw new InvalidOperationException("数据已被其他实例修改，未覆盖现有记忆。请重新载入后重试。");
            var next = Encode(state, checked((current?.Revision ?? 0) + 1));
            var bytes = next.Bytes;
            if (bytes.LongLength > MaxStateBytes)
                throw new InvalidDataException("本地记录超过 128 MB，请先导出并清理较早的聊天记录。未删除任何记录。");
            // This is the commit point. Both normal saves and deletions use the same protocol.
            WriteAtomic(BackupPath, bytes, null);
            state.StorageRevision = next.Revision;
            state.StorageToken = next.Hash;
            try { WriteAtomic(StatePath, bytes, null); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { PersistenceWarning = "已保存，主文件副本待修复；下次载入将使用最新备份。"; }
        }
    }

    public string ImportAsset(string source)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif"))
            throw new InvalidDataException("请选择 PNG、JPG、BMP、GIF 或 WebP 图片。");
        var info = new FileInfo(source);
        if (!info.Exists || info.Length is <= 0 or > 20 * 1024 * 1024)
            throw new InvalidDataException("图片不能为空，且不能超过 20 MB。");
        var destination = Path.Combine(AssetsDirectory, Guid.NewGuid().ToString("N") + extension);
        EnsureSafePath(destination);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            try { CopyBounded(input, output, 20 * 1024 * 1024); }
            catch { output.Dispose(); TryDelete(destination); throw; }
        }
        return Path.GetRelativePath(Root, destination);
    }

    public string? ResolveAsset(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(Root, relative));
            if (!IsWithin(path, AssetsDirectory)) return null;
            EnsureSafePath(path);
            return File.Exists(path) ? path : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>Call only after a successful save; consults both current state and recovery backup.</summary>
    public long PruneAssets(AppState state)
    {
        lock (gate)
        {
            using var lease = AcquireStoreLock();
            var pair = ReadPair();
            if (pair.Latest == null || pair.Latest.Revision != state.StorageRevision || pair.Latest.Hash != state.StorageToken) return 0;
            var paths = state.Friends.SelectMany(f => new[] { f.AvatarPath, f.PortraitPath, f.ChatBackgroundPath }).ToList();
            // Protect both copies, including a lagging main after a successful degraded save.
            foreach (var snapshot in new[] { pair.Main.Snapshot, pair.Backup.Snapshot })
                if (snapshot != null) paths.AddRange(snapshot.State.Friends.SelectMany(f => new[] { f.AvatarPath, f.PortraitPath, f.ChatBackgroundPath }));
            if (pair.Main.Damaged || pair.Backup.Damaged) return 0;
            var used = paths.Select(ResolveAsset).Where(p => p != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            long freed = 0;
            foreach (var path in SafeFiles(AssetsDirectory))
                if (!used.Contains(path) && IsImportedImage(path)) freed += TryDelete(path);
            return freed;
        }
    }

    private static bool IsImportedImage(string path) =>
        Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)
        && Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";

    public long CleanupCache(AppSettings settings, bool all = false)
    {
        lock (gate)
        {
            EnsureSafePath(CacheDirectory);
            var expiry = DateTime.UtcNow.AddHours(-Math.Clamp(settings.CacheHours, 1, 720));
            var limit = Math.Clamp(settings.CacheLimitMb, 1, 4096) * 1024L * 1024;
            var files = SafeFiles(CacheDirectory).Select(path => new FileInfo(path)).OrderBy(f => f.LastWriteTimeUtc).ToList();
            long remaining = files.Sum(FileLength);
            long freed = 0;
            foreach (var file in files)
            {
                // .part/.tmp files belong to in-flight downloads; only expire after 1 hour unless manually clearing.
                var partial = file.Extension is ".part" or ".tmp";
                if (!all && partial && file.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-1)) continue;
                var log = IsWithin(file.FullName, Path.Combine(CacheDirectory, "logs"));
                if (!all && remaining <= limit && file.LastWriteTimeUtc >= expiry && !(log && FileLength(file) > 1024 * 1024)) continue;
                var bytes = TryDelete(file.FullName);
                freed += bytes;
                remaining -= bytes;
            }
            PruneEmptyCacheDirectories();
            return freed;
        }
    }

    private void PruneEmptyCacheDirectories()
    {
        if (!Directory.Exists(CacheDirectory)) return;
        var pending = new Stack<string>();
        var directories = new List<string>();
        string sessions = Path.Combine(CacheDirectory, "voice-engine");
        pending.Push(CacheDirectory);
        while (pending.TryPop(out var current))
        {
            try
            {
                EnsureSafePath(current);
                foreach (var child in Directory.GetDirectories(current))
                {
                    try
                    {
                        EnsureSafePath(child); // Reject the entry and every ancestor before traversing.
                        // Retained/locked session files may belong to an active worker. Keep its
                        // complete environment tree, including temporarily empty cache folders.
                        if (Path.GetDirectoryName(child) == sessions && SafeFiles(child).Any()) continue;
                        directories.Add(child);
                        pending.Push(child);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        for (int i = directories.Count - 1; i >= 0; i--)
        {
            string path = directories[i];
            if (path == Path.Combine(CacheDirectory, "tmp") || path == Path.Combine(CacheDirectory, "logs")) continue;
            try
            {
                if (!IsWithin(path, CacheDirectory)) continue; // Never delete the cache root.
                EnsureSafePath(path);
                // Nonrecursive deletion is also the final, atomic emptiness check if a writer
                // creates a file after the scan. A nonempty or locked directory is simply kept.
                Directory.Delete(path, recursive: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public (long Data, long Cache) GetStorageBytes() =>
        (SafeFiles(DataDirectory).Sum(p => FileLength(new FileInfo(p))), SafeFiles(CacheDirectory).Sum(p => FileLength(new FileInfo(p))));

    public void Export(FriendProfile profile, string destination)
    {
        ValidateProfile(profile);
        var copy = JsonSerializer.Deserialize<FriendProfile>(JsonSerializer.SerializeToUtf8Bytes(profile, Json), Json)!;
        // Export the portable voice identity, never credentials, connection details or local files.
        copy.AvatarPath = "";
        copy.PortraitPath = "";
        copy.ChatBackgroundPath = "";
        ClearVoiceConnection(copy.Voice);
        var document = JsonSerializer.SerializeToNode(copy, Json)!.AsObject();
        document["Voice"]!.AsObject().Remove("ProtectedApiKey");
        document["$qibanExport"] = "friend-v1";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.LongLength > MaxImportBytes) throw new InvalidDataException("角色导出超过 128 MB，无法安全导入。请先缩减角色记录后重试。");
        var fullPath = Path.GetFullPath(destination);
        if (!Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("角色备份必须保存为 .json 文件，不能覆盖程序或其他类型文件。");
        if (IsWithin(fullPath, DataDirectory) || IsWithin(fullPath, CacheDirectory))
            throw new InvalidOperationException("请导出到 data 和 cache 目录之外，以免覆盖应用数据。");
        EnsureNoReparsePoints(fullPath);
        if (File.Exists(fullPath) && IsWithin(fullPath, Root) && !IsFriendExport(fullPath))
            throw new InvalidOperationException("不能覆盖程序目录内已有的非角色备份文件。请使用新的文件名。");
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".qiban-export-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public FriendProfile ImportFriend(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length is <= 0 or > MaxImportBytes) throw new InvalidDataException("导入文件不能为空且不能超过 128 MB。");
        using var stream = File.OpenRead(file);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String
            || !document.RootElement.TryGetProperty("Persona", out var persona) || persona.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("文件不是有效的朋友备份：缺少 Name 或 Persona 字段。");
        RequireJapaneseShape(document.RootElement);
        var friend = document.RootElement.Deserialize<FriendProfile>(Json) ?? throw new InvalidDataException("没有有效的角色数据。");
        ValidateProfile(friend);
        friend.Id = Guid.NewGuid().ToString("N");
        friend.AvatarPath = "";
        friend.PortraitPath = "";
        friend.ChatBackgroundPath = "";
        ClearVoiceConnection(friend.Voice);
        foreach (var message in friend.Messages)
            if (message.Status is "streaming" or "pending" or "sending") message.Status = "interrupted";
        return friend;
    }

    private sealed record Snapshot(AppState State, long Revision, string Hash, byte[] Bytes);
    private sealed record Copy(Snapshot? Snapshot, bool Damaged);
    private sealed record SnapshotPair(Copy Main, Copy Backup, Snapshot? Latest);

    private FileStream AcquireStoreLock()
    {
        var path = Path.Combine(DataDirectory, ".store.lock");
        EnsureSafePath(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("数据正在被其他实例使用，操作未提交。请稍后重试。", error); }
    }

    private SnapshotPair ReadPair()
    {
        var main = ReadCopy(StatePath);
        var backup = ReadCopy(BackupPath);
        var a = main.Snapshot;
        var b = backup.Snapshot;
        if (a == null && b == null)
        {
            if (main.Damaged || backup.Damaged)
                throw new InvalidDataException("数据及可用备份均不完整，原文件已保留。请从导出文件恢复；未创建空白记忆。");
            return new(main, backup, null);
        }
        if (a != null && b != null && a.Revision > 0 && a.Revision == b.Revision && a.Hash != b.Hash)
            throw new InvalidDataException("同一修订的数据副本不一致，已停止写入。请保留 data 目录并核对备份。");
        // Old v1 used a previous-version backup without revisions; its main remains authoritative.
        var latest = a == null ? b : b == null || a.Revision >= b.Revision ? a : b;
        return new(main, backup, latest);
    }

    private Copy ReadCopy(string path)
    {
        EnsureSafePath(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxStateBytes) throw new InvalidDataException("数据文件超过允许的 128 MB。");
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            CheckKnownVersions(document.RootElement);
            RequireShape(document.RootElement);
            var root = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
            long revision = 0;
            string hash;
            if (root.TryGetPropertyValue("$storage", out var metadata))
            {
                if (metadata is not JsonObject meta || !meta.TryGetPropertyValue("Format", out var format)
                    || format is not JsonValue fv || !fv.TryGetValue<int>(out var formatNumber))
                    throw new InvalidDataException("快照元数据不完整。");
                if (formatNumber != 1) throw new NotSupportedException("快照格式来自其他版本，原数据未修改。");
                if (!meta.TryGetPropertyValue("Revision", out var version) || version is not JsonValue rv
                    || !rv.TryGetValue<long>(out revision) || revision <= 0
                    || !meta.TryGetPropertyValue("Sha256", out var checksum) || checksum is not JsonValue cv
                    || !cv.TryGetValue<string>(out var expected) || expected == null || expected.Length != 64)
                    throw new InvalidDataException("快照修订或校验信息不完整。");
                meta.Remove("Sha256");
                hash = Hash(root);
                if (!string.Equals(hash, expected, StringComparison.Ordinal)) throw new InvalidDataException("快照完整性校验失败。");
                meta["Sha256"] = expected;
            }
            else hash = Hash(root);
            // Repairs must retain the original verified representation and optimistic token.
            // Migration becomes durable only with the next ordinary atomic Save.
            var originalBytes = JsonSerializer.SerializeToUtf8Bytes(root, Json);
            MigrateLegacyVoice(root);
            var state = root.Deserialize<AppState>(Json) ?? throw new InvalidDataException("数据文件为空。");
            Validate(state);
            MigrateEmbeddedVoice(state);
            return new(new(state, revision, hash, originalBytes), false);
        }
        catch (FileNotFoundException) { return new(null, false); }
        catch (DirectoryNotFoundException) { return new(null, false); }
        // Sharing/permission failures are not corruption and must never trigger stale fallback.
        catch (Exception error) when (error is JsonException or InvalidDataException) { return new(null, true); }
    }

    private static Snapshot Encode(AppState state, long revision)
    {
        var root = JsonSerializer.SerializeToNode(state, Json)!.AsObject();
        var metadata = new JsonObject { ["Format"] = 1, ["Revision"] = revision };
        root["$storage"] = metadata;
        var hash = Hash(root);
        metadata["Sha256"] = hash;
        return new(state, revision, hash, JsonSerializer.SerializeToUtf8Bytes(root, Json));
    }

    private static string Hash(JsonObject root) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(root, Json)));

    private void RepairCopy(string path, Copy copy, Snapshot latest)
    {
        if (copy.Snapshot?.Revision == latest.Revision && copy.Snapshot.Hash == latest.Hash) return;
        try
        {
            if (copy.Damaged)
            {
                var damaged = Path.Combine(DataDirectory, $"state.damaged-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
                EnsureSafePath(damaged);
                File.Copy(path, damaged, false);
            }
            WriteAtomic(path, latest.Bytes, null);
            RecoveryNotice = "已恢复最新有效数据并同步副本；损坏原文件保留在 data 目录。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { PersistenceWarning = "最新数据已载入，但副本暂时无法修复。请检查磁盘空间和文件占用。"; }
    }

    private static void RequireShape(JsonElement root)
    {
        // Constructors have defaults for new profiles. They must not fill holes in stored data.
        Require(root, "Version", "SelectedFriendId", "Settings", "Friends");
        if (root.GetProperty("Version").ValueKind != JsonValueKind.Number || !root.GetProperty("Version").TryGetInt32(out var version)) throw new InvalidDataException("数据版本无效。");
        if (version != 1) throw new NotSupportedException("此数据由不同版本创建，请使用对应版本打开。原文件未修改。");
        var settings = root.GetProperty("Settings");
        Require(settings, "Endpoint", "Model", "ProtectedApiKey", "Temperature", "ContextMessages", "MemoryBudget", "SuggestMemories", "CacheLimitMb", "CacheHours");
        bool hasLegacyVoice = settings.TryGetProperty("Voice", out var legacyVoice);
        if (hasLegacyVoice) RequireVoiceShape(legacyVoice);
        // Optional only for older versions; if present, new settings must be complete.
        if (settings.TryGetProperty("Initiative", out var initiative))
            Require(initiative, "IdleEnabled", "ActivityEnabled", "MaxUnanswered");
        if (root.GetProperty("Friends").ValueKind != JsonValueKind.Array) throw new InvalidDataException("角色列表缺失。");
        foreach (var friend in root.GetProperty("Friends").EnumerateArray())
        {
            Require(friend, "Id", "Name", "Tagline", "Persona", "Greeting", "AvatarPath", "PortraitPath", "Messages", "Memories");
            RequireJapaneseShape(friend);
            if (friend.TryGetProperty("Voice", out var voice)) RequireVoiceShape(voice);
            else if (!hasLegacyVoice) throw new InvalidDataException("角色语音配置缺失，未使用默认值替代原数据。");
            if (friend.TryGetProperty("InitiativeProfile", out var profile) && profile.ValueKind != JsonValueKind.Null)
                Require(profile, "IdleMinMinutes", "IdleMaxMinutes", "ActivityProbability", "ActivityCooldownMinutes");
            if (friend.TryGetProperty("Learning", out var learning))
            {
                Require(learning, "LastProcessedMessageId", "SummarySuppressedThroughMessageId", "Summary", "SuppressedSourceIds", "SummarySuppressedSourceIds");
                if (learning.GetProperty("Summary").ValueKind != JsonValueKind.Array || learning.GetProperty("SuppressedSourceIds").ValueKind != JsonValueKind.Array
                    || learning.GetProperty("SummarySuppressedSourceIds").ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("自动记忆记录不完整。");
                foreach (var item in learning.GetProperty("Summary").EnumerateArray())
                    Require(item, "Text", "Role", "SourceMessageId", "Evidence");
            }
            if (friend.GetProperty("Messages").ValueKind != JsonValueKind.Array || friend.GetProperty("Memories").ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("聊天或记忆列表缺失。");
            foreach (var message in friend.GetProperty("Messages").EnumerateArray())
                Require(message, "Id", "Role", "Text", "CreatedAt", "Status");
            foreach (var memory in friend.GetProperty("Memories").EnumerateArray())
                Require(memory, "Id", "Content", "Category", "State", "Pinned", "Confidence", "SourceMessageId", "CreatedAt", "UpdatedAt", "ExpiresAt", "LastUsedAt");
        }
        RejectDuplicateProperties(root);
    }

    private static void RequireVoiceShape(JsonElement voice) => Require(voice, "Enabled", "AutoPlay", "Provider",
        "Endpoint", "ProtectedApiKey", "Model", "Voice", "Language", "ReferenceAudio", "ReferenceText",
        "GptWeights", "SovitsWeights", "RequestTemplate");

    private static void RequireJapaneseShape(JsonElement friend)
    {
        // Absent fields belong to older versions. Present settings and bilingual records
        // must be complete, so damaged data cannot silently become a different mode.
        if (friend.TryGetProperty("JapaneseChat", out var japanese))
            Require(japanese, "Enabled", "DisplayLanguage");
        if (friend.TryGetProperty("Messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            foreach (var message in messages.EnumerateArray())
                if (message.ValueKind == JsonValueKind.Object &&
                    (message.TryGetProperty("JapaneseText", out _) || message.TryGetProperty("DisplayLanguage", out _)))
                    Require(message, "JapaneseText", "DisplayLanguage");
    }

    private static void MigrateLegacyVoice(JsonObject root)
    {
        var settings = root["Settings"]!.AsObject();
        if (!settings.TryGetPropertyValue("Voice", out var legacy)) return;
        var legacyVoice = legacy ?? throw new InvalidDataException("旧语音配置不完整。");
        ValidateVoice(legacyVoice.Deserialize<VoiceSettings>(Json));
        foreach (var node in root["Friends"]!.AsArray())
        {
            var friend = node!.AsObject();
            if (!friend.ContainsKey("Voice")) friend["Voice"] = legacyVoice.DeepClone();
        }
        settings.Remove("Voice");
    }

    private static void ClearVoiceConnection(VoiceSettings voice)
    {
        voice.Enabled = false;
        voice.AutoPlay = false;
        voice.ProtectedApiKey = "";
        voice.Endpoint = "";
        voice.ReferenceAudio = "";
        voice.GptWeights = "";
        voice.SovitsWeights = "";
        // Custom request bodies can contain embedded API keys outside the dedicated key field.
        voice.RequestTemplate = new VoiceSettings().RequestTemplate;
    }

    private static void MigrateEmbeddedVoice(AppState state)
    {
        foreach (var voice in state.Friends.Select(friend => friend.Voice))
        {
            // Only the previous built-in localhost default becomes embedded. Explicit external
            // choices use a distinct provider value so reopening never changes that decision.
            if (voice.Provider == "GPT-SoVITS" && Uri.TryCreate(voice.Endpoint, UriKind.Absolute, out var endpoint)
                && endpoint.Scheme == "http" && endpoint.IsLoopback && endpoint.Port == 9880
                && endpoint.UserInfo.Length == 0 && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0
                && endpoint.AbsolutePath.TrimEnd('/') is "" or "/tts")
                voice.Provider = "GPT-SoVITS 内置";
        }
    }

    private static void CheckKnownVersions(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var number) && number != 1)
            throw new NotSupportedException("数据来自其他版本，原文件未修改。");
        if (root.TryGetProperty("$storage", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("Format", out var format) && format.ValueKind == JsonValueKind.Number
            && format.TryGetInt32(out var formatNumber) && formatNumber != 1)
            throw new NotSupportedException("快照格式来自其他版本，原数据未修改。");
    }

    private static void Require(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object || names.Any(name => !element.TryGetProperty(name, out _)))
            throw new InvalidDataException("存储字段不完整，未使用默认值替代原数据。");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("数据包含重复字段。");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private void WriteAtomic(string destination, byte[] content, string? backup)
    {
        EnsureSafePath(destination);
        if (backup != null) EnsureSafePath(backup);
        var temporary = Path.Combine(DataDirectory, $".state-{Guid.NewGuid():N}.tmp");
        EnsureSafePath(temporary);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            { stream.Write(content); stream.Flush(true); }
            if (File.Exists(destination)) File.Replace(temporary, destination, backup, true);
            else File.Move(temporary, destination);
        }
        finally { TryDelete(temporary); }
    }

    private static void Validate(AppState state)
    {
        if (state.Version != 1) throw new NotSupportedException("此数据由不同版本创建，请使用对应版本打开。原文件未修改。");
        if (state.Settings == null || state.Settings.Initiative == null || state.Friends == null || state.Friends.Count is < 1 or > 1000)
            throw new InvalidDataException("应用数据结构不完整。");
        var settings = state.Settings;
        // Theme preferences are optional for older snapshots and never invalidate user records.
        settings.InterfaceTheme = settings.InterfaceTheme == "xp" ? "xp" : "standard";
        if (settings.Initiative.MaxUnanswered is < 1 or > 10)
            throw new InvalidDataException("主动聊天上限应为 1～10 条。");
        if (new[] { settings.Endpoint, settings.Model, settings.ProtectedApiKey }
            .Any(value => value == null || value.Length > 65536)
            || !double.IsFinite(settings.Temperature) || settings.Temperature is < 0 or > 2
            || settings.ContextMessages is < 2 or > 200 || settings.MemoryBudget is < 0 or > 32000
            || settings.CacheLimitMb is < 1 or > 4096 || settings.CacheHours is < 1 or > 720)
            throw new InvalidDataException("模型、语音或缓存设置包含空字段或无效数值。请使用有效备份恢复。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var friend in state.Friends)
        {
            ValidateProfile(friend);
            if (!ids.Add(friend.Id)) throw new InvalidDataException("角色标识重复。");
        }
        if (!ids.Contains(state.SelectedFriendId)) state.SelectedFriendId = state.Friends[0].Id;
    }

    private static void ValidateProfile(FriendProfile? friend)
    {
        if (friend == null || string.IsNullOrWhiteSpace(friend.Id) || string.IsNullOrWhiteSpace(friend.Name)
            || friend.Name.Length > 100 || friend.Id.Length > 128 || friend.Persona == null || friend.Persona.Length > 32000
            || friend.Tagline == null || friend.Tagline.Length > 1000 || friend.Greeting == null || friend.Greeting.Length > 16000
            || friend.Messages == null || friend.Messages.Count > 100000 || friend.Memories == null || friend.Memories.Count > 20000)
            throw new InvalidDataException("角色资料不完整或超出合理长度，请检查导入文件。");
        friend.AvatarPath ??= "";
        friend.PortraitPath ??= "";
        friend.ChatBackgroundPath ??= "";
        ValidateVoice(friend.Voice);
        if (friend.JapaneseChat == null || string.IsNullOrWhiteSpace(friend.JapaneseChat.DisplayLanguage)
            || friend.JapaneseChat.DisplayLanguage.Length > 80 || friend.JapaneseChat.DisplayLanguage.Any(char.IsControl))
            throw new InvalidDataException("日语聊天的显示语言不能为空，且不能超过 80 字符。");
        if (friend.InitiativeProfileKey == null || friend.InitiativeProfileKey.Length is not (0 or 64))
            throw new InvalidDataException("角色节奏缓存无效。");
        if (friend.InitiativeProfile is { } profile &&
            (!double.IsFinite(profile.IdleMinMinutes) || !double.IsFinite(profile.IdleMaxMinutes)
            || !double.IsFinite(profile.ActivityProbability) || !double.IsFinite(profile.ActivityCooldownMinutes)
            || profile.IdleMinMinutes is < 0.5 or > 240 || profile.IdleMaxMinutes < profile.IdleMinMinutes || profile.IdleMaxMinutes > 240
            || profile.ActivityProbability is < 0.01 or > 0.8 || profile.ActivityCooldownMinutes is < 0.5 or > 120))
            throw new InvalidDataException("角色节奏参数超出范围。");
        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in friend.Messages)
            if (item == null || string.IsNullOrEmpty(item.Id) || !messageIds.Add(item.Id) || item.Text == null || item.Text.Length > 200000
                || item.JapaneseText == null || item.JapaneseText.Length > 200000 || item.DisplayLanguage == null || item.DisplayLanguage.Length > 80
                || item.Role is not ("user" or "assistant" or "system") || item.Status == null || item.Trigger is not ("" or "idle" or "activity"))
                throw new InvalidDataException("聊天记录包含无效内容。");
        var memoryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in friend.Memories)
            if (item == null || string.IsNullOrEmpty(item.Id) || !memoryIds.Add(item.Id) || string.IsNullOrWhiteSpace(item.Content)
                || item.Content.Length > 8000 || item.State is not ("pending" or "approved" or "archived")
                || item.Category == null || item.SourceMessageId == null || !double.IsFinite(item.Confidence)
                || item.Origin is not ("manual" or "model")
                || item.Confidence is < 0 or > 1)
                throw new InvalidDataException("记忆记录包含无效内容。");
        var learning = friend.Learning;
        if (learning == null || learning.LastProcessedMessageId == null || learning.LastProcessedMessageId.Length > 128
            || learning.SummarySuppressedThroughMessageId == null || learning.SummarySuppressedThroughMessageId.Length > 128
            || learning.Summary == null || learning.Summary.Count > 12 || learning.SuppressedSourceIds == null
            || learning.SummarySuppressedSourceIds == null || learning.SummarySuppressedSourceIds.Count > 100000
            || learning.SummarySuppressedSourceIds.Any(id => string.IsNullOrEmpty(id) || id.Length > 128)
            || learning.SummarySuppressedSourceIds.Distinct(StringComparer.Ordinal).Count() != learning.SummarySuppressedSourceIds.Count
            || learning.SuppressedSourceIds.Count > 100000
            || learning.SuppressedSourceIds.Any(id => string.IsNullOrEmpty(id) || id.Length > 128)
            || learning.SuppressedSourceIds.Distinct(StringComparer.Ordinal).Count() != learning.SuppressedSourceIds.Count)
            throw new InvalidDataException("自动记忆进度或屏蔽记录无效。");
        int summaryCharacters = 0;
        foreach (var item in learning.Summary)
            if (item == null || string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > 400
                || (summaryCharacters += item.Text.Length) > MemoryLearning.SummaryCharacterLimit
                || item.Role is not ("user" or "assistant") || string.IsNullOrEmpty(item.SourceMessageId) || item.SourceMessageId.Length > 128
                || string.IsNullOrWhiteSpace(item.Evidence) || item.Evidence.Length > 2000)
                throw new InvalidDataException("对话摘要包含无效内容。");
    }

    private static void ValidateVoice(VoiceSettings? voice)
    {
        if (voice == null || new[] { voice.Provider, voice.Endpoint, voice.ProtectedApiKey, voice.Model,
            voice.Voice, voice.Language, voice.ReferenceAudio, voice.ReferenceText, voice.GptWeights,
            voice.SovitsWeights, voice.RequestTemplate }.Any(value => value == null || value.Length > 65536))
            throw new InvalidDataException("角色语音配置包含空字段或超出长度限制。");
    }

    private void EnsureSafePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.Equals(Root, StringComparison.OrdinalIgnoreCase) && !IsWithin(full, Root))
            throw new IOException("文件路径必须位于程序目录内。");
        EnsureNoReparsePoints(full);
    }

    private static void EnsureNoReparsePoints(string full)
    {
        // Check existing ancestors before creating / traversing files; applies to exports too.
        for (var current = full; current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("程序数据目录不能使用符号链接或目录联接，请放到真实的可写文件夹。");
    }

    private static bool IsFriendExport(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxImportBytes) return false;
            using var stream = File.OpenRead(path);
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("$qibanExport", out var kind)
                && kind.ValueKind == JsonValueKind.String && kind.GetString() == "friend-v1";
        }
        catch (Exception e) when (e is IOException or JsonException) { return false; }
    }

    private IEnumerable<string> SafeFiles(string directory)
    {
        EnsureSafePath(directory);
        if (!Directory.Exists(directory)) yield break;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            string[] entries;
            try { EnsureSafePath(current); entries = Directory.GetFileSystemEntries(current); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }

    private long TryDelete(string file)
    {
        try
        {
            EnsureSafePath(file);
            if (!File.Exists(file)) return 0;
            var length = new FileInfo(file).Length;
            File.Delete(file);
            return length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static bool IsWithin(string path, string directory) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static long FileLength(FileInfo file) { try { return file.Length; } catch (IOException) { return 0; } }

    private static void CopyBounded(Stream source, Stream destination, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = source.Read(buffer)) > 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("文件超过允许大小。");
            destination.Write(buffer, 0, count);
        }
    }
}
