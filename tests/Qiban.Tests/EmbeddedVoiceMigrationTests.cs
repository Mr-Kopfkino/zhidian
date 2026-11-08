using System.Text.Json;
using System.Text.Json.Nodes;
using Qiban.Core;

internal static class EmbeddedVoiceMigrationTests
{
    internal static void Run(string testRoot, Action<bool, string> check)
    {
        check(new VoiceSettings().Provider == "GPT-SoVITS 内置", "new roles select the embedded engine without an external service setup");
        var root = Path.Combine(testRoot, "embedded-voice-migration");
        var store = new PortableStore(root); store.Initialize();
        var state = new AppState();
        state.Friends.Clear();
        string[] addresses = ["http://127.0.0.1:9880/tts", "http://localhost:9880", "http://[::1]:9880/tts/",
            "http://127.0.0.1:19880/tts", "https://voice.example/tts", "http://127.0.0.1:9880/tts?custom=1"];
        for (int i = 0; i < addresses.Length; i++)
        {
            state.Friends.Add(new FriendProfile
            {
                Id = "embedded-migration-" + i,
                Voice = new VoiceSettings { Provider = "GPT-SoVITS", Endpoint = addresses[i], Enabled = true,
                    GptWeights = "D:/voices/role-" + i + ".ckpt", SovitsWeights = "D:/voices/role-" + i + ".pth",
                    ReferenceAudio = "D:/voices/role-" + i + ".wav", ReferenceText = "角色参考文本", Language = "ja",
                    ProtectedApiKey = "encrypted-sentinel", RequestTemplate = "{\"text\":\"{{text}}\",\"preserved\":true}" },
                Messages = [new() { Role = "user", Text = "保留聊天原文" }],
                Memories = [new() { Content = "保留已确认的长期记忆", State = "approved" }]
            });
        }
        state.Friends.Add(new FriendProfile { Id = "explicit-external", Voice = new() { Provider = "GPT-SoVITS 外接" } });
        state.Friends.Add(new FriendProfile { Id = "cloud", Voice = new() { Provider = "OpenAI-compatible" } });
        state.SelectedFriendId = state.Friends[0].Id;
        store.Save(state);
        string main = Path.Combine(store.DataDirectory, "state.json"), backup = Path.Combine(store.DataDirectory, "state.backup.json");
        byte[] original = File.ReadAllBytes(main);
        long revision = state.StorageRevision;
        string? token = state.StorageToken;
        foreach (var friend in state.Friends.Take(3)) friend.Voice.Provider = "GPT-SoVITS 内置";
        string expected = JsonSerializer.Serialize(state);
        var migrated = store.Load();
        check(migrated.Friends.Take(3).All(friend => friend.Voice.Provider == "GPT-SoVITS 内置")
            && migrated.Friends.Skip(3).Take(3).All(friend => friend.Voice.Provider == "GPT-SoVITS"),
            "only the previous standard loopback service becomes embedded across IPv4 localhost and IPv6");
        check(migrated.Friends[6].Voice.Provider == "GPT-SoVITS 外接" && migrated.Friends[7].Voice.Provider == "OpenAI-compatible",
            "explicit external and compatible providers retain their selected backend");
        check(JsonSerializer.Serialize(migrated) == expected,
            "embedded migration preserves all role weights references secrets chat and memory except the intended provider choice");
        check(migrated.StorageRevision == revision && migrated.StorageToken == token
            && File.ReadAllBytes(main).SequenceEqual(original) && File.ReadAllBytes(backup).SequenceEqual(original),
            "embedded migration retains verified bytes and revision until an ordinary atomic save");
        File.WriteAllText(main, "{damaged");
        migrated = store.Load();
        check(File.ReadAllBytes(main).SequenceEqual(original) && migrated.Friends[0].Voice.Provider == "GPT-SoVITS 内置",
            "recovery repairs original verified representation before applying in-memory embedded migration");
        store.Save(migrated);
        check(store.Load().StorageRevision == revision + 1 && JsonSerializer.Serialize(store.Load()) == expected
            && File.ReadAllBytes(main).SequenceEqual(File.ReadAllBytes(backup)),
            "the first post-migration save durably commits the embedded choice in matching snapshots");
        migrated = store.Load(); migrated.Friends[0].Voice.Provider = "GPT-SoVITS 外接"; store.Save(migrated);
        check(store.Load().Friends[0].Voice.Provider == "GPT-SoVITS 外接",
            "a user selecting the default-address external provider is not migrated again on restart");

        var invalidRoot = Path.Combine(testRoot, "embedded-voice-tampered");
        var invalid = new PortableStore(invalidRoot); invalid.Initialize();
        var document = JsonNode.Parse(original)!;
        document["Friends"]![0]!["Voice"]!["GptWeights"] = "D:/tampered.ckpt";
        string invalidPath = Path.Combine(invalid.DataDirectory, "state.json");
        string content = document.ToJsonString(); File.WriteAllText(invalidPath, content);
        bool refused = false;
        try { invalid.Load(); } catch (InvalidDataException) { refused = true; }
        check(refused && File.ReadAllText(invalidPath) == content,
            "embedded migration never legitimizes a tampered weight path or replaces invalid stored data");
    }
}
