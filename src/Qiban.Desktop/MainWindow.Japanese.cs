using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Threading;
using Qiban.Core;

namespace Qiban.Desktop;

public partial class MainWindow
{
    private async Task CompleteReplyAsync(FriendProfile friend, ChatMessage answer, IReadOnlyList<MemoryItem> memories,
        InitiativeTrigger? initiative, TextBlock? textBlock, bool translationRetry, CancellationToken ct)
    {
        var mode = friend.JapaneseChat.Copy();
        answer.DisplayLanguage = mode.Enabled ? mode.DisplayLanguage : "";
        bool showOriginal = !mode.Enabled || JapaneseChat.IsJapaneseDisplay(mode.DisplayLanguage);
        if (!translationRetry)
        {
            var buffer = new StringBuilder();
            var gate = new object();
            void ReadBuffer()
            {
                lock (gate)
                {
                    string content = buffer.ToString();
                    if (showOriginal) answer.Text = content;
                }
                if (textBlock != null) textBlock.Text = answer.Text;
            }
            var refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(55) };
            refresh.Tick += (_, _) =>
            {
                bool nearBottom = ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 70;
                ReadBuffer();
                if (nearBottom) ChatScroll.ScrollToEnd();
            };
            refresh.Start();
            try
            {
                void Append(string delta) { lock (gate) buffer.Append(delta); }
                if (initiative == null) await _chat.StreamAsync(_state.Settings, friend, memories, Append, ct);
                else await _chat.StreamInitiativeAsync(_state.Settings, friend, memories, initiative, Append, ct);
                ct.ThrowIfCancellationRequested();
                if (mode.Enabled)
                {
                    string original;
                    lock (gate) original = buffer.ToString();
                    try { JapaneseChat.EnsureJapanese(original); }
                    catch (InvalidDataException) when (!string.IsNullOrWhiteSpace(original))
                    {
                        // Correct this completed reply once; never re-generate the conversation
                        // or allow an unverified draft to count as a delivered proactive message.
                        lock (gate) buffer.Clear();
                        answer.Text = "";
                        if (textBlock != null) textBlock.Text = "";
                        Notice("正在转换日语");
                        original = await _chat.TranslateAsync(_state.Settings, friend, original, "日本語", ct);
                        ct.ThrowIfCancellationRequested();
                        JapaneseChat.EnsureJapanese(original);
                        lock (gate) buffer.Append(original);
                    }
                    answer.JapaneseText = original;
                }
            }
            finally { refresh.Stop(); ReadBuffer(); }
        }
        ct.ThrowIfCancellationRequested();
        if (!mode.Enabled) return;
        JapaneseChat.EnsureJapanese(answer.JapaneseText);
        if (showOriginal) answer.Text = answer.JapaneseText;
        else
        {
            // Commit the original before translation; failure/restart must not erase the reply.
            Save();
            Notice("正在翻译");
            answer.Text = await _chat.TranslateAsync(_state.Settings, friend, answer.JapaneseText, mode.DisplayLanguage, ct);
        }
        ct.ThrowIfCancellationRequested();
        if (textBlock != null) textBlock.Text = answer.Text;
    }
}
