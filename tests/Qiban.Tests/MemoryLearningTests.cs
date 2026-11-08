using System.Text.Json;
using System.Text.Json.Nodes;
using Qiban.Core;

internal static class MemoryLearningTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var settings = new AppSettings();
        var friend = Profile("我不吃香菜，每周六会去游泳。");
        var request = MemoryLearning.Prepare(friend, settings)!;
        check(request.EnableMemories && request.EnableSummary && request.Messages[0].CreatedAt == friend.Messages[0].CreatedAt,
            "automatic learning and summaries default on and retain source dates");
        var candidate = Candidate(friend, "用户不吃香菜", "我不吃香菜", .95);
        var result = new MemoryLearningResult { Memories = [candidate], Summary = [Summary(friend, "用户提到自己的饮食偏好和周末运动", "每周六会去游泳")] };
        var outcome = MemoryLearning.Apply(friend, request, result, settings);
        check(outcome.Added == 1 && outcome.Pending == 0 && friend.Memories[0].State == "approved"
            && friend.Memories[0].Origin == "model" && friend.Memories[0].SourceMessageId == friend.Messages[0].Id,
            "natural conversation facts become sourced approved model memories without a remember command");
        check(outcome.SummaryChanged && MemoryLearning.GetSummary(friend).Single().Role == "user"
            && friend.Learning.LastProcessedMessageId == request.ThroughMessageId && MemoryLearning.Prepare(friend, settings) == null,
            "a checked rolling summary and processed cursor stop repeat requests for the same conversation");
        Reject(() => MemoryLearning.Apply(friend, request, result, settings), "a second application of the same job is rejected without duplicating memory");
        friend.Messages.Add(new() { Role = "assistant", Text = "要不要聊聊你周末的安排？", Trigger = "idle" });
        check(MemoryLearning.Prepare(friend, settings) == null, "proactive assistant messages alone never initiate learning");

        var greeting = Profile("下周会一起去看画展。");
        MemoryLearning.Apply(greeting, MemoryLearning.Prepare(greeting, settings)!, new()
            { Summary = [Summary(greeting, "用户谈到下周看画展的计划", "下周会一起去看画展")] }, settings);
        string accumulatedSummary = JsonSerializer.Serialize(greeting.Learning.Summary);
        greeting.Messages.Add(new() { Text = "你好。" });
        outcome = MemoryLearning.Apply(greeting, MemoryLearning.Prepare(greeting, settings)!, new(), settings);
        check(!outcome.SummaryChanged && JsonSerializer.Serialize(greeting.Learning.Summary) == accumulatedSummary
            && greeting.Learning.LastProcessedMessageId == greeting.Messages[1].Id,
            "an empty summary after a casual greeting preserves valid accumulated context while advancing progress");

        var repeated = Profile("我喜欢苹果。");
        MemoryLearning.Apply(repeated, MemoryLearning.Prepare(repeated, settings)!, new()
            { Memories = [Candidate(repeated, "用户喜欢苹果", "喜欢苹果", .95)] }, settings);
        repeated.Messages.Add(new() { Text = "今天到公园散步了。" });
        var nextRequest = MemoryLearning.Prepare(repeated, settings)!;
        outcome = MemoryLearning.Apply(repeated, nextRequest, new()
            { Memories = [Candidate(repeated, "苹果是用户喜欢的水果", "喜欢苹果", .95)] }, settings);
        check(nextRequest.NewUserMessageIds.SequenceEqual(new[] { repeated.Messages[1].Id })
            && nextRequest.Messages.Count == 2 && outcome.Added == 0 && repeated.Memories.Count == 1,
            "prior context remains available for understanding but cannot accumulate paraphrased memories on every turn");

        var confidence = Profile("我可能喜欢茶，也许是因为最近睡得少。");
        var response = new MemoryLearningResult
        {
            Memories = [Candidate(confidence, "用户喜欢茶", "我可能喜欢茶", .7), Candidate(confidence, "用户经常失眠", "最近睡得少", .2)],
            Summary = [Summary(confidence, "用户喜欢茶且经常失眠", "我可能喜欢茶")]
        };
        outcome = MemoryLearning.Apply(confidence, MemoryLearning.Prepare(confidence, settings)!, response, settings);
        check(outcome.Added == 0 && outcome.Pending == 1 && confidence.Memories.Count == 1
            && confidence.Memories[0].State == "pending" && confidence.Learning.Summary.Count == 0,
            "uncertain facts await review, very weak guesses are discarded, and neither bypasses review through summaries");

        var conflict = Profile("我现在喜欢红茶了。");
        var original = new MemoryItem { Content = "用户不喜欢红茶", State = "approved", Pinned = true };
        conflict.Memories.Add(original);
        var conflicting = Candidate(conflict, "用户现在喜欢红茶", "我现在喜欢红茶", .99);
        conflicting.ConflictsWithIds.Add(original.Id);
        MemoryLearning.Apply(conflict, MemoryLearning.Prepare(conflict, settings)!, new() { Memories = [conflicting] }, settings);
        check(conflict.Memories.Count == 2 && conflict.Memories[1].State == "pending" && original.Content == "用户不喜欢红茶"
            && original.State == "approved" && original.Pinned && original.Origin == "manual",
            "even high-confidence conflicting proposals never replace a user's confirmed or pinned memory");

        var duplicate = Profile("我喜欢猫。");
        duplicate.Memories.Add(new() { Content = "用户喜欢猫。", State = "archived", SourceMessageId = "older-source" });
        outcome = MemoryLearning.Apply(duplicate, MemoryLearning.Prepare(duplicate, settings)!,
            new() { Memories = [Candidate(duplicate, "用 户喜欢猫！", "我喜欢猫", .99)] }, settings);
        check(outcome.Added == 0 && duplicate.Memories.Count == 1 && duplicate.Memories[0].State == "archived",
            "normalized duplicates do not revive archived memories");

        var grounded = Profile("我喜欢古典音乐。");
        var sourceRequest = MemoryLearning.Prepare(grounded, settings)!;
        var valid = Candidate(grounded, "用户喜欢古典音乐", "我喜欢古典音乐", .9);
        var invalid = Candidate(grounded, "用户在东京居住", "我住在东京", .99);
        string before = JsonSerializer.Serialize(grounded);
        Reject(() => MemoryLearning.Apply(grounded, sourceRequest, new() { Memories = [valid, invalid] }, settings),
            "fabricated evidence rejects the whole proposal batch");
        check(JsonSerializer.Serialize(grounded) == before, "invalid later entries cannot leave partial memories or advance the cursor");
        invalid.Evidence = "我喜欢古典音乐";
        invalid.SourceMessageId = "fabricated-id";
        Reject(() => MemoryLearning.Apply(grounded, sourceRequest, new() { Memories = [invalid] }, settings),
            "model supplied source identifiers must exist in the actual provided conversation");
        grounded.Messages.Add(new() { Role = "assistant", Text = "你住在东京吧。" });
        var assistantFact = new MemoryCandidate { Content = "用户住在东京", Category = "事实", Confidence = .99,
            SourceMessageId = grounded.Messages[1].Id, Evidence = "你住在东京" };
        Reject(() => MemoryLearning.Apply(grounded, MemoryLearning.Prepare(grounded, settings)!, new() { Memories = [assistantFact] }, settings),
            "assistant guesses cannot become user facts even with high confidence and an exact quote");
        var assistantSummary = new SummaryCandidate { Text = "角色询问用户是否居住在东京", SourceMessageId = grounded.Messages[1].Id, Evidence = "你住在东京" };
        MemoryLearning.Apply(grounded, MemoryLearning.Prepare(grounded, settings)!, new() { Summary = [assistantSummary] }, settings);
        check(MemoryLearning.GetSummary(grounded).Single().Role == "assistant", "summaries preserve the actual speaker of a proposal or question");

        var edited = Profile(new string('甲', 9000));
        var editRequest = MemoryLearning.Prepare(edited, settings)!;
        edited.Messages[0].Text += "此前只是假设";
        Reject(() => MemoryLearning.Apply(edited, editRequest, new(), settings),
            "changes outside the transmitted excerpt still invalidate a learning job through its full source hash");
        var changedSettings = Profile("我喜欢晚饭后散步。");
        var settingsRequest = MemoryLearning.Prepare(changedSettings, settings)!;
        settings.AutoMemoryEnabled = false;
        Reject(() => MemoryLearning.Apply(changedSettings, settingsRequest, new(), settings), "a disabled feature cannot receive a late learning result");
        settings.AutoSummaryEnabled = false;
        check(MemoryLearning.Prepare(changedSettings, settings) == null, "turning off both features disables background learning requests");
        settings.AutoMemoryEnabled = settings.AutoSummaryEnabled = true;
        var renamed = Profile("明天去图书馆。");
        var stale = MemoryLearning.Prepare(renamed, settings)!;
        renamed.Messages[0].Role = "assistant";
        Reject(() => MemoryLearning.Apply(renamed, stale, new(), settings), "a changed speaker invalidates pending results");

        var bounded = new FriendProfile();
        for (int i = 0; i < 60; i++) bounded.Messages.Add(new() { Text = $"第{i}次聊天" + new string('字', 3000) });
        var boundedRequest = MemoryLearning.Prepare(bounded, settings)!;
        check(boundedRequest.Messages.Count <= 24 && boundedRequest.Messages.Sum(m => m.Text.Length) <= 32000
            && boundedRequest.Messages[^1].Id == bounded.Messages[^1].Id && boundedRequest.Messages[0].Id != bounded.Messages[0].Id,
            "first-run catch-up is bounded to recent conversation instead of silently uploading all history");
        var backlog = Profile("这一条已经处理过。");
        backlog.Learning.LastProcessedMessageId = backlog.Messages[0].Id;
        for (int i = 0; i < 30; i++) backlog.Messages.Add(new() { Role = "assistant", Text = $"此前第{i}次主动攀谈", Trigger = "idle" });
        for (int i = 0; i < 65; i++) backlog.Messages.Add(new() { Text = $"积压的第{i}条新用户消息" + new string('字', 1500) });
        var expectedOrder = backlog.Messages.Skip(1).Where(m => m.Role == "user").Select(m => m.Id).ToArray();
        var learnedOrder = new List<string>();
        bool batchesBounded = true;
        for (int attempt = 0; attempt < 10 && MemoryLearning.Prepare(backlog, settings) is { } batch; attempt++)
        {
            batchesBounded &= batch.Messages.Count <= 24 && batch.Messages.Sum(m => m.Text.Length) <= 32000;
            learnedOrder.AddRange(batch.NewUserMessageIds);
            MemoryLearning.Apply(backlog, batch, new(), settings);
        }
        check(batchesBounded && learnedOrder.SequenceEqual(expectedOrder) && MemoryLearning.Prepare(backlog, settings) == null
            && backlog.Learning.LastProcessedMessageId == expectedOrder[^1],
            "a persisted cursor skips an assistant-only prefix and drains queued user turns in bounded order without losing older new facts");
        var tooLong = Profile("一段测试原文");
        var longSummaries = Enumerable.Range(0, 7).Select(i => Summary(tooLong, new string('字', 400), "测试原文")).ToList();
        Reject(() => MemoryLearning.Apply(tooLong, MemoryLearning.Prepare(tooLong, settings)!, new() { Summary = longSummaries }, settings),
            "oversized rolling summaries are rejected before persistence");

        var cleared = Profile("我想在周末看展览。");
        var clearRequest = MemoryLearning.Prepare(cleared, settings)!;
        MemoryLearning.Apply(cleared, clearRequest, new() { Summary = [Summary(cleared, "用户计划周末看展览", "周末看展览")] }, settings);
        MemoryLearning.ClearSummary(cleared);
        cleared.Messages.Add(new() { Text = "今天也去公园散步了。" });
        var later = MemoryLearning.Prepare(cleared, settings)!;
        MemoryLearning.Apply(cleared, later, new() { Summary = [Summary(cleared, "用户计划周末看展览", "周末看展览"),
            new() { Text = "用户今天去公园散步", SourceMessageId = cleared.Messages[1].Id, Evidence = "去公园散步" }] }, settings);
        check(cleared.Learning.Summary.Count == 1 && cleared.Learning.Summary[0].SourceMessageId == cleared.Messages[1].Id,
            "explicitly cleared summaries do not return from older context when a new turn is summarized");
        MemoryLearning.SuppressSource(cleared, cleared.Messages[1].Id);
        check(cleared.Learning.Summary.Count == 0 && MemoryLearning.GetSummary(cleared).Count == 0
            && MemoryLearning.Prepare(cleared, settings) == null,
            "suppressing a source removes its summary and prevents automatic re-learning after deletion or archive");
        var lostMarker = Profile("以前谈过的话题");
        lostMarker.Messages.Add(new() { Role = "assistant", Text = "收到。" });
        MemoryLearning.ClearSummary(lostMarker);
        lostMarker.Messages.RemoveAt(1);
        lostMarker.Messages.Add(new() { Text = "这是新的话题" });
        MemoryLearning.Apply(lostMarker, MemoryLearning.Prepare(lostMarker, settings)!, new()
            { Summary = [Summary(lostMarker, "旧话题再次出现", "以前谈过的话题")] }, settings);
        check(lostMarker.Learning.Summary.Count == 0,
            "deleting the summary-clear boundary message still cannot revive previously cleared source material");
        var deleted = Profile("我会在下午联系你。");
        var deletedRequest = MemoryLearning.Prepare(deleted, settings)!;
        deleted.Messages.Clear();
        Reject(() => MemoryLearning.Apply(deleted, deletedRequest, new(), settings), "deleted chat sources cannot be resurrected by an in-flight result");

        var persisted = new AppState();
        persisted.Friends = [friend];
        persisted.SelectedFriendId = friend.Id;
        var store = new PortableStore(Path.Combine(root, "automatic-memory"));
        store.Initialize();
        store.Save(persisted);
        var reloaded = store.Load();
        check(JsonSerializer.Serialize(reloaded) == JsonSerializer.Serialize(persisted)
            && MemoryLearning.GetSummary(reloaded.Friends[0]).Count == 1,
            "automatic memory provenance, summary, and cursor round-trip with the normal atomic state");
        File.WriteAllText(Path.Combine(store.DataDirectory, "state.json"), "{damaged");
        check(JsonSerializer.Serialize(store.Load()) == JsonSerializer.Serialize(persisted), "backup recovery restores automatic learning state with existing facts");

        var legacy = JsonSerializer.SerializeToNode(persisted)!.AsObject();
        legacy["Settings"]!.AsObject().Remove("AutoMemoryEnabled");
        legacy["Settings"]!.AsObject().Remove("AutoSummaryEnabled");
        foreach (var profile in legacy["Friends"]!.AsArray())
        {
            profile!.AsObject().Remove("Learning");
            foreach (var memory in profile["Memories"]!.AsArray()) memory!.AsObject().Remove("Origin");
        }
        var legacyStore = new PortableStore(Path.Combine(root, "legacy-memory-learning"));
        legacyStore.Initialize();
        string legacyPath = Path.Combine(legacyStore.DataDirectory, "state.json");
        string legacyJson = legacy.ToJsonString();
        File.WriteAllText(legacyPath, legacyJson);
        var migrated = legacyStore.Load();
        check(migrated.Settings.AutoMemoryEnabled && migrated.Settings.AutoSummaryEnabled && migrated.Friends[0].Learning.Summary.Count == 0
            && migrated.Friends[0].Memories[0].Origin == "manual" && migrated.Friends[0].Memories[0].Content == friend.Memories[0].Content
            && File.ReadAllText(legacyPath) == legacyJson,
            "older snapshots gain safe learning defaults without rewriting verified history or prior manual memories");
        migrated.Friends[0].Learning.Summary.Add(new() { Text = "损坏摘要", SourceMessageId = "id", Role = "invented", Evidence = "证据" });
        Reject(() => legacyStore.Save(migrated), "invalid speaker metadata is rejected by portable persistence");

        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { rejected = true; }
            check(rejected, label);
        }
    }

    private static FriendProfile Profile(string text) => new() { Messages = [new() { Text = text }] };
    private static MemoryCandidate Candidate(FriendProfile friend, string content, string evidence, double confidence) => new()
    {
        Content = content, Category = "偏好", Confidence = confidence, SourceMessageId = friend.Messages[0].Id, Evidence = evidence
    };
    private static SummaryCandidate Summary(FriendProfile friend, string text, string evidence) => new()
    {
        Text = text, SourceMessageId = friend.Messages[0].Id, Evidence = evidence
    };
}
