using Qiban.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

var testRoot = Path.Combine(Path.GetFullPath(args.Length > 0 ? args[0] : ".build/test-data"), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    passed++;
    Console.WriteLine("PASS: " + label);
}
void Reject(Action action, string label)
{
    var rejected = false;
    try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or NotSupportedException) { rejected = true; }
    Check(rejected, label);
}

try
{
    InitiativeTests.Run(Check);
    BackgroundTests.Run(testRoot, Check);
    CacheCleanupTests.Run(testRoot, Check);
    VoiceProfileTests.Run(testRoot, Check);
    EmbeddedVoiceMigrationTests.Run(testRoot, Check);
    JapaneseChatTests.Run(testRoot, Check);
    ThemeSettingsTests.Run(testRoot, Check);
    MemoryLearningTests.Run(testRoot, Check);
    const string oldDefaultPersona = "你是小栖，一个温和、有好奇心、有自己想法的电子朋友。用自然简短的中文聊天，像熟悉的朋友发消息。认真听用户说话，不要每次都追问，不要说教。喜欢散步、音乐和日常的小发现。可以轻松开玩笑，但不要强行卖萌。不要编造共同经历；不确定的记忆要坦诚询问。";
    const string oldDefaultGreeting = "嗨，我是小栖。\n今天有什么想和我聊聊的吗？";
    FriendProfile OldDefault() => new() { Id = "default", Name = "小栖", Persona = oldDefaultPersona, Greeting = oldDefaultGreeting };
    var freshDefault = new AppState().Friends.Single();
    Check(freshDefault.Name == "五河琴里" && freshDefault.Name == DefaultCharacters.KotoriName
        && freshDefault.Persona == DefaultCharacters.KotoriPersona && freshDefault.Greeting == DefaultCharacters.KotoriGreeting,
        "new profiles use the complete Kotori default preset");

    var legacyCharacter = OldDefault();
    legacyCharacter.Tagline = "保留用户简介";
    legacyCharacter.AvatarPath = "data/assets/custom-avatar.png";
    legacyCharacter.PortraitPath = "data/assets/custom-portrait.png";
    var legacyChat = new ChatMessage { Text = "记住：我喜欢乌龙茶", CreatedAt = DateTimeOffset.Parse("2025-02-03T12:30:00+08:00") };
    var legacyMemory = new MemoryItem { Content = "我喜欢乌龙茶", State = "approved", SourceMessageId = legacyChat.Id, Pinned = true, CreatedAt = legacyChat.CreatedAt, UpdatedAt = legacyChat.CreatedAt };
    legacyCharacter.Messages.Add(legacyChat);
    legacyCharacter.Memories.Add(legacyMemory);
    var preservedRecords = JsonSerializer.Serialize(new { legacyCharacter.Messages, legacyCharacter.Memories });
    var migrationState = new AppState { Friends = [legacyCharacter], Settings = new AppSettings { Model = "user-model" } };
    var originalSettings = migrationState.Settings;
    Check(DefaultCharacters.UpgradeLegacyDefault(migrationState)
        && legacyCharacter.Name == DefaultCharacters.KotoriName
        && legacyCharacter.Persona == DefaultCharacters.KotoriPersona
        && legacyCharacter.Greeting == DefaultCharacters.KotoriGreeting,
        "untouched legacy default upgrades its persona and stock greeting");
    Check(ReferenceEquals(migrationState.Friends.Single(), legacyCharacter) && legacyCharacter.Id == "default"
        && ReferenceEquals(legacyCharacter.Messages.Single(), legacyChat) && ReferenceEquals(legacyCharacter.Memories.Single(), legacyMemory)
        && preservedRecords == JsonSerializer.Serialize(new { legacyCharacter.Messages, legacyCharacter.Memories }),
        "default upgrade preserves profile identity, chat and sourced memory objects without rewriting records");
    Check(legacyCharacter.Tagline == "保留用户简介" && legacyCharacter.AvatarPath == "data/assets/custom-avatar.png"
        && legacyCharacter.PortraitPath == "data/assets/custom-portrait.png" && migrationState.SelectedFriendId == "default"
        && ReferenceEquals(migrationState.Settings, originalSettings) && originalSettings.Model == "user-model",
        "default upgrade preserves custom assets, tagline, selection and connection settings");
    string upgradedPresetState = JsonSerializer.Serialize(migrationState);
    Check(!DefaultCharacters.UpgradeLegacyDefault(migrationState) && JsonSerializer.Serialize(migrationState) == upgradedPresetState,
        "default preset upgrade is idempotent");

    var renamedDefault = OldDefault();
    renamedDefault.Name = "用户自定义名称";
    Check(!DefaultCharacters.UpgradeLegacyDefault(new AppState { Friends = [renamedDefault] })
        && renamedDefault.Name == "用户自定义名称" && renamedDefault.Persona == oldDefaultPersona && renamedDefault.Greeting == oldDefaultGreeting,
        "renamed default character is not overwritten by the preset upgrade");
    var rewrittenDefault = OldDefault();
    rewrittenDefault.Persona = "用户编写的人物设定";
    Check(!DefaultCharacters.UpgradeLegacyDefault(new AppState { Friends = [rewrittenDefault] })
        && rewrittenDefault.Name == "小栖" && rewrittenDefault.Persona == "用户编写的人物设定" && rewrittenDefault.Greeting == oldDefaultGreeting,
        "custom persona is not overwritten even when the old character name remains");
    var separateCharacter = OldDefault();
    separateCharacter.Id = "user-created-character";
    Check(!DefaultCharacters.UpgradeLegacyDefault(new AppState { Friends = [separateCharacter], SelectedFriendId = separateCharacter.Id })
        && separateCharacter.Name == "小栖" && separateCharacter.Persona == oldDefaultPersona,
        "non-default characters are excluded from the built-in preset migration");
    var customGreeting = OldDefault();
    customGreeting.Greeting = "保留自定义开场白";
    Check(DefaultCharacters.UpgradeLegacyDefault(new AppState { Friends = [customGreeting] })
        && customGreeting.Name == DefaultCharacters.KotoriName && customGreeting.Greeting == "保留自定义开场白",
        "matching legacy persona upgrades without replacing a custom greeting");

    var store = new PortableStore(Path.Combine(testRoot, "portable"));
    store.Initialize();
    Check(Environment.GetEnvironmentVariable("TEMP")!.StartsWith(store.CacheDirectory), "process temp remains inside portable folder");
    var state = store.Load();
    state.Friends[0].Name = "备份角色";
    store.Save(state);
    state.Friends[0].Name = "最新角色";
    state.Friends[0].Messages.Add(new ChatMessage { Role = "assistant", Status = "streaming", Text = "未完成的回答" });
    store.Save(state);
    Check(store.Load().Friends[0].Messages[0].Status == "interrupted", "crashed streaming message recovers visibly");
    File.WriteAllText(Path.Combine(store.DataDirectory, "state.json"), "{broken");
    var recovered = store.Load();
    Check(recovered.Friends[0].Name == "最新角色" && store.RecoveryNotice != null, "corrupt main recovers latest acknowledged save with notice");
    Check(Directory.GetFiles(store.DataDirectory, "state.damaged-*.json").Length == 1, "damaged data retained for manual recovery");
    recovered.Friends[0].Memories.Add(new MemoryItem { Content = "应当忘记的内容", State = "approved" });
    store.Save(recovered);
    recovered.Friends[0].Memories.Clear();
    store.Save(recovered, purgeBackup: true);
    File.WriteAllText(Path.Combine(store.DataDirectory, "state.json"), "{broken-again");
    Check(store.Load().Friends[0].Memories.Count == 0, "deletion cannot reappear from recovery backup");
    var invalidSettings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(store.DataDirectory, "state.json")))!;
    invalidSettings["Settings"]!["Model"] = null;
    File.WriteAllText(Path.Combine(store.DataDirectory, "state.json"), invalidSettings.ToJsonString());
    Check(store.Load().Settings.Model != null && store.RecoveryNotice != null, "structurally invalid nullable settings recover instead of crashing UI");
    var atomic = new PortableStore(Path.Combine(testRoot, "atomic"));
    atomic.Initialize();
    var atomicState = atomic.Load();
    atomicState.Friends[0].Memories.Add(new MemoryItem { Content = "准备删除的记录", State = "approved" });
    atomic.Save(atomicState, purgeBackup: true);
    atomicState.Friends[0].Memories.Clear();
    using (var locked = new FileStream(Path.Combine(atomic.DataDirectory, "state.backup.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        Reject(() => atomic.Save(atomicState, purgeBackup: true), "backup write failure reports failed forget operation");
    Check(atomic.Load().Friends[0].Memories.Count == 1, "failed forget leaves main data unchanged rather than partly committed");
    File.WriteAllText(Path.Combine(atomic.DataDirectory, "state.json"), "broken");
    File.WriteAllText(Path.Combine(atomic.DataDirectory, "state.backup.json"), "broken");
    Reject(() => atomic.Load(), "two corrupt state files never silently reset user data");

    var durable = new PortableStore(Path.Combine(testRoot, "durable"));
    durable.Initialize();
    var stable = durable.Load();
    var fact = new MemoryItem { Content = "我喜欢 Python，也喜欢乌龙茶。", State = "approved", Pinned = true };
    stable.Friends[0].Memories.Add(fact);
    durable.Save(stable);
    string mainPath = Path.Combine(durable.DataDirectory, "state.json");
    string backupPath = Path.Combine(durable.DataDirectory, "state.backup.json");
    Check(File.ReadAllBytes(mainPath).SequenceEqual(File.ReadAllBytes(backupPath)), "first save creates two matching newest snapshots");
    File.WriteAllText(mainPath, "{");
    stable = durable.Load();
    Check(stable.Friends[0].Memories.Single().Id == fact.Id, "first-save memory survives a corrupt main without needing a second save");
    File.WriteAllText(mainPath, "{}");
    Check(durable.Load().Friends[0].Memories.Count == 1, "valid empty JSON recovers instead of resetting memories");
    var wrongVersion = JsonNode.Parse(File.ReadAllText(mainPath))!;
    wrongVersion["Version"] = "invalid";
    File.WriteAllText(mainPath, wrongVersion.ToJsonString());
    Check(durable.Load().Friends[0].Memories.Count == 1, "wrong JSON version type recovers from the valid replica");
    var changed = JsonNode.Parse(File.ReadAllText(mainPath))!;
    changed["Friends"]![0]!["Memories"]![0]!["Content"] = "被意外篡改的内容";
    File.WriteAllText(mainPath, changed.ToJsonString());
    Check(durable.Load().Friends[0].Memories.Single().Content == fact.Content, "checksum detects syntactically valid changed memory");
    var missing = JsonNode.Parse(File.ReadAllText(mainPath))!.AsObject();
    missing.Remove("$storage");
    missing["Friends"]![0]!.AsObject().Remove("Memories");
    File.WriteAllText(mainPath, missing.ToJsonString());
    Check(durable.Load().Friends[0].Memories.Single().Id == fact.Id, "missing memory array is rejected instead of replaced with an empty default");

    // Simulate power loss after the backup commit, before the older main was replaced.
    stable = durable.Load();
    byte[] preDeleteMain = File.ReadAllBytes(mainPath);
    stable.Friends[0].Memories.Clear();
    durable.Save(stable, purgeBackup: true);
    File.WriteAllBytes(mainPath, preDeleteMain);
    Check(durable.Load().Friends[0].Memories.Count == 0, "newer committed backup wins over valid older main after interrupted deletion");
    Check(File.ReadAllBytes(mainPath).SequenceEqual(File.ReadAllBytes(backupPath)), "recovery repairs the lagging main to the latest revision");

    stable = durable.Load();
    stable.Friends[0].Memories.Add(new MemoryItem { Content = "主副本被占用时仍持久保存", State = "approved" });
    using (var readLock = new FileStream(mainPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        durable.Save(stable);
        Check(durable.PersistenceWarning != null && stable.StorageRevision > 1, "backup commit reports success with warning if main replacement is blocked");
    }
    Check(durable.Load().Friends[0].Memories.Count == 1 && durable.PersistenceWarning == null, "degraded save survives reload and repairs after unlock");
    using (var exclusive = new FileStream(mainPath, FileMode.Open, FileAccess.Read, FileShare.None))
        Reject(() => durable.Load(), "unreadable main is not mistaken for corruption or silently overwritten");

    var secondStore = new PortableStore(durable.Root);
    var stale = secondStore.Load();
    stable = durable.Load();
    stable.Friends[0].Memories.Add(new MemoryItem { Content = "并发写入新增的记忆", State = "approved" });
    durable.Save(stable);
    stale.Friends[0].Memories.Clear();
    Reject(() => secondStore.Save(stale), "stale loaded state cannot overwrite a newer committed memory");
    Reject(() => durable.Save(new AppState()), "new default state cannot replace existing memories");
    Check(durable.Load().Friends[0].Memories.Count == 2, "rejected stale write leaves both latest memories intact");
    using (var lease = new FileStream(Path.Combine(durable.DataDirectory, ".store.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Reject(() => secondStore.Load(), "cross-instance operation lock prevents concurrent read-repair/write races");

    var legacy = new PortableStore(Path.Combine(testRoot, "legacy"));
    legacy.Initialize();
    var legacyState = new AppState();
    legacyState.Friends[0].Memories.Add(new MemoryItem { Content = "旧版角色记忆", State = "approved", SourceMessageId = "old-source" });
    string legacyMain = Path.Combine(legacy.DataDirectory, "state.json");
    File.WriteAllText(legacyMain, JsonSerializer.Serialize(legacyState));
    var upgraded = legacy.Load();
    Check(upgraded.StorageRevision == 0 && upgraded.Friends[0].Memories.Single().Content == "旧版角色记忆", "complete legacy v1 loads without changing its memories");
    legacy.Save(upgraded);
    Check(upgraded.StorageRevision == 1 && File.ReadAllText(legacyMain).Contains("$storage") && legacy.Load().Friends[0].Memories[0].SourceMessageId == "old-source", "legacy save upgrades integrity metadata while preserving provenance");

    var conflictStore = new PortableStore(Path.Combine(testRoot, "conflict"));
    conflictStore.Initialize();
    var conflicting = conflictStore.Load();
    conflicting.Friends[0].Name = "不同内容的相同修订";
    conflictStore.Save(conflicting);
    string legacyBackup = Path.Combine(legacy.DataDirectory, "state.backup.json");
    byte[] goodLegacyBackup = File.ReadAllBytes(legacyBackup);
    File.Copy(Path.Combine(conflictStore.DataDirectory, "state.json"), legacyBackup, true);
    Reject(() => legacy.Load(), "same revision with different valid checksums stops rather than arbitrarily choosing a copy");
    File.WriteAllBytes(legacyBackup, goodLegacyBackup);
    var future = JsonNode.Parse(File.ReadAllText(legacyBackup))!;
    future["$storage"]!["Format"] = 999;
    File.WriteAllText(legacyBackup, future.ToJsonString());
    byte[] futureBytes = File.ReadAllBytes(legacyBackup);
    Reject(() => legacy.Load(), "future snapshot format is not silently replaced with an older compatible copy");
    Check(File.ReadAllBytes(legacyBackup).SequenceEqual(futureBytes), "future-version file remains untouched");

    var repeated = durable.Load();
    string[] memoryIds = repeated.Friends[0].Memories.Select(m => m.Id).ToArray();
    for (int cycle = 0; cycle < 200; cycle++)
    {
        var reopened = new PortableStore(durable.Root);
        repeated = reopened.Load();
        repeated.Settings.ContextMessages = 2 + cycle % 100;
        reopened.Save(repeated);
    }
    var afterCycles = new PortableStore(durable.Root).Load();
    Check(afterCycles.Friends[0].Memories.Select(m => m.Id).SequenceEqual(memoryIds) && afterCycles.Friends[0].Memories.All(m => m.State == "approved"), "200 reopen-and-save cycles preserve approved memory identity and state");
    var emptyStore = new PortableStore(Path.Combine(testRoot, "empty-json"));
    emptyStore.Initialize();
    File.WriteAllText(Path.Combine(emptyStore.DataDirectory, "state.json"), "{}");
    Reject(() => emptyStore.Load(), "empty JSON without a recovery copy never becomes a new blank profile");

    var outside = Path.Combine(testRoot, "outside");
    Directory.CreateDirectory(outside);
    var sentinel = Path.Combine(outside, "keep.txt");
    File.WriteAllText(sentinel, "must survive");
    var protectedData = Path.Combine(store.DataDirectory, "keep.txt");
    File.WriteAllText(protectedData, "permanent");
    File.WriteAllText(Path.Combine(store.CacheDirectory, "old.wav"), "temporary");
    store.CleanupCache(recovered.Settings, all: true);
    Check(File.Exists(sentinel) && File.Exists(protectedData), "cache clear preserves user data and outside files");
    Check(!File.Exists(Path.Combine(store.CacheDirectory, "old.wav")), "cache clear removes generated files");
    Check(store.ResolveAsset("../../outside/keep.txt") == null && store.ResolveAsset(sentinel) == null, "asset resolver rejects absolute and traversal paths");
    Reject(() => store.ImportAsset(sentinel), "asset import rejects non-image extensions");
    try
    {
        var link = Path.Combine(store.CacheDirectory, "external-link");
        Directory.CreateSymbolicLink(link, outside);
        store.CleanupCache(recovered.Settings, all: true);
        Check(File.Exists(sentinel), "cache walker does not follow symbolic links");
        Directory.Delete(link);
    }
    catch (Exception e) when (e is UnauthorizedAccessException or IOException)
    { Console.WriteLine("SKIP: symbolic link creation unavailable for this Windows user"); }
    var friend = new FriendProfile();
    var pending = new MemoryItem { Content = "我喜欢咖啡", State = "pending" };
    var approved = new MemoryItem { Content = "我喜欢咖啡", State = "approved" };
    var expired = new MemoryItem { Content = "咖啡要加奶", State = "approved", ExpiresAt = DateTimeOffset.Now.AddDays(-1), Pinned = true };
    var archived = new MemoryItem { Content = "咖啡讨厌糖", State = "archived", Pinned = true };
    friend.Memories.AddRange([pending, approved, expired, archived]);
    var result = MemoryEngine.Retrieve(friend, "今天想喝咖啡", 1000);
    Check(result.Count == 1 && result[0] == approved, "retrieval excludes pending archived and expired memory");
    Check(MemoryEngine.Retrieve(new FriendProfile(), "咖啡", 1000).Count == 0, "memories isolated per friend");
    Check(MemoryEngine.Retrieve(friend, "咖啡", 3).Count == 0, "memory character budget enforced");
    Check(approved.LastUsedAt != null && pending.LastUsedAt == null, "usage timestamp only records retrieved memory");
    var mixed = new FriendProfile();
    mixed.Memories.Add(new MemoryItem { Content = "我喜欢Python", State = "approved" });
    Check(MemoryEngine.Retrieve(mixed, "Python", 1000).Count == 1, "Chinese prefix does not prevent Latin keyword memory recall");
    mixed.Memories[0].Content = "Python语言";
    Check(MemoryEngine.Retrieve(mixed, "Python", 1000).Count == 1, "Chinese suffix does not prevent Latin keyword memory recall");
    var message = new ChatMessage { Text = "请记住：我的生日是五月一日" };
    var suggested = MemoryEngine.SuggestFromMessage(friend, message);
    Check(suggested?.State == "pending" && suggested.SourceMessageId == message.Id, "explicit request creates reviewable sourced candidate");
    Check(MemoryEngine.SuggestFromMessage(friend, message) == null, "memory candidates deduplicate");
    Check(MemoryEngine.SuggestFromMessage(friend, new ChatMessage { Text = "不要记住我的生日" }) == null, "negated request never creates memory");
    Check(MemoryEngine.SuggestFromMessage(friend, new ChatMessage { Role = "assistant", Text = "记住用户喜欢咖啡" }) == null, "assistant claims never become factual memory");
    var export = Path.Combine(testRoot, "friend.json");
    store.Export(friend, export);
    var imported = store.ImportFriend(export);
    Check(imported.Id != friend.Id && imported.Memories.Count == friend.Memories.Count, "profile import gets new identity and keeps structured memory");
    Check(!File.ReadAllText(export).Contains("ProtectedApiKey"), "profile export contains no credential settings");
    var unrelated = Path.Combine(testRoot, "unrelated.json");
    File.WriteAllText(unrelated, "{}");
    Reject(() => store.ImportFriend(unrelated), "unrelated JSON cannot silently become a default friend");
    Reject(() => store.Export(friend, Path.Combine(store.DataDirectory, "state.json")), "export cannot overwrite internal state");
    var programFile = Path.Combine(store.Root, "Qiban.exe");
    File.WriteAllText(programFile, "program-sentinel");
    Reject(() => store.Export(friend, programFile), "export cannot overwrite executable");
    var runtimeConfig = Path.Combine(store.Root, "Qiban.runtimeconfig.json");
    File.WriteAllText(runtimeConfig, "{}");
    Reject(() => store.Export(friend, runtimeConfig), "export cannot overwrite existing app JSON configuration");
    Check(File.ReadAllText(programFile) == "program-sentinel" && File.ReadAllText(runtimeConfig) == "{}", "protected app files unchanged after rejected export");
    var localExport = Path.Combine(store.Root, "朋友备份.json");
    store.Export(friend, localExport);
    friend.Name = "已更新的朋友";
    store.Export(friend, localExport);
    Check(store.ImportFriend(localExport).Name == friend.Name, "user can replace own previous portable export");
    if (OperatingSystem.IsWindows())
    {
        const string dummy = "test-only-not-a-real-api-key";
        var encrypted = SecretVault.Protect(dummy);
        Check(!encrypted.Contains(dummy) && SecretVault.Unprotect(encrypted) == dummy, "DPAPI key encryption round-trips without plaintext storage");
    }
    Console.WriteLine($"All {passed} assertions passed.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
finally
{
    // The only deletion target is the exact fresh child directory allocated by this run.
    var resolved = Path.GetFullPath(testRoot);
    if (resolved == testRoot && Path.GetFileName(resolved).Length == 32 && Directory.Exists(resolved))
        Directory.Delete(resolved, recursive: true);
}
