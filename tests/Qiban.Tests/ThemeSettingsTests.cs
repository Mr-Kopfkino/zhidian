using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qiban.Core;

internal static class ThemeSettingsTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static void Run(string testRoot, Action<bool, string> check)
    {
        check(new AppSettings().InterfaceTheme == "standard", "new installations use the standard interface theme");
        var store = new PortableStore(Path.Combine(testRoot, "theme-settings"));
        store.Initialize();
        var legacy = new AppState();
        legacy.Settings.Model = "preserved-chat-model";
        legacy.Friends[0].Voice.Model = "preserved-voice-model";
        legacy.Friends[0].ChatBackgroundPath = "data/assets/background.png";
        var message = new ChatMessage { Text = "保存长期记忆的来源" };
        legacy.Friends[0].Messages.Add(message);
        legacy.Friends[0].Memories.Add(new MemoryItem
        {
            Content = "需要保留的记忆", State = "approved", Pinned = true, SourceMessageId = message.Id
        });
        var oldRoot = JsonSerializer.SerializeToNode(legacy)!.AsObject();
        oldRoot["Settings"]!.AsObject().Remove("InterfaceTheme");
        var metadata = new JsonObject { ["Format"] = 1, ["Revision"] = 12 };
        oldRoot["$storage"] = metadata;
        metadata["Sha256"] = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(oldRoot, Json)));
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(oldRoot, Json);
        string main = Path.Combine(store.DataDirectory, "state.json");
        string backup = Path.Combine(store.DataDirectory, "state.backup.json");
        File.WriteAllBytes(main, original);
        File.WriteAllBytes(backup, original);
        var state = store.Load();
        check(state.Settings.InterfaceTheme == "standard" && state.StorageRevision == 12
            && state.StorageToken == metadata["Sha256"]!.GetValue<string>()
            && File.ReadAllBytes(main).SequenceEqual(original) && File.ReadAllBytes(backup).SequenceEqual(original),
            "legacy theme defaults preserve verified snapshot bytes and revision until the next save");

        state.Settings.InterfaceTheme = "xp";
        store.Save(state);
        state = store.Load();
        check(state.Settings.InterfaceTheme == "xp" && state.StorageRevision == 13
            && File.ReadAllBytes(main).SequenceEqual(File.ReadAllBytes(backup)),
            "XP theme persists across restart in both atomic storage replicas");
        var savedRoot = JsonNode.Parse(File.ReadAllText(main))!.AsObject();
        savedRoot.Remove("$storage");
        savedRoot["Settings"]!.AsObject().Remove("InterfaceTheme");
        oldRoot.Remove("$storage");
        check(JsonNode.DeepEquals(savedRoot, oldRoot),
            "saving a theme preserves every prior connection, role, voice, background, chat and memory field");

        File.WriteAllText(main, "{broken");
        state = store.Load();
        check(state.Settings.InterfaceTheme == "xp" && state.Friends[0].Memories[0].SourceMessageId == message.Id
            && File.ReadAllBytes(main).SequenceEqual(File.ReadAllBytes(backup)),
            "backup recovery restores the selected theme together with sourced long-term memories");
        state.Settings.InterfaceTheme = "standard";
        store.Save(state);
        check(store.Load().Settings.InterfaceTheme == "standard", "switching back to standard theme persists across restart");

        foreach (string? unsupported in new string?[] { "unknown-future-theme", "", null })
        {
            var source = JsonSerializer.SerializeToNode(legacy)!.AsObject();
            source["Settings"]!["InterfaceTheme"] = unsupported;
            string sourceText = source.ToJsonString();
            var fallbackStore = new PortableStore(Path.Combine(testRoot, "theme-fallback-" + Guid.NewGuid().ToString("N")));
            fallbackStore.Initialize();
            string sourcePath = Path.Combine(fallbackStore.DataDirectory, "state.json");
            File.WriteAllText(sourcePath, sourceText);
            var fallback = fallbackStore.Load();
            check(fallback.Settings.InterfaceTheme == "standard" && fallback.Friends[0].Memories[0].SourceMessageId == message.Id
                && File.ReadAllText(sourcePath) == sourceText,
                $"unsupported theme ({unsupported ?? "null"}) uses standard without discarding user data");
        }

        var tampered = JsonNode.Parse(File.ReadAllText(main))!;
        tampered["Settings"]!["InterfaceTheme"] = "xp";
        File.WriteAllText(main, tampered.ToJsonString());
        check(store.Load().Settings.InterfaceTheme == "standard" && File.ReadAllBytes(main).SequenceEqual(File.ReadAllBytes(backup)),
            "theme tampering is detected before normalization and repaired from the verified replica");
    }
}
