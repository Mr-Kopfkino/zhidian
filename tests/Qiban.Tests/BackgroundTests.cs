using System.Text.Json;
using System.Text.Json.Nodes;
using Qiban.Core;

internal static class BackgroundTests
{
    internal static void Run(string testRoot, Action<bool, string> check)
    {
        var store = new PortableStore(Path.Combine(testRoot, "backgrounds"));
        store.Initialize();
        var state = new AppState();
        var friend = state.Friends[0];
        friend.Messages.Add(new ChatMessage { Text = "背景测试对话" });
        friend.Memories.Add(new MemoryItem { Content = "背景测试记忆", State = "approved" });

        // A genuine 1x1 PNG is enough for storage tests; decoding and rendering belong to WPF tests.
        var source = Path.Combine(testRoot, "background-source.png");
        File.WriteAllBytes(source, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRuoAAAAASUVORK5CYII="));
        string original = store.ImportAsset(source);
        friend.ChatBackgroundPath = original;
        store.Save(state);
        var reloaded = store.Load();
        check(reloaded.Friends[0].ChatBackgroundPath == original && store.ResolveAsset(original) != null,
            "chat background reference and portable image survive save and reload");
        File.WriteAllText(Path.Combine(store.CacheDirectory, "temporary.bin"), "cache");
        store.CleanupCache(state.Settings, all: true);
        store.PruneAssets(state);
        check(store.ResolveAsset(original) != null && !Directory.EnumerateFiles(store.CacheDirectory, "*", SearchOption.AllDirectories).Any(),
            "cache clearing and asset cleanup retain the referenced chat background");

        state.Friends.Add(new FriendProfile { Id = "second", ChatBackgroundPath = original });
        string replacement = store.ImportAsset(source);
        friend.ChatBackgroundPath = replacement;
        store.Save(state);
        store.PruneAssets(state);
        check(store.ResolveAsset(original) != null && store.ResolveAsset(replacement) != null,
            "replacing one background retains images shared by other friends");
        state.Friends[1].ChatBackgroundPath = "";
        store.Save(state);
        store.PruneAssets(state);
        check(store.ResolveAsset(original) == null && store.ResolveAsset(replacement) != null,
            "removing the last background reference releases only its orphaned image");

        // A committed backup may coexist with a lagging main. Its image must remain recoverable.
        var primary = Path.Combine(store.DataDirectory, "state.json");
        using (var lockedMain = new FileStream(primary, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            friend.ChatBackgroundPath = "";
            store.Save(state);
            store.PruneAssets(state);
            check(store.PersistenceWarning != null && store.ResolveAsset(replacement) != null,
                "background assets referenced by a lagging valid snapshot remain protected");
        }
        state = store.Load();
        store.PruneAssets(state);
        check(state.Friends[0].ChatBackgroundPath == "" && store.ResolveAsset(replacement) == null,
            "background removal persists and orphan cleanup resumes after snapshot repair");

        friend = state.Friends[0];
        friend.ChatBackgroundPath = "data/assets/private-background.png";
        var exported = Path.Combine(testRoot, "background-friend.json");
        store.Export(friend, exported);
        var exportNode = JsonNode.Parse(File.ReadAllText(exported))!;
        check(exportNode["ChatBackgroundPath"]!.GetValue<string>() == ""
            && friend.ChatBackgroundPath == "data/assets/private-background.png",
            "role export strips background paths without mutating the selected character");
        exportNode["ChatBackgroundPath"] = "C:\\external\\background.png";
        File.WriteAllText(exported, exportNode.ToJsonString());
        var imported = store.ImportFriend(exported);
        check(imported.ChatBackgroundPath == "" && imported.Messages.Count == 1 && imported.Memories.Count == 1,
            "role import discards foreign background paths while preserving messages and memory");

        var legacyStore = new PortableStore(Path.Combine(testRoot, "background-legacy"));
        legacyStore.Initialize();
        friend.ChatBackgroundPath = "";
        var legacy = JsonSerializer.SerializeToNode(state)!;
        foreach (var profile in legacy["Friends"]!.AsArray()) profile!.AsObject().Remove("ChatBackgroundPath");
        string expectedRecords = JsonSerializer.Serialize(new { friend.Messages, friend.Memories });
        File.WriteAllText(Path.Combine(legacyStore.DataDirectory, "state.json"), legacy.ToJsonString());
        var loadedLegacy = legacyStore.Load();
        legacyStore.Save(loadedLegacy);
        var upgraded = legacyStore.Load().Friends[0];
        check(upgraded.ChatBackgroundPath == "" && JsonSerializer.Serialize(new { upgraded.Messages, upgraded.Memories }) == expectedRecords,
            "legacy snapshots without background fields retain all conversations and long-term memories");
    }
}
