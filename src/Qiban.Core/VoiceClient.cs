using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qiban.Core;

/// <summary>Serializes character voice generation through a bundled engine or a configured speech service.</summary>
public sealed class VoiceClient : IDisposable
{
    private const int MaximumAudioBytes = 32 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly EmbeddedVoiceEngine embedded;
    private readonly string applicationRoot;
    private int disposed;
    public VoiceClient() : this(NetworkContract.CreateHandler()) { }
    public VoiceClient(string applicationRoot) : this(NetworkContract.CreateHandler(), applicationRoot) { }
    public VoiceClient(HttpMessageHandler handler, string? applicationRoot = null)
        : this(handler, applicationRoot ?? AppContext.BaseDirectory, null) { }
    internal VoiceClient(HttpMessageHandler handler, string applicationRoot, Func<string, string, System.Diagnostics.ProcessStartInfo>? testStart, Action? beforePublish = null)
    {
        http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        this.applicationRoot = Path.GetFullPath(applicationRoot);
        embedded = new(this.applicationRoot, testStart, beforePublish);
    }
    public bool IsEmbeddedRunning => embedded.IsRunning;
    public void StopEmbedded() => embedded.Stop();
    private static bool IsExternalGpt(string provider) => provider is "GPT-SoVITS" or "GPT-SoVITS 外接";

    public async Task<string> SynthesizeAsync(VoiceSettings settings, string text, string cacheDirectory, CancellationToken ct, bool japanese = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Copy();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16_000) throw new InvalidDataException("朗读文本须为 1 至 16000 字符。");
        if (japanese) JapaneseChat.EnsureJapanese(text);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token, deadline.Token);
        using var mounting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, deadline.Token);
        string? temporary = null;
        bool acquired = false;
        try
        {
            if (settings.Provider == "GPT-SoVITS 内置")
            {
                string embeddedDirectory = Path.GetFullPath(cacheDirectory);
                string boundary = Path.Combine(applicationRoot, "cache") + Path.DirectorySeparatorChar;
                if (!embeddedDirectory.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
                    && embeddedDirectory != Path.Combine(applicationRoot, "cache"))
                    throw new IOException("内置语音缓存必须位于程序 cache 目录。");
                RejectLinks(embeddedDirectory); Directory.CreateDirectory(embeddedDirectory);
                await operations.WaitAsync(timeout.Token).ConfigureAwait(false);
                acquired = true;
                string embeddedDestination = Path.Combine(embeddedDirectory, "voice-" + Guid.NewGuid().ToString("N") + ".wav");
                temporary = await embedded.ExecuteAsync(settings, text, japanese, embeddedDestination, timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                temporary = null;
                return embeddedDestination;
            }
            var (endpoint, body) = BuildRequest(settings, text, japanese);
            Uri? mountEndpoint = null;
            if (IsExternalGpt(settings.Provider))
            {
                bool gpt = !string.IsNullOrWhiteSpace(settings.GptWeights), sovits = !string.IsNullOrWhiteSpace(settings.SovitsWeights);
                if (gpt != sovits) throw new InvalidDataException("请同时填写 GPT 与 SoVITS 权重，或同时留空以使用独立服务的预载模型。");
                if (gpt) mountEndpoint = ValidateMount(settings);
            }
            // GPT-SoVITS changes server-wide weights. Keep the complete pair and audio download
            // in one transaction relative to all synthesis/manual mount calls on this client.
            await operations.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            if (mountEndpoint != null) await MountPairAsync(settings, mountEndpoint, timeout.Token, mounting.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            using var request = NetworkContract.Request(HttpMethod.Post, endpoint, settings.ProtectedApiKey, body);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            NetworkContract.EnsureSuccess(response, "语音服务");
            if (response.Content.Headers.ContentLength > MaximumAudioBytes) throw new InvalidDataException("语音文件超过 32 MB 限制。");
            var directory = Path.GetFullPath(cacheDirectory); RejectLinks(directory); Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, "voice-" + Guid.NewGuid().ToString("N") + ".part");
            string extension;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                byte[] buffer = new byte[81920]; int total = 0, read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    total += read;
                    if (total > MaximumAudioBytes) throw new InvalidDataException("语音文件超过 32 MB 限制。");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                output.Position = 0; byte[] header = new byte[12];
                int count = await output.ReadAsync(header, timeout.Token).ConfigureAwait(false);
                extension = DetectFormat(header.AsSpan(0, count), total);
            }
            timeout.Token.ThrowIfCancellationRequested();
            string destination = Path.ChangeExtension(temporary, extension);
            File.Move(temporary, destination); temporary = null;
            return destination;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetime.IsCancellationRequested) { throw new TimeoutException("语音合成或模型切换超过 300 秒未完成，请检查语音服务。"); }
        catch (HttpRequestException) { throw new IOException("无法连接语音服务，请检查服务是否启动及 API 地址。"); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (acquired) operations.Release();
        }
    }

    public async Task MountAsync(VoiceSettings settings, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Copy();
        var endpoint = settings.Provider == "GPT-SoVITS 内置" ? null : ValidateMount(settings);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token, deadline.Token);
        using var mounting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, deadline.Token);
        bool acquired = false;
        try
        {
            await operations.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            if (endpoint == null) await embedded.ExecuteAsync(settings, null, false, null, timeout.Token).ConfigureAwait(false);
            else await MountPairAsync(settings, endpoint, timeout.Token, mounting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new TimeoutException("挂载超时；服务可能已加载部分权重，请核对服务状态后重新挂载完整组合。"); }
        finally { if (acquired) operations.Release(); }
    }

    private static Uri ValidateMount(VoiceSettings settings)
    {
        if (!IsExternalGpt(settings.Provider)) throw new InvalidOperationException("权重挂载仅适用于 GPT-SoVITS。");
        var endpoint = NetworkContract.Endpoint(settings.Endpoint, "/tts");
        if (!endpoint.IsLoopback) throw new InvalidOperationException("只能向本机 GPT-SoVITS 服务挂载权重。");
        if (string.IsNullOrWhiteSpace(settings.GptWeights) || string.IsNullOrWhiteSpace(settings.SovitsWeights))
            throw new InvalidDataException("请同时填写 GPT (.ckpt) 与 SoVITS (.pth) 权重的服务端路径。");
        if (settings.GptWeights.Length > 4096 || settings.SovitsWeights.Length > 4096)
            throw new InvalidDataException("权重路径过长。");
        return endpoint;
    }

    private async Task MountPairAsync(VoiceSettings settings, Uri endpoint, CancellationToken callerToken, CancellationToken requestToken)
    {
        bool gptLoaded = false;
        try
        {
            var root = endpoint.AbsoluteUri[..^"/tts".Length];
            // Aborting HTTP does not prove that the server stopped loading weights. Once a load
            // starts, retain the gate until it settles (or the finite deadline/shutdown fires).
            // A role switch cancels the next step, not the in-flight server mutation.
            callerToken.ThrowIfCancellationRequested();
            await MountOne(root + "/set_gpt_weights", settings.GptWeights, settings.ProtectedApiKey, requestToken).ConfigureAwait(false);
            gptLoaded = true;
            callerToken.ThrowIfCancellationRequested();
            await MountOne(root + "/set_sovits_weights", settings.SovitsWeights, settings.ProtectedApiKey, requestToken).ConfigureAwait(false);
            callerToken.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (callerToken.IsCancellationRequested && (error is IOException or HttpRequestException or JsonException))
        { throw new OperationCanceledException(callerToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or HttpRequestException or JsonException)
        {
            throw new IOException(gptLoaded ? "GPT 权重已加载，但 SoVITS 挂载失败。请检查路径并重新挂载完整组合。" : "权重挂载失败，请确认服务已启动、文件路径对服务可见且模型版本兼容。");
        }
    }

    private async Task MountOne(string url, string path, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = NetworkContract.Request(HttpMethod.Get, new Uri(url + "?weights_path=" + Uri.EscapeDataString(path)), key);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        NetworkContract.EnsureSuccess(response, "权重加载服务");
        string result = await NetworkContract.ReadText(response.Content, 16_384, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result);
        if (!(document.RootElement.ValueKind == JsonValueKind.String && document.RootElement.GetString() == "success" ||
            document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String && message.GetString() == "success"))
            throw new InvalidDataException("语音服务未确认权重加载成功。");
    }

    private static (Uri Endpoint, object Body) BuildRequest(VoiceSettings settings, string text, bool japanese)
    {
        switch (settings.Provider)
        {
            case "GPT-SoVITS":
            case "GPT-SoVITS 外接":
                if (string.IsNullOrWhiteSpace(settings.ReferenceAudio)) throw new InvalidDataException("GPT-SoVITS 需要填写服务端可读取的参考音频路径。");
                return (NetworkContract.Endpoint(settings.Endpoint, "/tts"), new
                {
                    text, text_lang = japanese ? "ja" : settings.Language, ref_audio_path = settings.ReferenceAudio,
                    prompt_text = settings.ReferenceText, prompt_lang = settings.Language,
                    media_type = "wav", streaming_mode = false
                });
            case "OpenAI-compatible":
            case "OpenAI 兼容":
                return (NetworkContract.Endpoint(settings.Endpoint, "/audio/speech"), new
                {
                    model = settings.Model, input = text, voice = settings.Voice, response_format = "wav"
                });
            case "Custom":
            case "Custom JSON":
            case "自定义 JSON":
                if (settings.RequestTemplate.Length > 65_536) throw new InvalidDataException("自定义请求模板过长。");
                JsonNode? node;
                try { node = JsonNode.Parse(settings.RequestTemplate, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException) { throw new InvalidDataException("自定义语音请求模板不是有效 JSON。"); }
                if (node is not JsonObject) throw new InvalidDataException("自定义语音模板必须是 JSON 对象。");
                bool replaced = false, languageReplaced = false;
                ReplaceText(node, text, japanese ? "ja" : settings.Language, ref replaced, ref languageReplaced);
                if (!replaced) throw new InvalidDataException("语音模板须在字符串值中包含 {{text}} 占位符。");
                if (japanese && !languageReplaced)
                    throw new InvalidDataException("日语模式的自定义语音模板须将服务的语言参数设为 {{language}}，其值固定为 ja。");
                return (NetworkContract.Endpoint(settings.Endpoint), node);
            default: throw new InvalidDataException("未知的语音服务类型。");
        }
    }

    private static void ReplaceText(JsonNode node, string text, string language, ref bool replaced, ref bool languageReplaced)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                var previous = obj[key]; var next = ReplaceValue(previous, text, language, ref replaced, ref languageReplaced);
                if (!ReferenceEquals(previous, next)) obj[key] = next;
            }
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
            {
                var previous = array[i]; var next = ReplaceValue(previous, text, language, ref replaced, ref languageReplaced);
                if (!ReferenceEquals(previous, next)) array[i] = next;
            }
    }
    private static JsonNode? ReplaceValue(JsonNode? value, string text, string language, ref bool replaced, ref bool languageReplaced)
    {
        if (value is JsonValue primitive && primitive.TryGetValue<string>(out var str))
        {
            bool hasText = str.Contains("{{text}}", StringComparison.Ordinal);
            bool hasLanguage = str.Contains("{{language}}", StringComparison.Ordinal);
            replaced |= hasText; languageReplaced |= hasLanguage;
            if (hasText || hasLanguage)
                return JsonValue.Create(str.Replace("{{language}}", language, StringComparison.Ordinal).Replace("{{text}}", text, StringComparison.Ordinal));
        }
        if (value is not null) ReplaceText(value, text, language, ref replaced, ref languageReplaced);
        return value;
    }

    private static string DetectFormat(ReadOnlySpan<byte> header, int length)
    {
        if (length >= 44 && header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WAVE"u8)) return ".wav";
        if (length >= 16 && header.Length >= 3 && (header[..3].SequenceEqual("ID3"u8) ||
            header[0] == 0xff && (header[1] & 0xe0) == 0xe0 && (header[1] & 0x06) != 0 && (header[2] & 0xf0) is not (0 or 0xf0))) return ".mp3";
        throw new InvalidDataException("语音服务没有返回有效的 WAV 或 MP3 音频，请检查协议和输出格式。");
    }

    private static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("语音缓存目录不能使用符号链接或目录联接。");
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        embedded.Dispose();
        http.Dispose();
        // Active awaiters still need the gate/token source; neither allocates an OS wait handle here.
        // Let their managed lifetime end after pending operations release their references.
    }
}
