using System.Security.Cryptography;
using System.Text;
using System.Windows.Controls;
using System.Windows.Threading;
using Qiban.Core;

namespace Qiban.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer _initiativeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly InitiativeEnvironment _initiativeEnvironment;
    private readonly InitiativeScheduler _initiativeScheduler;
    private CancellationTokenSource? _profileCts;
    private Task? _initiativeTask;
    private bool _initiativeEnabled, _initiativeTicking, _initiativeSending, _profileReady, _sessionPaused;
    private string _initiativeIdentity = "";
    private string _initiativeFriendId = "";
    private DateTimeOffset _profileRetryAt, _lastEdit, _lastPulse;
    private ActivityObservation? _activityCandidate;
    private DateTimeOffset _activitySince;
    private ActivityObservation? _lastObservedActivity;
    private DateTimeOffset _lastObservedAt;
    private InitiativeTrigger? _pendingInitiative;
    private DateTimeOffset _pendingSince;
    private bool _profileFailed;
    private string _initiativeError = "";

    private static string InitiativeKey(AppSettings settings, FriendProfile friend) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', "initiative-v1", friend.Name, friend.Persona, settings.Endpoint, settings.Model))));

    private void InitializeInitiative(bool enabled)
    {
        _initiativeEnabled = enabled;
        _lastPulse = _initiativeEnvironment.Now();
        _initiativeTimer.Tick += async (_, _) => await TickInitiativeAsync();
        ResetInitiative();
        if (enabled) _initiativeTimer.Start();
    }

    private void OnInputEdited(object sender, TextChangedEventArgs e) => _lastEdit = _initiativeEnvironment.Now();

    private void ResetInitiative(bool force = false)
    {
        if (!_ready) return;
        var options = _state.Settings.Initiative;
        var key = InitiativeKey(_state.Settings, Friend);
        var identity = $"{Friend.Id}/{key}/{options.IdleEnabled}/{options.ActivityEnabled}/{options.MaxUnanswered}";
        if (!force && identity == _initiativeIdentity) { UpdateInitiativeStatus(); return; }
        _profileCts?.Cancel();
        _initiativeIdentity = identity;
        _pendingInitiative = null;
        _activityCandidate = null;
        _lastObservedActivity = null;
        _profileRetryAt = DateTimeOffset.MinValue;
        _profileFailed = false;
        _initiativeError = "";
        _profileReady = Friend.InitiativeProfileKey == key && Friend.InitiativeProfile != null;
        var sinceUser = Friend.Messages.AsEnumerable().Reverse().TakeWhile(m => m.Role != "user");
        int unanswered = sinceUser.Count(m => m.Trigger.Length != 0 && m.Text.Length > 0);
        var last = Friend.Messages.LastOrDefault(m => m.Trigger.Length != 0 && m.Text.Length > 0);
        var lastActivity = Friend.Messages.LastOrDefault(m => m.Trigger == "activity" && m.Text.Length > 0);
        _initiativeScheduler.Reset(_initiativeEnvironment.Now(), _profileReady ? Friend.InitiativeProfile! : InitiativeProfile.Balanced,
            unanswered, last?.CreatedAt, lastActivity?.CreatedAt, preserveActivityAttempts: _initiativeFriendId == Friend.Id);
        _initiativeFriendId = Friend.Id;
        UpdateInitiativeStatus();
    }

    private void UpdateInitiativeStatus(string? message = null)
    {
        if (!_ready) return;
        var options = _state.Settings.Initiative;
        if (message != null) { InitiativeStatus.Text = message; return; }
        if (!options.IdleEnabled && !options.ActivityEnabled) { InitiativeStatus.Text = "已关闭"; return; }
        if (string.IsNullOrWhiteSpace(_state.Settings.Model)) { InitiativeStatus.Text = "模型未配置"; return; }
        if (_sessionPaused) { InitiativeStatus.Text = "会话暂停"; return; }
        var now = _initiativeEnvironment.Now();
        var p = _profileReady && Friend.InitiativeProfile is { } saved ? saved : InitiativeProfile.Balanced;
        string progress = _initiativeScheduler.UnansweredCount >= options.MaxUnanswered ? "等待用户回复 · 已达到连续未回复上限" :
            _initiativeError.Length > 0 && _initiativeScheduler.RetryAfter > now
                ? $"发送失败 · {Math.Ceiling((_initiativeScheduler.RetryAfter - now).TotalSeconds)} 秒后重试：{_initiativeError}" :
            _pendingInitiative != null ? "活动消息已排队" : _initiativeSending ? "正在主动回复" : _busy ? "等待当前回复完成" :
            options.IdleEnabled ? $"下次待机触发约 {Math.Max(0, Math.Ceiling((_initiativeScheduler.NextIdleAt - now).TotalSeconds))} 秒" : "正在检测前台活动";
        string analysis = _profileReady ? "角色节奏" : _profileFailed ? "临时节奏 · 角色分析待重试" :
            _profileCts is { IsCancellationRequested: false } ? "临时节奏 · 角色分析中" : "临时节奏 · 等待角色分析";
        InitiativeStatus.Text = $"{progress}\n{analysis}：待机 {p.IdleMinMinutes:0.#}–{p.IdleMaxMinutes:0.#} 分钟 · 活动概率 {p.ActivityProbability:P0}";
        if (options.ActivityEnabled) InitiativeStatus.Text += "\n" + ActivityStatus(now, options.MaxUnanswered);
    }

    private string ActivityStatus(DateTimeOffset now, int maxUnanswered)
    {
        if (_activityCandidate is not { } activity)
            return _lastObservedActivity is { } recent && now - _lastObservedAt <= TimeSpan.FromMinutes(2)
                ? $"最近：{recent.Category}（{recent.ProcessName}） · 等待前台应用" : "前台：未检测到应用";
        string state;
        var next = _initiativeScheduler.GetActivityEligibleAt(now, activity);
        if (_initiativeScheduler.UnansweredCount >= maxUnanswered) state = "等待用户回复";
        else if (_pendingInitiative != null) state = "活动消息已排队";
        else if (_initiativeSending) state = "正在主动回复";
        else if (next > now) state = $"{Math.Ceiling((next - now).TotalSeconds)} 秒后重新评估";
        else state = "检测中";
        return $"前台：{activity.Category}（{activity.ProcessName}） · {state}";
    }

    private async Task<bool> EnsureInitiativeProfileAsync()
    {
        if (_profileReady) return true;
        if (_busy || _memoryCts != null || _profileCts != null || _initiativeEnvironment.Now() < _profileRetryAt) return false;
        var identity = _initiativeIdentity;
        var friend = Friend;
        var key = InitiativeKey(_state.Settings, friend);
        using var cts = new CancellationTokenSource();
        _profileCts = cts;
        UpdateInitiativeStatus();
        try
        {
            var profile = await _chat.GetInitiativeProfileAsync(_state.Settings, friend, cts.Token);
            if (_closing || cts.IsCancellationRequested || identity != _initiativeIdentity ||
                key != InitiativeKey(_state.Settings, Friend) || !ReferenceEquals(friend, Friend)) return false;
            friend.InitiativeProfile = profile;
            friend.InitiativeProfileKey = key;
            Save();
            _profileReady = true;
            _profileFailed = false;
            _initiativeScheduler.UpdateProfile(_initiativeEnvironment.Now(), profile);
            UpdateInitiativeStatus();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception)
        {
            if (!_closing && identity == _initiativeIdentity)
            {
                _profileRetryAt = _initiativeEnvironment.Now().AddMinutes(2);
                _profileFailed = true;
                UpdateInitiativeStatus();
            }
            return false;
        }
        finally { if (ReferenceEquals(_profileCts, cts)) _profileCts = null; }
    }

    private async Task TickInitiativeAsync()
    {
        if (!_initiativeEnabled || _closing) return;
        var now = _initiativeEnvironment.Now();
        if (_lastPulse != default && now - _lastPulse > TimeSpan.FromMinutes(1))
        {
            _sessionPaused = true;
            _profileCts?.Cancel();
            if (_initiativeSending) _chatCts?.Cancel();
            _pendingInitiative = null;
            if (!_initiativeSending) _initiativeScheduler.CancelPending(now);
        }
        _lastPulse = now;
        // This check also runs while generation awaits I/O, so locking cancels it promptly.
        if (!_initiativeEnvironment.IsSessionAvailable())
        {
            _sessionPaused = true;
            _profileCts?.Cancel();
            if (_initiativeSending) _chatCts?.Cancel();
            _pendingInitiative = null;
            if (!_initiativeSending) _initiativeScheduler.CancelPending(now);
            UpdateInitiativeStatus();
            return;
        }
        if (_initiativeTicking) return;
        _initiativeTicking = true;
        try
        {
            ResetInitiative(_sessionPaused);
            _sessionPaused = false;
            var options = _state.Settings.Initiative;
            if ((!options.IdleEnabled && !options.ActivityEnabled) || string.IsNullOrWhiteSpace(_state.Settings.Model)) return;
            // Personality analysis must never prevent ordinary proactive requests. While it is
            // pending or unavailable, use the bounded temporary cadence and retry in the background.
            _ = EnsureInitiativeProfileAsync();
            now = _initiativeEnvironment.Now();

            if (_pendingInitiative != null && now - _pendingSince > TimeSpan.FromMinutes(2))
            {
                _pendingInitiative = null;
                _initiativeScheduler.CancelPending(now);
            }
            if (options.ActivityEnabled)
            {
                var observed = _initiativeEnvironment.ObserveActivity();
                if (observed != _activityCandidate) { _activityCandidate = observed; _activitySince = now; }
                if (observed != null) { _lastObservedActivity = observed; _lastObservedAt = now; }
                // Require two samples of the same foreground app to ignore transient switches.
                if (_pendingInitiative == null && observed != null && now - _activitySince >= TimeSpan.FromSeconds(2))
                {
                    _pendingInitiative = _initiativeScheduler.TryActivity(now, observed, options.MaxUnanswered);
                    if (_pendingInitiative != null) _pendingSince = now;
                }
            }
            if (_busy) return; // Retain at most one activity event until the current reply finishes.
            if (_pendingInitiative == null && options.IdleEnabled && now - _lastEdit >= TimeSpan.FromSeconds(15))
                _pendingInitiative = _initiativeScheduler.TryIdle(now, options.MaxUnanswered);
            if (_pendingInitiative is not { } trigger) return;
            _pendingInitiative = null;
            _initiativeScheduler.DispatchPending();
            _initiativeTask = SendAsync(false, trigger);
            await _initiativeTask;
        }
        catch (Exception e)
        {
            _initiativeScheduler.Completed(_initiativeEnvironment.Now(), false);
            _initiativeError = Boot.SafeError(e);
        }
        finally { _initiativeTask = null; _initiativeTicking = false; if (!_closing) UpdateInitiativeStatus(); }
    }

    private void StopInitiative()
    {
        _initiativeTimer.Stop();
        _profileCts?.Cancel();
        _pendingInitiative = null;
    }
}
