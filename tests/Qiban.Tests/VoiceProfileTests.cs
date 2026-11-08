using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qiban.Core;

internal static class VoiceProfileTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static void Run(string testRoot, Action<bool, string> check)
    {
        var oldVoice = new VoiceSettings
        {
            Enabled = true, AutoPlay = true, Provider = "Custom", Endpoint = "https://voice.example/v1/speech",
            ProtectedApiKey = "legacy-encrypted-key-sentinel", Model = "old-model", Voice = "old-speaker", Language = "ja",
            ReferenceAudio = "D:/voices/old.wav", ReferenceText = "旧参考文本", GptWeights = "D:/voices/old.ckpt",
            SovitsWeights = "D:/voices/old.pth", RequestTemplate = "{\"text\":\"{{text}}\",\"token\":\"inline-secret-sentinel\"}"
        };
        var state = new AppState();
        state.Friends[0].Messages.Add(new ChatMessage { Text = "保留原始对话" });
        state.Friends[0].Memories.Add(new MemoryItem { Content = "保留长期记忆", State = "approved" });
        state.Friends.Add(new FriendProfile { Id = "second" });
        state.Friends.Add(new FriendProfile { Id = "already-custom", Voice = new VoiceSettings
            { Provider = "OpenAI-compatible", Model = "custom-model", Voice = "custom-speaker", ProtectedApiKey = "custom-key" } });
        string records = JsonSerializer.Serialize(state.Friends.Select(f => new { f.Id, f.Messages, f.Memories }));
        var legacyRoot = JsonSerializer.SerializeToNode(state)!.AsObject();
        legacyRoot["Settings"]!["Voice"] = JsonSerializer.SerializeToNode(oldVoice);
        legacyRoot["Friends"]![0]!.AsObject().Remove("Voice");
        legacyRoot["Friends"]![1]!.AsObject().Remove("Voice");
        var metadata = new JsonObject { ["Format"] = 1, ["Revision"] = 7 };
        legacyRoot["$storage"] = metadata;
        metadata["Sha256"] = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacyRoot, Json)));
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(legacyRoot, Json);
        var store = NewStore("voice-migration");
        string main = Path.Combine(store.DataDirectory, "state.json"), backup = Path.Combine(store.DataDirectory, "state.backup.json");
        File.WriteAllBytes(main, original); File.WriteAllBytes(backup, original);

        state = store.Load();
        check(state.StorageRevision == 7 && state.StorageToken == metadata["Sha256"]!.GetValue<string>()
            && File.ReadAllBytes(main).SequenceEqual(original) && File.ReadAllBytes(backup).SequenceEqual(original),
            "voice migration retains original verified snapshot bytes and optimistic storage token until save");
        check(JsonSerializer.Serialize(state.Friends[0].Voice) == JsonSerializer.Serialize(oldVoice)
            && JsonSerializer.Serialize(state.Friends[1].Voice) == JsonSerializer.Serialize(oldVoice)
            && !ReferenceEquals(state.Friends[0].Voice, state.Friends[1].Voice),
            "legacy global voice is copied intact into independent per-character configurations");
        check(state.Friends[2].Voice.Model == "custom-model" && state.Friends[2].Voice.ProtectedApiKey == "custom-key",
            "legacy migration does not overwrite an existing character voice");
        check(JsonSerializer.Serialize(state.Friends.Select(f => new { f.Id, f.Messages, f.Memories })) == records,
            "voice migration preserves character identities, original chats and long-term memories");

        File.WriteAllText(main, "{broken");
        var repaired = store.Load();
        check(File.ReadAllBytes(main).SequenceEqual(original) && repaired.StorageRevision == 7
            && store.Load().Friends[0].Voice.Voice == "old-speaker",
            "repair of a legacy voice snapshot preserves its original checksum and remains readable");

        repaired.Friends[0].Voice.Model = "first-new-model";
        repaired.Friends[0].Voice.ReferenceAudio = "D:/voices/first-new.wav";
        store.Save(repaired);
        var reloaded = store.Load();
        var saved = JsonNode.Parse(File.ReadAllText(main))!;
        check(reloaded.StorageRevision == 8 && saved["Settings"]!.AsObject().ContainsKey("Voice") == false
            && saved["Friends"]!.AsArray().All(f => f!.AsObject().ContainsKey("Voice"))
            && File.ReadAllBytes(main).SequenceEqual(File.ReadAllBytes(backup)),
            "first save commits only per-character voices with matching latest snapshots");
        check(reloaded.Friends[0].Voice.Model == "first-new-model" && reloaded.Friends[0].Voice.ReferenceAudio == "D:/voices/first-new.wav"
            && reloaded.Friends[1].Voice.Model == "old-model" && reloaded.Friends[2].Voice.Model == "custom-model",
            "changing one character voice never changes another after restart");
        string voices = JsonSerializer.Serialize(reloaded.Friends.Select(f => f.Voice));
        store.Save(reloaded);
        check(JsonSerializer.Serialize(store.Load().Friends.Select(f => f.Voice)) == voices,
            "subsequent saves do not reapply or flatten the legacy voice configuration");
        var added = new FriendProfile();
        var copied = reloaded.Friends[0].Voice.Copy(); copied.Model = "request-snapshot";
        check(!added.Voice.Enabled && added.Voice.ProtectedApiKey == ""
            && added.Voice.Model != reloaded.Friends[0].Voice.Model && reloaded.Friends[0].Voice.Model == "first-new-model",
            "new characters start independently and request copies cannot mutate saved voices");

        var exported = Path.Combine(testRoot, "voice-export.json");
        string beforeExport = JsonSerializer.Serialize(reloaded.Friends[0].Voice);
        store.Export(reloaded.Friends[0], exported);
        string exportText = File.ReadAllText(exported);
        var voiceNode = JsonNode.Parse(exportText)!["Voice"]!;
        check(!exportText.Contains("ProtectedApiKey") && !exportText.Contains("legacy-encrypted-key-sentinel")
            && !exportText.Contains("inline-secret-sentinel") && !exportText.Contains("voice.example")
            && voiceNode["ReferenceAudio"]!.GetValue<string>() == "" && voiceNode["GptWeights"]!.GetValue<string>() == ""
            && voiceNode["SovitsWeights"]!.GetValue<string>() == ""
            && voiceNode["Model"]!.GetValue<string>() == "first-new-model" && voiceNode["Voice"]!.GetValue<string>() == "old-speaker"
            && beforeExport == JsonSerializer.Serialize(reloaded.Friends[0].Voice),
            "character export retains voice identity but strips credentials, connection data, template secrets and local paths");
        var hostileImport = JsonNode.Parse(exportText)!;
        hostileImport["Voice"]!["ProtectedApiKey"] = "imported-key-sentinel";
        hostileImport["Voice"]!["Endpoint"] = "https://unconfirmed.example/tts";
        hostileImport["Voice"]!["RequestTemplate"] = oldVoice.RequestTemplate;
        hostileImport["Voice"]!["ReferenceAudio"] = "C:/foreign/private.wav";
        hostileImport["Voice"]!["Enabled"] = true; hostileImport["Voice"]!["AutoPlay"] = true;
        File.WriteAllText(exported, hostileImport.ToJsonString());
        var imported = store.ImportFriend(exported);
        check(!imported.Voice.Enabled && !imported.Voice.AutoPlay && imported.Voice.ProtectedApiKey == ""
            && imported.Voice.Endpoint == "" && imported.Voice.ReferenceAudio == ""
            && imported.Voice.RequestTemplate == new VoiceSettings().RequestTemplate
            && imported.Voice.Model == "first-new-model" && imported.Messages.Count == 1 && imported.Memories.Count == 1,
            "imported voice presets require connection confirmation and cannot inject credentials or automatic playback");

        RejectBroken("missing-voice", root => root["Friends"]![0]!.AsObject().Remove("Voice"));
        RejectBroken("null-voice", root => root["Friends"]![0]!["Voice"] = null);
        RejectBroken("incomplete-voice", root => root["Friends"]![0]!["Voice"]!.AsObject().Remove("Model"));
        RejectBroken("null-voice-field", root => root["Friends"]![0]!["Voice"]!["Model"] = null);
        var tampered = legacyRoot.DeepClone();
        tampered["Settings"]!["Voice"]!["Model"] = "tampered-after-signing";
        var badHashStore = NewStore("voice-legacy-hash");
        File.WriteAllText(Path.Combine(badHashStore.DataDirectory, "state.json"), tampered.ToJsonString());
        bool rejectedHash = false;
        try { badHashStore.Load(); } catch (InvalidDataException) { rejectedHash = true; }
        check(rejectedHash, "legacy voice migration never bypasses snapshot integrity verification");

        var assetStore = NewStore("voice-user-assets");
        var assetState = assetStore.Load();
        string reference = Path.Combine(assetStore.AssetsDirectory, Guid.NewGuid().ToString("N") + ".wav");
        string gpt = Path.Combine(assetStore.AssetsDirectory, "user-model.ckpt");
        string sovits = Path.Combine(assetStore.AssetsDirectory, "user-model.pth");
        string manualImage = Path.Combine(assetStore.AssetsDirectory, "user-image.png");
        foreach (var file in new[] { reference, gpt, sovits, manualImage }) File.WriteAllText(file, "user-owned-sentinel");
        assetState.Friends[0].Voice.ReferenceAudio = reference;
        assetState.Friends[0].Voice.GptWeights = gpt;
        assetState.Friends[0].Voice.SovitsWeights = sovits;
        assetStore.Save(assetState); assetStore.PruneAssets(assetState);
        check(new[] { reference, gpt, sovits, manualImage }.All(File.Exists),
            "asset pruning preserves user-owned voice audio, model weights and manually placed images");
        assetState.Friends[0].Voice = new VoiceSettings();
        assetStore.Save(assetState); assetStore.PruneAssets(assetState);
        check(new[] { reference, gpt, sovits, manualImage }.All(File.Exists),
            "clearing a voice configuration never treats user-owned audio or models as garbage");
        string managedImage = assetStore.ImportAsset(manualImage);
        string managedPath = assetStore.ResolveAsset(managedImage)!;
        assetStore.PruneAssets(assetState);
        check(!File.Exists(managedPath) && File.Exists(manualImage) && File.Exists(reference),
            "unused app-imported image copies are reclaimed without deleting user source assets");

        PortableStore NewStore(string name)
        {
            var result = new PortableStore(Path.Combine(testRoot, name)); result.Initialize(); return result;
        }
        void RejectBroken(string name, Action<JsonNode> change)
        {
            var broken = JsonSerializer.SerializeToNode(new AppState())!;
            change(broken);
            var brokenStore = NewStore(name);
            string path = Path.Combine(brokenStore.DataDirectory, "state.json");
            string text = broken.ToJsonString(); File.WriteAllText(path, text);
            bool rejected = false;
            try { brokenStore.Load(); } catch (InvalidDataException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == text, name + " is rejected without replacing it with a default voice");
        }
    }
}
