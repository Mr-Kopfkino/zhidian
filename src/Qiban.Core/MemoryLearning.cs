using System.Text;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Qiban.Core;

public sealed class MemoryLearningState
{
    public string LastProcessedMessageId { get; set; } = "";
    public string SummarySuppressedThroughMessageId { get; set; } = "";
    public List<SummaryItem> Summary { get; set; } = [];
    public List<string> SuppressedSourceIds { get; set; } = [];
    public List<string> SummarySuppressedSourceIds { get; set; } = [];
}

public sealed class SummaryItem
{
    public string Text { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Role { get; set; } = "";
}

public sealed record LearningMessage(string Id, string Role, string Text, DateTimeOffset CreatedAt = default)
{
    [JsonIgnore] public string OriginalTextHash { get; init; } = "";
}
public sealed record LearningMemory(string Id, string Content, string Category, string State);
public sealed record MemoryLearningRequest(string FriendId, string ThroughMessageId,
    IReadOnlyList<LearningMessage> Messages, IReadOnlyList<LearningMemory> ExistingMemories,
    IReadOnlyList<SummaryItem> PreviousSummary, bool EnableMemories, bool EnableSummary)
{
    public string PreviousProcessedMessageId { get; init; } = "";
    public string SummarySuppressionId { get; init; } = "";
    public IReadOnlyList<string> NewUserMessageIds { get; init; } = Messages.Where(m => m.Role == "user").Select(m => m.Id).ToArray();
}

public sealed class MemoryCandidate
{
    public string Content { get; set; } = "";
    public string Category { get; set; } = "事实";
    public double Confidence { get; set; }
    public string SourceMessageId { get; set; } = "";
    public string Evidence { get; set; } = "";
    public List<string> ConflictsWithIds { get; set; } = [];
}

public sealed class SummaryCandidate
{
    public string Text { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string Evidence { get; set; } = "";
}

public sealed class MemoryLearningResult
{
    public List<MemoryCandidate> Memories { get; set; } = [];
    public List<SummaryCandidate> Summary { get; set; } = [];
}

public sealed record MemoryLearningOutcome(int Added, int Pending, bool SummaryChanged);

/// <summary>Bounded, source-checked learning proposals. Call Apply and the portable store's Save on the UI thread.</summary>
public static class MemoryLearning
{
    public const int SummaryCharacterLimit = 2400;
    private static string SourceText(ChatMessage message) => message.Role == "assistant" && !string.IsNullOrEmpty(message.JapaneseText)
        ? message.JapaneseText : message.Text;
    private static bool Eligible(ChatMessage message) => message.Role is "user" or "assistant"
        && message.Status == "complete" && !string.IsNullOrWhiteSpace(SourceText(message));
    private static HashSet<string> Suppressed(FriendProfile friend) => friend.Learning.SuppressedSourceIds
        .Concat(friend.Memories.Where(m => m.State == "archived").Select(m => m.SourceMessageId)).ToHashSet(StringComparer.Ordinal);

    public static MemoryLearningRequest? Prepare(FriendProfile friend, AppSettings settings)
    {
        if (!settings.AutoMemoryEnabled && !settings.AutoSummaryEnabled) return null;
        var suppressed = Suppressed(friend);
        int cursor = friend.Messages.FindLastIndex(m => m.Id == friend.Learning.LastProcessedMessageId);
        bool newUser = friend.Messages.Skip(cursor + 1).Any(m => Eligible(m) && m.Role == "user" && !suppressed.Contains(m.Id));
        if (!newUser) return null;
        var messages = new List<LearningMessage>();
        int remaining = 32000;
        bool Available(ChatMessage message) => Eligible(message) && !suppressed.Contains(message.Id);
        void Add(ChatMessage message, int limit)
        {
            string text = SourceText(message);
            text = text[..Math.Min(text.Length, limit)];
            messages.Add(new(message.Id, message.Role, text, message.CreatedAt) { OriginalTextHash = Hash(SourceText(message)) });
            remaining -= text.Length;
        }
        if (cursor < 0)
        {
            // An older installation has no cursor. Only inspect its recent conversation,
            // never unexpectedly upload its entire saved history on upgrade.
            foreach (var message in friend.Messages.Where(Available).TakeLast(24).Reverse())
            {
                if (remaining == 0) break;
                Add(message, Math.Min(8000, remaining));
            }
            messages.Reverse();
        }
        else
        {
            // Once a cursor exists, consume the oldest unfinished batch. Repeated user
            // interruptions must not make earlier unprocessed turns fall off a tail window.
            int batchStart = cursor + 1;
            var context = friend.Messages.Take(batchStart).Where(Available).TakeLast(4).ToArray();
            if (!friend.Messages.Skip(batchStart).Where(Available).Take(24 - context.Length).Any(m => m.Role == "user"))
            {
                // Assistant-only activity is not a learning trigger; retain its latest
                // context while advancing to the next actual user statement.
                batchStart = friend.Messages.FindIndex(batchStart, m => Available(m) && m.Role == "user");
                context = friend.Messages.Take(batchStart).Where(Available).TakeLast(4).ToArray();
            }
            foreach (var message in context) Add(message, 2000);
            foreach (var message in friend.Messages.Skip(batchStart).Where(Available).Take(24 - context.Length))
            {
                int size = Math.Min(8000, SourceText(message).Length);
                if (size > remaining) break;
                Add(message, size);
            }
        }
        if (messages.Count == 0 || !messages.Any(m => m.Role == "user" && friend.Messages.FindIndex(x => x.Id == m.Id) > cursor)) return null;
        var known = new List<LearningMemory>();
        remaining = 12000;
        foreach (var memory in friend.Memories.OrderByDescending(m => m.Pinned).ThenByDescending(m => m.UpdatedAt).Take(80))
        {
            if (memory.Content.Length > remaining) continue;
            known.Add(new(memory.Id, memory.Content, memory.Category, memory.State));
            remaining -= memory.Content.Length;
        }
        return new(friend.Id, messages[^1].Id, messages, known, GetSummary(friend),
            settings.AutoMemoryEnabled, settings.AutoSummaryEnabled)
        {
            PreviousProcessedMessageId = friend.Learning.LastProcessedMessageId,
            SummarySuppressionId = friend.Learning.SummarySuppressedThroughMessageId,
            NewUserMessageIds = messages.Where(m => m.Role == "user" && friend.Messages.FindIndex(x => x.Id == m.Id) > cursor).Select(m => m.Id).ToArray()
        };
    }

    public static IReadOnlyList<SummaryItem> GetSummary(FriendProfile friend)
    {
        var suppressed = Suppressed(friend);
        suppressed.UnionWith(friend.Learning.SummarySuppressedSourceIds);
        suppressed.UnionWith(friend.Memories.Where(m => m.State == "pending").Select(m => m.SourceMessageId));
        var sources = friend.Messages.Where(Eligible).ToDictionary(m => m.Id, StringComparer.Ordinal);
        int remaining = SummaryCharacterLimit;
        var valid = new List<SummaryItem>();
        foreach (var item in friend.Learning.Summary.Take(12))
        {
            if (suppressed.Contains(item.SourceMessageId) || !sources.TryGetValue(item.SourceMessageId, out var source)
                || item.Role != source.Role || string.IsNullOrWhiteSpace(item.Evidence)
                || !SourceText(source).Contains(item.Evidence, StringComparison.Ordinal) || item.Text.Length > remaining) continue;
            valid.Add(new() { Text = item.Text, Role = item.Role, SourceMessageId = item.SourceMessageId, Evidence = item.Evidence });
            remaining -= item.Text.Length;
        }
        return valid;
    }

    public static MemoryLearningOutcome Apply(FriendProfile friend, MemoryLearningRequest request,
        MemoryLearningResult result, AppSettings settings)
    {
        // Validate the entire response before making any mutation. Stale/deleted/edited sources
        // must not return after a user correction, a concurrent job, or a settings change.
        if (friend.Id != request.FriendId || friend.Learning.LastProcessedMessageId != request.PreviousProcessedMessageId
            || friend.Learning.SummarySuppressedThroughMessageId != request.SummarySuppressionId
            || request.EnableMemories != settings.AutoMemoryEnabled || request.EnableSummary != settings.AutoSummaryEnabled)
            throw new InvalidOperationException("记忆整理结果已过期。");
        if (result.Memories == null || result.Summary == null || result.Memories.Count > 12 || result.Summary.Count > 12)
            throw new InvalidDataException("记忆整理条目数量无效。");
        var live = friend.Messages.Where(Eligible).ToDictionary(m => m.Id, StringComparer.Ordinal);
        var suppressed = Suppressed(friend);
        var supplied = request.Messages.ToDictionary(m => m.Id, StringComparer.Ordinal);
        int processedIndex = friend.Messages.FindLastIndex(m => m.Id == request.PreviousProcessedMessageId);
        var newSourceIds = request.NewUserMessageIds.Where(id => supplied.TryGetValue(id, out var source) && source.Role == "user"
            && friend.Messages.FindIndex(m => m.Id == id) > processedIndex).ToHashSet(StringComparer.Ordinal);
        if (!supplied.ContainsKey(request.ThroughMessageId)) throw new InvalidDataException("记忆整理进度无效。");
        foreach (var message in request.Messages)
            if (!live.TryGetValue(message.Id, out var current) || current.Role != message.Role
                || current.CreatedAt != message.CreatedAt || suppressed.Contains(message.Id)
                || !SourceText(current).StartsWith(message.Text, StringComparison.Ordinal)
                || (message.OriginalTextHash.Length > 0 ? Hash(SourceText(current)) != message.OriginalTextHash : SourceText(current) != message.Text))
                throw new InvalidOperationException("记忆整理来源已变更。");
        var previous = request.PreviousSummary.ToLookup(m => m.SourceMessageId, StringComparer.Ordinal);
        foreach (var memory in request.ExistingMemories)
            if (!friend.Memories.Any(m => m.Id == memory.Id && m.Content == memory.Content
                && m.Category == memory.Category && m.State == memory.State))
                throw new InvalidOperationException("已有记忆在整理期间被修改。");
        void Evidence(string id, string quote, bool userOnly)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128 || string.IsNullOrWhiteSpace(quote) || quote.Length > 2000
                || suppressed.Contains(id) || !live.TryGetValue(id, out var source) || userOnly && source.Role != "user"
                || !SourceText(source).Contains(quote, StringComparison.Ordinal))
                throw new InvalidDataException("记忆整理缺少有效的原文依据。");
            bool found = supplied.TryGetValue(id, out var message) && message.Text.Contains(quote, StringComparison.Ordinal);
            if (!found && (userOnly || !previous[id].Any(item => item.Evidence.Contains(quote, StringComparison.Ordinal)
                && item.Role == source.Role))) throw new InvalidDataException("记忆整理引用了未提供的来源。");
        }
        foreach (var candidate in result.Memories)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Content) || candidate.Content.Length > 800
                || candidate.Category is not ("偏好" or "事实" or "事件" or "约定")
                || !double.IsFinite(candidate.Confidence) || candidate.Confidence is < 0 or > 1
                || candidate.ConflictsWithIds == null || candidate.ConflictsWithIds.Count > 12
                || candidate.ConflictsWithIds.Any(id => string.IsNullOrEmpty(id) || !request.ExistingMemories.Any(m => m.Id == id)))
                throw new InvalidDataException("自动记忆内容或冲突来源无效。");
            Evidence(candidate.SourceMessageId, candidate.Evidence, true);
        }
        int summarySize = 0;
        foreach (var candidate in result.Summary)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Text) || candidate.Text.Length > 400
                || (summarySize += candidate.Text.Length) > SummaryCharacterLimit)
                throw new InvalidDataException("对话摘要超出长度限制。");
            Evidence(candidate.SourceMessageId, candidate.Evidence, false);
        }
        if ((!request.EnableMemories && result.Memories.Count > 0) || (!request.EnableSummary && result.Summary.Count > 0))
            throw new InvalidDataException("记忆整理返回了已关闭的内容。");

        int added = 0, pending = 0;
        var known = friend.Memories.Select(m => Normalize(m.Content)).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in result.Memories)
        {
            if (!newSourceIds.Contains(candidate.SourceMessageId) || candidate.Confidence < .5
                || friend.Memories.Count >= 20000 || !known.Add(Normalize(candidate.Content))) continue;
            bool approved = candidate.Confidence >= .85 && candidate.ConflictsWithIds.Count == 0;
            friend.Memories.Add(new MemoryItem
            {
                Content = candidate.Content.Trim(), Category = candidate.Category, SourceMessageId = candidate.SourceMessageId,
                Confidence = candidate.Confidence, Origin = "model", State = approved ? "approved" : "pending"
            });
            if (approved) added++; else pending++;
        }
        bool summaryChanged = false;
        if (request.EnableSummary)
        {
            int floor = friend.Messages.FindLastIndex(m => m.Id == friend.Learning.SummarySuppressedThroughMessageId);
            // An unreviewed or conflicting fact must not become trusted merely by being
            // repeated in the automatically injected summary.
            var uncertainSources = friend.Memories.Where(m => m.State == "pending").Select(m => m.SourceMessageId)
                .Concat(result.Memories.Where(m => newSourceIds.Contains(m.SourceMessageId)
                    && (m.Confidence < .85 || m.ConflictsWithIds.Count > 0)).Select(m => m.SourceMessageId))
                .Concat(friend.Learning.SummarySuppressedSourceIds)
                .ToHashSet(StringComparer.Ordinal);
            // Empty means there is nothing worth adding this turn, not a request to erase
            // accumulated context. Only the user's explicit ClearSummary action clears it.
            IEnumerable<SummaryCandidate> summaryCandidates = result.Summary.Count == 0
                ? GetSummary(friend).Select(item => new SummaryCandidate
                    { Text = item.Text, SourceMessageId = item.SourceMessageId, Evidence = item.Evidence })
                : result.Summary;
            var summary = summaryCandidates.Where(item => friend.Messages.FindIndex(m => m.Id == item.SourceMessageId) > floor
                    && !uncertainSources.Contains(item.SourceMessageId))
                .DistinctBy(item => (item.SourceMessageId, Normalize(item.Text)))
                .Select(item => new SummaryItem { Text = item.Text.Trim(), SourceMessageId = item.SourceMessageId,
                    Evidence = item.Evidence, Role = live[item.SourceMessageId].Role }).ToList();
            summaryChanged = !friend.Learning.Summary.Select(x => (x.Text, x.SourceMessageId, x.Evidence, x.Role))
                .SequenceEqual(summary.Select(x => (x.Text, x.SourceMessageId, x.Evidence, x.Role)));
            friend.Learning.Summary = summary;
        }
        friend.Learning.LastProcessedMessageId = request.ThroughMessageId;
        return new(added, pending, summaryChanged);
    }

    public static void SuppressSource(FriendProfile friend, string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return;
        if (!friend.Learning.SuppressedSourceIds.Contains(sourceId, StringComparer.Ordinal))
        {
            // Keep all suppressions while their original messages remain available for learning.
            var existing = friend.Messages.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            friend.Learning.SuppressedSourceIds.RemoveAll(id => !existing.Contains(id));
            if (friend.Learning.SuppressedSourceIds.Count >= 100000) throw new InvalidOperationException("记忆屏蔽记录已达上限。");
            friend.Learning.SuppressedSourceIds.Add(sourceId);
        }
        friend.Learning.Summary.RemoveAll(item => item.SourceMessageId == sourceId);
    }

    public static void ClearSummary(FriendProfile friend)
    {
        friend.Learning.Summary.Clear();
        friend.Learning.SummarySuppressedThroughMessageId = friend.Messages.LastOrDefault()?.Id ?? "";
        friend.Learning.SummarySuppressedSourceIds = friend.Messages.Select(m => m.Id).Distinct(StringComparer.Ordinal).ToList();
    }

    private static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
        .Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
