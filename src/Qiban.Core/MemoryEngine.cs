using System.Text.RegularExpressions;

namespace Qiban.Core;

/// <summary>Small, explainable Chinese/Latin lexical retriever; no hidden model or embedding downloads.</summary>
public static partial class MemoryEngine
{
    public static IReadOnlyList<MemoryItem> Retrieve(FriendProfile friend, string query, int charBudget)
    {
        if (charBudget <= 0) return [];
        var now = DateTimeOffset.UtcNow;
        var words = Tokens(query);
        var candidates = friend.Memories
            .Where(m => m.State == "approved" && (m.ExpiresAt == null || m.ExpiresAt > now))
            .Select(m => new { Memory = m, Terms = Tokens(m.Content) })
            .Select(x => new
            {
                x.Memory,
                Overlap = words.Count == 0 ? 0 : words.Intersect(x.Terms).Count() / Math.Sqrt(Math.Max(1, words.Count * x.Terms.Count))
            })
            .Where(x => x.Memory.Pinned || x.Overlap > 0 || words.Count == 0)
            .OrderByDescending(x => x.Memory.Pinned)
            .ThenByDescending(x => x.Overlap * 6 + Math.Clamp(x.Memory.Confidence, 0, 1) * .2
                + Math.Exp(-Math.Max(0, (now - x.Memory.UpdatedAt).TotalDays) / 90) * .3)
            .ThenByDescending(x => x.Memory.UpdatedAt)
            .ThenBy(x => x.Memory.Id, StringComparer.Ordinal);
        var results = new List<MemoryItem>();
        var remaining = Math.Min(charBudget, 32000);
        foreach (var item in candidates)
        {
            var cost = item.Memory.Content.Length + item.Memory.Category.Length + 32;
            if (cost > remaining) continue;
            results.Add(item.Memory);
            item.Memory.LastUsedAt = now;
            remaining -= cost;
            if (results.Count >= 24) break;
        }
        return results;
    }

    public static MemoryItem? SuggestFromMessage(FriendProfile friend, ChatMessage message)
    {
        if (message.Role != "user" || friend.Memories.Count >= 20000) return null;
        var match = RememberRequest().Match(message.Text.Trim());
        if (!match.Success) return null;
        var content = match.Groups[1].Value.Trim();
        if (content.Length is < 2 or > 8000) return null;
        var comparable = Normalize(content);
        if (friend.Memories.Any(m => Normalize(m.Content) == comparable)) return null;
        var item = new MemoryItem
        {
            Content = content,
            Category = content.Contains("喜欢") || content.Contains("讨厌") || content.Contains("偏好") ? "偏好" : "事实",
            State = "pending",
            Confidence = 1,
            SourceMessageId = message.Id
        };
        friend.Memories.Add(item);
        return item;
    }

    private static HashSet<string> Tokens(string value)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Words().Matches(value.ToLowerInvariant()))
        {
            var word = match.Value;
            if (word.Any(c => c >= 0x3400 && c <= 0x9fff))
            {
                if (word.Length == 1) tokens.Add(word);
                for (var i = 0; i + 1 < word.Length; i++) tokens.Add(word.Substring(i, 2));
            }
            else tokens.Add(word);
        }
        return tokens;
    }

    private static string Normalize(string value) => Whitespace().Replace(value.Trim().TrimEnd('。', '.', '！', '!'), "").ToLowerInvariant();
    [GeneratedRegex(@"^(?:(?:请你|请|帮我|你要|一定要)\s*)?记住\s*[：:，,]?\s*(.+)$", RegexOptions.Singleline)]
    private static partial Regex RememberRequest();
    [GeneratedRegex(@"[\u3400-\u9fff]+|[\p{L}\p{N}-[\u3400-\u9fff]]+")]
    private static partial Regex Words();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
