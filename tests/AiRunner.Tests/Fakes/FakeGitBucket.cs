using AiRunner.Infrastructure;

namespace AiRunner.Tests.Fakes;

/// <summary>In-memory GitBucket: issues, comments, labels and PRs, with the same bot-marker behaviour.</summary>
public sealed class FakeGitBucket : IGitBucketClient
{
    private long _nextCommentId = 1000;
    private int _nextNumber = 100;

    public string DefaultBranch { get; set; } = "main";
    public string Login { get; set; } = "bot";
    public Dictionary<int, GbIssue> Issues { get; } = [];
    public Dictionary<int, List<GbComment>> Comments { get; } = [];
    public List<GbPull> Pulls { get; } = [];
    public List<(long Id, string Body)> Updated { get; } = [];

    // ---- arrange helpers ----

    public GbIssue AddIssue(int number, string title, string? body = null, params string[] labels)
    {
        var issue = new GbIssue(number, title, body, new GbUser("alice"),
            labels.Select(l => new GbLabel(l)).ToList(), DateTimeOffset.UtcNow, null, "open");
        Issues[number] = issue;
        Comments.TryAdd(number, []);
        return issue;
    }

    public GbComment AddHumanComment(int number, string body, string user = "alice") => AddRaw(number, body, user);

    public GbComment AddBotComment(int number, string body) => AddRaw(number, BotComments.Marker + "\n" + body, Login);

    public GbPull AddPull(int number, string head, string baseRef, string sha = "abc1234def", string? body = null,
        bool merged = false, string state = "open", string title = "Some change")
    {
        var pull = new GbPull(number, title, new GbRef(head, sha), new GbRef(baseRef, "base000"), DateTimeOffset.UtcNow,
            body, new GbUser("alice"), state, merged, merged ? DateTimeOffset.UtcNow : null);
        Pulls.RemoveAll(p => p.Number == number);
        Pulls.Add(pull);
        return pull;
    }

    public HashSet<string> LabelsOf(int number) => Issues[number].LabelNames();
    public IReadOnlyList<GbComment> CommentsOf(int number) => Comments.GetValueOrDefault(number) ?? [];

    private GbComment AddRaw(int number, string body, string user)
    {
        var c = new GbComment(++_nextCommentId, body, new GbUser(user), DateTimeOffset.UtcNow);
        Comments.TryAdd(number, []);
        Comments[number].Add(c);
        return c;
    }

    private void SetLabels(int number, IEnumerable<string> labels) =>
        Issues[number] = Issues[number] with { Labels = labels.Distinct().Select(l => new GbLabel(l)).ToList() };

    // ---- IGitBucketClient ----

    public Task<GbUser> GetCurrentUserAsync(CancellationToken ct) => Task.FromResult(new GbUser(Login));

    public Task<GbRepo> GetRepoAsync(string repo, CancellationToken ct) => Task.FromResult(new GbRepo(DefaultBranch));

    public Task<IReadOnlyList<GbIssue>> ListOpenIssuesAsync(string repo, string? label, string? assignee, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GbIssue>>(Issues.Values
            .Where(i => i.IsOpen() && (label is null || i.HasLabel(label)) && (assignee is null || i.IsAssignedTo(assignee)))
            .ToList());

    public int CommentListCalls { get; private set; }

    public Task<GbIssue> GetIssueAsync(string repo, int number, CancellationToken ct) => Task.FromResult(Issues[number]);

    public Task<IReadOnlyList<GbComment>> ListCommentsAsync(string repo, int number, CancellationToken ct)
    {
        CommentListCalls++;
        return Task.FromResult(CommentsOf(number));
    }

    public Task<GbIssue> CreateIssueAsync(string repo, string title, string body, IEnumerable<string> labels, CancellationToken ct)
    {
        while (Issues.ContainsKey(++_nextNumber)) { }
        return Task.FromResult(AddIssue(_nextNumber, title, body, labels.ToArray()));
    }

    public Task<GbComment> AddCommentAsync(string repo, int number, string body, CancellationToken ct) =>
        Task.FromResult(AddRaw(number, body.Contains(BotComments.Marker) ? body : BotComments.Marker + "\n" + body, Login));

    public Task<GbComment> UpdateCommentAsync(string repo, long commentId, string body, CancellationToken ct)
    {
        foreach (var list in Comments.Values)
        {
            var i = list.FindIndex(c => c.Id == commentId);
            if (i < 0) continue;
            list[i] = list[i] with { Body = BotComments.Marker + "\n" + body };
            Updated.Add((commentId, body));
            return Task.FromResult(list[i]);
        }
        throw new GitBucketException(System.Net.HttpStatusCode.NotFound, "comment not found");
    }

    public Task AddLabelsAsync(string repo, int number, IEnumerable<string> labels, CancellationToken ct)
    {
        if (Issues.ContainsKey(number)) SetLabels(number, LabelsOf(number).Concat(labels));
        return Task.CompletedTask;
    }

    public Task RemoveLabelAsync(string repo, int number, string label, CancellationToken ct)
    {
        if (Issues.ContainsKey(number))
            SetLabels(number, LabelsOf(number).Where(l => !string.Equals(l, label, StringComparison.OrdinalIgnoreCase)));
        return Task.CompletedTask;
    }

    public Task<bool> CreateLabelAsync(string repo, string name, string color, string description, CancellationToken ct) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<GbPull>> ListPullsAsync(string repo, string state, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GbPull>>(Pulls.Where(p => state == "all" || p.State == state).ToList());

    public Task<GbPull> GetPullAsync(string repo, int number, CancellationToken ct) =>
        Task.FromResult(Pulls.Single(p => p.Number == number));

    public Task<GbPull> CreatePullAsync(string repo, string title, string head, string baseBranch, string body, CancellationToken ct)
    {
        var number = Math.Max(_nextNumber, Pulls.Select(p => p.Number).DefaultIfEmpty(0).Max()) + 1;
        _nextNumber = number;
        var pull = new GbPull(number, title, new GbRef(head, "new0000"), new GbRef(baseBranch, "base000"),
            DateTimeOffset.UtcNow, body, new GbUser(Login), "open", false, null);
        Pulls.Add(pull);
        return Task.FromResult(pull);
    }
}
