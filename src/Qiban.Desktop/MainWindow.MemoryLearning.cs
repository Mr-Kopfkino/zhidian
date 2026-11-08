using System.Windows;
using System.Windows.Threading;
using Qiban.Core;

namespace Qiban.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer _memoryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _memoryCts;
    private bool _memoryLearningEnabled;
    private long _memoryEpoch;
    private DateTimeOffset _memoryNextAt;
    private string _memoryStatus = "";
    private string _memoryFailureFriendId = "", _memoryFailureStatus = "";
    private DateTimeOffset _memoryFailureUntil;

    private void InitializeMemoryLearning(bool enabled)
    {
        _memoryLearningEnabled = enabled;
        _memoryNextAt = DateTimeOffset.UtcNow.AddSeconds(3);
        _memoryTimer.Tick += async (_, _) => await RunMemoryLearningAsync();
        if (enabled) _memoryTimer.Start();
        UpdateMemoryLearningUi();
    }

    private void CancelMemoryLearning(bool preserveFailure = false)
    {
        _memoryEpoch++;
        _memoryCts?.Cancel();
        bool keepFailure = preserveFailure && _memoryFailureFriendId == Friend.Id && _memoryFailureUntil > DateTimeOffset.UtcNow;
        _memoryNextAt = keepFailure ? _memoryFailureUntil : DateTimeOffset.UtcNow.AddSeconds(3);
        _memoryStatus = keepFailure ? _memoryFailureStatus : "";
        if (!preserveFailure) _memoryFailureUntil = default;
    }

    private void RecordMemoryLearningFailure(Exception error, string friendId)
    {
        _memoryStatus = "整理失败 · 2 分钟后重试：" + Boot.SafeError(error);
        _memoryNextAt = DateTimeOffset.UtcNow.AddMinutes(2);
        _memoryFailureFriendId = friendId;
        _memoryFailureUntil = _memoryNextAt;
        _memoryFailureStatus = _memoryStatus;
    }

    private void UpdateMemoryLearningUi()
    {
        if (!_ready) return;
        bool enabled = _state.Settings.AutoMemoryEnabled || _state.Settings.AutoSummaryEnabled;
        MemoryLearningStatus.Text = !enabled ? "自动整理已关闭" : string.IsNullOrWhiteSpace(_state.Settings.Model)
            ? "模型未配置" : _memoryCts is { IsCancellationRequested: false } ? "正在整理"
            : _memoryStatus.Length > 0 ? _memoryStatus : _busy ? "等待回复完成" : "自动整理已开启";
        AnalyzeMemoryButton.IsEnabled = enabled && !_busy && _memoryCts == null;
        var summary = MemoryLearning.GetSummary(Friend);
        ConversationSummary.Text = summary.Count == 0 ? "暂无摘要" : string.Join("\n", summary.Select(item =>
            (item.Role == "user" ? "用户：" : "角色：") + item.Text));
        ClearSummaryButton.IsEnabled = summary.Count > 0;
    }

    private async Task RunMemoryLearningAsync(bool immediate = false)
    {
        if ((!_memoryLearningEnabled && !immediate) || _closing || !_ready || _busy || _memoryCts != null) return;
        if (!immediate && DateTimeOffset.UtcNow < _memoryNextAt) return;
        if (!_state.Settings.AutoMemoryEnabled && !_state.Settings.AutoSummaryEnabled || string.IsNullOrWhiteSpace(_state.Settings.Model)) return;
        var friend = Friend;
        MemoryLearningRequest? request;
        try { request = MemoryLearning.Prepare(friend, _state.Settings); }
        catch (Exception error)
        {
            RecordMemoryLearningFailure(error, friend.Id);
            UpdateMemoryLearningUi();
            return;
        }
        if (request == null)
        {
            _memoryFailureUntil = default;
            _memoryNextAt = DateTimeOffset.UtcNow.AddSeconds(15);
            if (immediate) _memoryStatus = "暂无待整理对话";
            UpdateMemoryLearningUi();
            return;
        }
        // Dialogue always has priority over this auxiliary request, including local models
        // that can serve only one generation at a time.
        _profileCts?.Cancel();
        var epoch = _memoryEpoch;
        using var cts = new CancellationTokenSource();
        _memoryCts = cts;
        UpdateMemoryLearningUi();
        try
        {
            var result = await _chat.AnalyzeMemoryAsync(_state.Settings, request, cts.Token);
            if (_closing || cts.IsCancellationRequested || epoch != _memoryEpoch || !ReferenceEquals(friend, Friend)) return;
            var outcome = MemoryLearning.Apply(friend, request, result, _state.Settings);
            Save();
            _memoryFailureUntil = default;
            _memoryStatus = "已整理" + (_state.Settings.AutoMemoryEnabled ? $" · 自动保存 {outcome.Added} 条 · 待确认 {outcome.Pending} 条" : "")
                + (outcome.SummaryChanged ? " · 摘要已更新" : "");
            _memoryNextAt = DateTimeOffset.UtcNow.AddSeconds(3);
            if (MemoryPage.Visibility == Visibility.Visible) RefreshMemoriesPreservingDraft();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closing && epoch == _memoryEpoch)
            {
                RecordMemoryLearningFailure(error, friend.Id);
            }
        }
        finally
        {
            if (ReferenceEquals(_memoryCts, cts)) _memoryCts = null;
            if (!_closing) UpdateMemoryLearningUi();
        }
    }

    private void RefreshMemoriesPreservingDraft()
    {
        var draft = (Id: _selectedMemory?.Id, Text: MemoryContent.Text, Category: Combo(MemoryCategory), Pinned: MemoryPinned.IsChecked, Expiry: MemoryExpiry.Text);
        RefreshMemories();
        _selectedMemory = Friend.Memories.FirstOrDefault(m => m.Id == draft.Id);
        MemoryContent.Text = draft.Text;
        SetCombo(MemoryCategory, draft.Category);
        MemoryPinned.IsChecked = draft.Pinned;
        MemoryExpiry.Text = draft.Expiry;
    }

    private async void OnAnalyzeMemory(object sender, RoutedEventArgs e) => await RunMemoryLearningAsync(immediate: true);

    private void OnClearSummary(object sender, RoutedEventArgs e) => Guard(() =>
    {
        CancelMemoryLearning();
        MemoryLearning.ClearSummary(Friend);
        Save();
        UpdateMemoryLearningUi();
    });
}
