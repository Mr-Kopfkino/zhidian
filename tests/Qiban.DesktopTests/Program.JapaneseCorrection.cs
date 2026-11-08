using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using Qiban.Core;

namespace Qiban.DesktopTests;

internal static partial class Program
{
    private static async Task VerifyJapaneseCorrectionAsync(string root)
    {
        var fast = new InitiativeProfile(.5, 1, .8, .5);
        const string mixed = "Chromeで調べものをしていたんですね。";
        const string corrected = "ブラウザーで調べものをしていたんですね。";
        await using (var model = new JapaneseModelServer(mixed, corrected))
        await using (var voice = new LocalVoiceServer())
        using (var scene = new TriggerScene(Path.Combine(root, "ordinary"), model.Endpoint, fast))
        {
            scene.State.Settings.Initiative.IdleEnabled = false;
            Configure(scene, "日本語", voice);
            scene.Control<TextBox>("InputBox").Text = "继续说吧。";
            await scene.Send();
            var request = await voice.Request.WaitAsync(TimeSpan.FromSeconds(10));
            using var audio = JsonDocument.Parse(request.Body);
            using var repair = JsonDocument.Parse(model.Requests.ToArray()[1].Body);
            var repairMessages = repair.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            using var source = JsonDocument.Parse(repairMessages[^1].GetProperty("content").GetString()!);
            var answer = scene.Friend.Messages.Last();
            Check(model.Requests.Count == 2 && source.RootElement.GetProperty("text").GetString() == mixed
                && source.RootElement.GetProperty("targetLanguage").GetString() == "日本語"
                && answer.Status == "complete" && answer.Text == corrected && answer.JapaneseText == corrected
                && audio.RootElement.GetProperty("text").GetString() == corrected
                && audio.RootElement.GetProperty("text_lang").GetString() == "ja"
                && scene.Store.Load().Friends[0].Messages.Last().JapaneseText == corrected,
                "ordinary Japanese reply with a Latin software name is corrected once, persisted and spoken only as verified Japanese");
            await voice.AudioStarted.WaitAsync(TimeSpan.FromSeconds(10));
            Click(scene.Control<Button>("StopButton"));
            await UntilAsync(() => scene.Private("_voiceCts") == null, "corrected Japanese speech stop");
        }

        await using (var model = new JapaneseModelServer("直哉。少し休みませんか。", "直哉、少し休みませんか。"))
        using (var scene = new TriggerScene(Path.Combine(root, "idle"), model.Endpoint, fast))
        {
            Configure(scene, "日本語");
            await scene.Advance(30);
            var answer = scene.Friend.Messages.Single();
            Check(model.Requests.Count == 2 && answer.Trigger == "idle" && answer.Status == "complete"
                && answer.Text == "直哉、少し休みませんか。" && answer.Text == answer.JapaneseText
                && scene.Scheduler.UnansweredCount == 1,
                "idle dispatch repairs a standalone kanji name and counts the corrected message once");
        }

        const string activityOriginal = "SnippingToolを使っていたんですね。";
        const string activityJapanese = "スクリーンショットのツールを使っていたんですね。";
        const string display = "You were using a screenshot tool.";
        await using (var model = new JapaneseModelServer(activityOriginal, activityJapanese, display))
        using (var scene = new TriggerScene(Path.Combine(root, "activity"), model.Endpoint, fast))
        {
            Configure(scene, "English");
            EnableActivityOnly(scene);
            scene.Observe = new("SnippingTool", "应用");
            await scene.Tick();
            await scene.Advance(2);
            var answer = scene.Friend.Messages.Single();
            using var translation = JsonDocument.Parse(model.Requests.ToArray()[2].Body);
            using var source = JsonDocument.Parse(translation.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            Check(model.Requests.Count == 3 && answer.Trigger == "activity" && answer.Status == "complete"
                && answer.Text == display && answer.JapaneseText == activityJapanese
                && source.RootElement.GetProperty("text").GetString() == activityJapanese
                && scene.Scheduler.UnansweredCount == 1
                && scene.Store.Load().Friends[0].Messages.Single().JapaneseText == activityJapanese,
                "activity tick corrects Japanese before display translation without fabricating a user turn");
        }

        foreach (string language in new[] { "日本語", "English" })
        {
            await using var model = new JapaneseModelServer(mixed, "Still invalid.");
            await using var voice = new LocalVoiceServer();
            using var scene = new TriggerScene(Path.Combine(root, language == "日本語" ? "failed-original" : "failed-translated"), model.Endpoint, fast);
            Configure(scene, language, voice);
            EnableActivityOnly(scene);
            scene.Observe = new("chrome", "浏览器");
            await scene.Tick();
            await scene.Advance(2);
            Check(model.Requests.Count == 2 && scene.Friend.Messages.Count == 0 && !voice.Request.IsCompleted
                && scene.Scheduler.UnansweredCount == 0 && scene.Scheduler.RetryAfter > scene.Now
                && scene.Store.Load().Friends[0].Messages.Count == 0,
                $"failed Japanese correction ({language}) clears unverified text, starts backoff and consumes no unanswered slot or speech request");
            await scene.Advance(10);
            Check(model.Requests.Count == 2, $"failed Japanese correction ({language}) cannot loop during backoff");
        }

        await using (var model = new JapaneseModelServer(mixed, corrected) { PauseBeforeResponse = 1 })
        await using (var voice = new LocalVoiceServer())
        using (var scene = new TriggerScene(Path.Combine(root, "cancelled"), model.Endpoint, fast))
        {
            Configure(scene, "日本語", voice);
            EnableActivityOnly(scene);
            scene.Observe = new("chrome", "浏览器");
            await scene.Tick();
            scene.Now = scene.Now.AddSeconds(2);
            var sending = scene.Tick();
            await UntilAsync(() => model.Requests.Count == 2, "paused Japanese correction request");
            Check(scene.Friend.Messages.Single().Text == "" && scene.Friend.Messages.Single().JapaneseText == "",
                "unverified Japanese draft is cleared while its correction request is in flight");
            Click(scene.Control<Button>("StopButton"));
            await sending.WaitAsync(TimeSpan.FromSeconds(10));
            Check(model.Requests.Count == 2 && scene.Friend.Messages.Count == 0 && !voice.Request.IsCompleted
                && scene.Scheduler.UnansweredCount == 0 && scene.Private("_chatCts") == null
                && scene.Store.Load().Friends[0].Messages.Count == 0,
                "cancelling a Japanese correction removes the invalid proactive draft without speech or duplicate generation");
        }

        static void Configure(TriggerScene scene, string language, LocalVoiceServer? voice = null)
        {
            scene.Friend.JapaneseChat.Enabled = true;
            scene.Friend.JapaneseChat.DisplayLanguage = language;
            scene.Friend.Voice = new VoiceSettings { Enabled = voice != null, AutoPlay = voice != null,
                Provider = "GPT-SoVITS", Endpoint = voice?.BaseAddress + "/tts", Language = "zh",
                ReferenceAudio = "D:/fixture/chinese-reference.wav", ReferenceText = "中文参考音频文本" };
            scene.Store.Save(scene.State);
        }
    }
}
