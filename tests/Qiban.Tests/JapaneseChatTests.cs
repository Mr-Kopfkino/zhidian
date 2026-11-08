using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qiban.Core;

internal static class JapaneseChatTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static void Run(string testRoot, Action<bool, string> check)
    {
        var legacy = new AppState();
        var user = new ChatMessage { Text = "记住：我喜欢乌龙茶 / I prefer tea." };
        legacy.Friends[0].Messages.Add(user);
        legacy.Friends[0].Messages.Add(new ChatMessage { Role = "assistant", Text = "我记住了。" });
        legacy.Friends[0].Memories.Add(new MemoryItem
        {
            Content = "我喜欢乌龙茶", State = "approved", SourceMessageId = user.Id, Pinned = true
        });
        legacy.Friends.Add(new FriendProfile { Id = "second", Voice = new VoiceSettings { Model = "second-model" } });
        var legacyNode = JsonSerializer.SerializeToNode(legacy)!.AsObject();
        RemoveJapaneseFields(legacyNode);
        var metadata = new JsonObject { ["Format"] = 1, ["Revision"] = 9 };
        legacyNode["$storage"] = metadata;
        metadata["Sha256"] = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacyNode, Json)));
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(legacyNode, Json);
        var store = NewStore("japanese-legacy");
        string main = Path.Combine(store.DataDirectory, "state.json");
        string backup = Path.Combine(store.DataDirectory, "state.backup.json");
        File.WriteAllBytes(main, original); File.WriteAllBytes(backup, original);
        var state = store.Load();
        check(state.StorageRevision == 9 && !state.Friends[0].JapaneseChat.Enabled
            && state.Friends[0].JapaneseChat.DisplayLanguage == "简体中文"
            && state.Friends[0].Messages.All(m => m.JapaneseText == "" && m.DisplayLanguage == "")
            && File.ReadAllBytes(main).SequenceEqual(original) && File.ReadAllBytes(backup).SequenceEqual(original),
            "legacy chat snapshots default Japanese mode off without rewriting verified source bytes");
        store.Save(state);
        var upgraded = JsonNode.Parse(File.ReadAllText(main))!.AsObject();
        RemoveJapaneseFields(upgraded);
        upgraded.Remove("$storage"); legacyNode.Remove("$storage");
        check(JsonNode.DeepEquals(upgraded, legacyNode) && File.ReadAllBytes(main).SequenceEqual(File.ReadAllBytes(backup)),
            "Japanese mode migration preserves all previous role, voice, message and memory fields");

        var first = state.Friends[0];
        first.JapaneseChat.Enabled = true;
        first.JapaneseChat.DisplayLanguage = "English";
        var copy = first.JapaneseChat.Copy(); copy.DisplayLanguage = "Español";
        store.Save(state);
        var reloaded = store.Load();
        check(reloaded.Friends[0].JapaneseChat.Enabled && reloaded.Friends[0].JapaneseChat.DisplayLanguage == "English"
            && !reloaded.Friends[1].JapaneseChat.Enabled && reloaded.Friends[1].JapaneseChat.DisplayLanguage == "简体中文"
            && reloaded.Friends[1].Voice.Model == "second-model" && first.JapaneseChat.DisplayLanguage == "English",
            "Japanese mode and display language persist independently for each character and request copy");

        var answer = new ChatMessage
        {
            Role = "assistant", Text = "Let's have some tea.", JapaneseText = "一緒にお茶を飲みましょう。", DisplayLanguage = "English"
        };
        first.Messages.Add(answer);
        store.Save(state);
        var restored = store.Load().Friends[0];
        check(restored.Messages[^1].Text == answer.Text && restored.Messages[^1].JapaneseText == answer.JapaneseText
            && restored.Messages[^1].DisplayLanguage == "English" && restored.Messages[0].Text == user.Text
            && restored.Messages[0].JapaneseText == "" && restored.Memories[0].SourceMessageId == user.Id,
            "Japanese speech original and translated display survive restart without translating user input or memory provenance");
        File.WriteAllText(main, "{broken");
        restored = store.Load().Friends[0];
        check(restored.Messages[^1].JapaneseText == answer.JapaneseText && restored.Messages[^1].Text == answer.Text
            && restored.JapaneseChat.DisplayLanguage == "English" && File.ReadAllBytes(main).SequenceEqual(File.ReadAllBytes(backup)),
            "recovery restores Japanese speech, display text and per-character language as one committed snapshot");

        string export = Path.Combine(testRoot, "japanese-role.json");
        store.Export(restored, export);
        var imported = store.ImportFriend(export);
        check(imported.Id != restored.Id && imported.JapaneseChat.Enabled && imported.JapaneseChat.DisplayLanguage == "English"
            && JsonSerializer.Serialize(imported.Messages) == JsonSerializer.Serialize(restored.Messages)
            && JsonSerializer.Serialize(imported.Memories) == JsonSerializer.Serialize(restored.Memories),
            "role export and import retain Japanese originals, translations, selected language and sourced memories");

        state = store.Load();
        state.Friends[0].Messages.Add(new ChatMessage
        {
            Role = "assistant", JapaneseText = "明日は晴れるかもしれません。", Text = "Tomorrow may", DisplayLanguage = "English", Status = "streaming"
        });
        store.Save(state);
        var interrupted = store.Load().Friends[0].Messages[^1];
        check(interrupted.Status == "interrupted" && interrupted.JapaneseText == "明日は晴れるかもしれません。"
            && interrupted.Text == "Tomorrow may" && interrupted.DisplayLanguage == "English",
            "interrupted display translation preserves its Japanese original and recoverable partial text");

        RejectBroken("null-japanese-mode", root => root["Friends"]![0]!["JapaneseChat"] = null);
        RejectBroken("incomplete-japanese-mode", root => root["Friends"]![0]!["JapaneseChat"]!.AsObject().Remove("DisplayLanguage"));
        RejectBroken("blank-display-language", root => root["Friends"]![0]!["JapaneseChat"]!["DisplayLanguage"] = "  ");
        RejectBroken("oversized-display-language", root => root["Friends"]![0]!["JapaneseChat"]!["DisplayLanguage"] = new string('a', 81));
        RejectBroken("null-japanese-original", root => root["Friends"]![0]!["Messages"]![0]!["JapaneseText"] = null);
        RejectBroken("incomplete-bilingual-message", root => root["Friends"]![0]!["Messages"]![0]!.AsObject().Remove("DisplayLanguage"));
        RejectBroken("oversized-japanese-original", root => root["Friends"]![0]!["Messages"]![0]!["JapaneseText"] = new string('あ', 200001));

        var exported = JsonNode.Parse(File.ReadAllText(export))!;
        exported["JapaneseChat"]!.AsObject().Remove("Enabled");
        File.WriteAllText(export, exported.ToJsonString());
        bool importRejected = false;
        try { store.ImportFriend(export); } catch (InvalidDataException) { importRejected = true; }
        check(importRejected, "import rejects an incomplete Japanese mode instead of silently changing its enabled state");

        var tampered = JsonNode.Parse(File.ReadAllText(main))!;
        var tamperedMessages = tampered["Friends"]![0]!["Messages"]!.AsArray();
        tamperedMessages[tamperedMessages.Count - 1]!["JapaneseText"] = "別の発言です。";
        var tamperedStore = NewStore("japanese-integrity");
        string tamperedPath = Path.Combine(tamperedStore.DataDirectory, "state.json");
        string tamperedJson = tampered.ToJsonString();
        File.WriteAllText(tamperedPath, tamperedJson);
        bool hashRejected = false;
        try { tamperedStore.Load(); } catch (InvalidDataException) { hashRejected = true; }
        check(hashRejected && File.ReadAllText(tamperedPath) == tamperedJson,
            "Japanese original participates in snapshot integrity checks and cannot be silently altered");

        PortableStore NewStore(string name)
        {
            var result = new PortableStore(Path.Combine(testRoot, name)); result.Initialize(); return result;
        }
        void RejectBroken(string name, Action<JsonNode> change)
        {
            var broken = JsonSerializer.SerializeToNode(legacy)!;
            change(broken);
            var brokenStore = NewStore(name);
            string path = Path.Combine(brokenStore.DataDirectory, "state.json");
            string text = broken.ToJsonString(); File.WriteAllText(path, text);
            bool rejected = false;
            try { brokenStore.Load(); } catch (InvalidDataException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == text, name + " is rejected without replacing saved data");
        }
    }

    private static void RemoveJapaneseFields(JsonObject state)
    {
        foreach (var node in state["Friends"]!.AsArray())
        {
            node!.AsObject().Remove("JapaneseChat");
            foreach (var message in node["Messages"]!.AsArray())
            {
                message!.AsObject().Remove("JapaneseText");
                message.AsObject().Remove("DisplayLanguage");
            }
        }
    }
}
