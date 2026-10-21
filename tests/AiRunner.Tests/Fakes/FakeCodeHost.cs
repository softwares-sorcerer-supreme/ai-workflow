using AiRunner.Infrastructure;
using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Tests.Fakes;

/// <summary>A file the fake serves for an _attached URL.</summary>
public sealed record FakeAttachment(byte[] Content, string? Name, string? ContentType);

/// <summary>In-memory code host: issues, comments, labels and PRs, with GitBucket's bot-marker behaviour.</summary>
public sealed class FakeCodeHost : ICodeHost
{
    private long _nextCommentId = 1000;
    private int _nextNumber = 100;

    public string DefaultBranch { get; set; } = "main";
    public string Login { get; set; } = "bot";
    public Dictionary<int, HostIssue> Issues { get; } = [];
    public Dictionary<int, List<HostComment>> Comments { get; } = [];
    public List<HostPull> Pulls { get; } = [];

    /// <summary>The repo of a PR, by number. A PR without an entry is in every repo (old tests with one repo).</summary>
    public Dictionary<int, string> PullRepos { get; } = [];
    public List<(long Id, string Body)> Updated { get; } = [];

    /// <summary>Called after the runner posts a comment, with the issue number: lets a test play a second runner in a race.</summary>
    public Action<int>? OnCommentAdded { get; set; }

    /// <summary>Which repo each repo-level call went to, in order: lets a test see where the runner reads and writes.</summary>
    public List<(string Operation, string Repo)> RepoCalls { get; } = [];

    public IEnumerable<string> ReposOf(string operation) =>
        RepoCalls.Where(call => call.Operation == operation).Select(call => call.Repo).Distinct();

    /// <summary>Files behind _attached URLs, by absolute URL. A URL that is not here answers 404.</summary>
    public Dictionary<string, FakeAttachment> Attachments { get; } = [];
    public List<string> Downloads { get; } = [];

    /// <summary>When set, listing issues fails with this message (VPN down, token rejected).</summary>
    public string? IssueListError { get; set; }

    /// <summary>When set, closing an issue fails with this message (web sign-in refused, no write access).</summary>
    public string? CloseError { get; set; }

    /// <summary>Plays a GitBucket version that ignores the labels of a new issue.</summary>
    public bool DropsLabelsOnCreate { get; set; }

    /// <summary>The number of every GetIssueAsync call, in order.</summary>
    public List<int> IssueReads { get; } = [];

    /// <summary>GitBucket's by default, so the jobs behave as they do against it.</summary>
    public CodeHostCapabilities Capabilities { get; set; } = new(KeepsLabelsOnCreate: false);

    /// <summary>GitBucket's _attached links on the kit's base URL.</summary>
    public IReadOnlyList<AttachmentRef> FindAttachments(string? text) => AttachmentLinks.Find(text, JobTestKit.BaseUrl);

    // ---- arrange helpers ----

    public HostIssue AddIssue(int number, string title, string? body = null, params string[] labels)
    {
        var labelList = labels.Select(label => new HostLabel(label)).ToList();
        var issue = new HostIssue(
            number,
            title,
            body,
            new HostUser("alice"),
            labelList,
            DateTimeOffset.UtcNow,
            null,
            "open");
        Issues[number] = issue;
        Comments.TryAdd(number, []);

        return issue;
    }

    public HostComment AddHumanComment(int number, string body, string user = "alice") => AddRaw(number, body, user);

    public HostComment AddBotComment(int number, string body) => AddRaw(number, BotComments.Marker + "\n" + body, Login);

    public HostPull AddPull(
        int number,
        string head,
        string baseRef,
        string sha = "abc1234def",
        string? body = null,
        bool merged = false,
        string state = "open",
        string title = "Some change",
        string? repo = null,
        params string[] labels)
    {
        var labelList = labels.Select(label => new HostLabel(label)).ToList();
        var pull = new HostPull(
            number,
            title,
            new HostRef(head, sha),
            new HostRef(baseRef, "base000"),
            DateTimeOffset.UtcNow,
            body,
            new HostUser("alice"),
            state,
            merged,
            merged ? DateTimeOffset.UtcNow : null,
            Labels: labelList);
        Pulls.RemoveAll(existingPull => existingPull.Number == number);
        Pulls.Add(pull);
        if (repo is not null)
        {
            PullRepos[number] = repo;
        }

        return pull;
    }

    public HashSet<string> LabelsOf(int number) => Issues[number].LabelNames();

    public IReadOnlyList<HostComment> CommentsOf(int number) => Comments.GetValueOrDefault(number) ?? [];

    private HostComment AddRaw(int number, string body, string user)
    {
        var comment = new HostComment(++_nextCommentId, body, new HostUser(user), DateTimeOffset.UtcNow);
        Comments.TryAdd(number, []);
        Comments[number].Add(comment);

        return comment;
    }

    private void SetLabels(int number, IEnumerable<string> labels) =>
        Issues[number] = Issues[number] with
        {
            Labels = labels.Distinct().Select(label => new HostLabel(label)).ToList()
        };

    public void AddAttachment(string url, byte[] content, string? name = null, string? contentType = null) =>
        Attachments[url] = new FakeAttachment(content, name, contentType);

    // ---- ICodeHost ----

    public Task<DownloadedAttachment> DownloadAttachmentAsync(
        AttachmentDownload download,
        CancellationToken cancellationToken)
    {
        Downloads.Add(download.Url);
        if (!Attachments.TryGetValue(download.Url, out var attachment))
        {
            throw new AttachmentDownloadException("the server answered 404");
        }

        if (attachment.Content.Length > download.MaxBytes)
        {
            throw new AttachmentDownloadException("too large");
        }

        var urlPath = new Uri(download.Url).AbsolutePath;
        var extension = AttachmentPolicy.ChooseExtension(attachment.Name, urlPath, attachment.ContentType);

        Directory.CreateDirectory(download.Directory);
        var path = Path.Combine(download.Directory, download.FileStem + extension);
        File.WriteAllBytes(path, attachment.Content);

        return Task.FromResult(new DownloadedAttachment(path, attachment.Name, attachment.ContentType, attachment.Content.Length));
    }

    /// <summary>When set, GET /user throws it: a token the host refuses (401) or a host that cannot be reached.</summary>
    public Exception? CurrentUserError { get; set; }

    /// <summary>How often GET /user was asked (it is not a repo call, so it is not in <see cref="RepoCalls"/>).</summary>
    public int CurrentUserReads { get; private set; }

    /// <summary>Every call the runner made, of any kind: zero proves "no code-host call".</summary>
    public int CallCount =>
        RepoCalls.Count + CurrentUserReads + IssueReads.Count + Downloads.Count + Updated.Count + _labelWrites;

    /// <summary>Label adds and removals, which <see cref="RepoCalls"/> does not list.</summary>
    private int _labelWrites;

    public Task<HostUser> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        CurrentUserReads++;
        if (CurrentUserError is not null)
        {
            return Task.FromException<HostUser>(CurrentUserError);
        }

        return Task.FromResult(new HostUser(Login));
    }

    public Task<HostRepo> GetRepoAsync(string repo, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("repo", repo));

        return Task.FromResult(new HostRepo(DefaultBranch));
    }

    /// <summary>Awaited before each open-issue list, so a test can hold a poll while it runs.</summary>
    public Func<Task>? BeforeIssueList { get; set; }

    public async Task<IReadOnlyList<HostIssue>> ListOpenIssuesAsync(
        string repo,
        string? label,
        string? assignee,
        CancellationToken cancellationToken)
    {
        RepoCalls.Add(("issues", repo));
        if (BeforeIssueList is not null)
        {
            await BeforeIssueList();
        }

        if (IssueListError is not null)
        {
            throw new HttpRequestException(IssueListError);
        }

        var matchingIssues = Issues.Values
            .Where(issue => issue.IsOpen()
                && (label is null || issue.HasLabel(label))
                && (assignee is null || issue.IsAssignedTo(assignee)))
            .ToList();

        return matchingIssues;
    }

    public Task<IReadOnlyList<HostIssue>> ListClosedIssuesAsync(
        string repo,
        string label,
        CancellationToken cancellationToken)
    {
        RepoCalls.Add(("closed-issues", repo));
        var matchingIssues = Issues.Values
            .Where(issue => !issue.IsOpen() && issue.HasLabel(label))
            .ToList();

        return Task.FromResult<IReadOnlyList<HostIssue>>(matchingIssues);
    }

    public int CommentListCalls { get; private set; }

    public Task<HostIssue> GetIssueAsync(string repo, int number, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("issue-read", repo));
        IssueReads.Add(number);

        return Task.FromResult(Issues[number]);
    }

    public Task<IReadOnlyList<HostComment>> ListCommentsAsync(string repo, int number, CancellationToken cancellationToken)
    {
        CommentListCalls++;
        RepoCalls.Add(("comments-read", repo));
        var comments = CommentsOf(number);
        return Task.FromResult(comments);
    }

    public Task<HostIssue> CreateIssueAsync(string repo, NewIssue issue, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("create-issue", repo));
        while (Issues.ContainsKey(++_nextNumber))
        {
        }

        var labels = DropsLabelsOnCreate ? [] : issue.Labels.ToArray();
        var created = AddIssue(_nextNumber, issue.Title, issue.Body, labels);

        return Task.FromResult(created);
    }

    public Task<HostComment> AddCommentAsync(string repo, int number, string body, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("comment", repo));
        var markedBody = body.Contains(BotComments.Marker) ? body : BotComments.Marker + "\n" + body;
        var comment = AddRaw(number, markedBody, Login);
        OnCommentAdded?.Invoke(number);

        return Task.FromResult(comment);
    }

    public Task<HostComment> UpdateCommentAsync(
        string repo,
        long commentId,
        string body,
        CancellationToken cancellationToken)
    {
        foreach (var list in Comments.Values)
        {
            var index = list.FindIndex(comment => comment.Id == commentId);
            if (index < 0)
            {
                continue;
            }

            list[index] = list[index] with
            {
                Body = BotComments.Marker + "\n" + body
            };
            Updated.Add((commentId, body));

            return Task.FromResult(list[index]);
        }

        throw new CodeHostException(System.Net.HttpStatusCode.NotFound, "comment not found");
    }

    public Task CloseIssueAsync(string repo, int number, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("close-issue", repo));
        if (CloseError is not null)
        {
            throw new CodeHostException(System.Net.HttpStatusCode.Unauthorized, CloseError);
        }

        Issues[number] = Issues[number] with
        {
            State = "closed"
        };

        return Task.CompletedTask;
    }

    public Task AddLabelsAsync(string repo, int number, IEnumerable<string> labels, CancellationToken cancellationToken)
    {
        _labelWrites++;
        if (Issues.ContainsKey(number))
        {
            var allLabels = LabelsOf(number).Concat(labels);
            SetLabels(number, allLabels);
        }

        return Task.CompletedTask;
    }

    /// <summary>Every label removal, issues and PRs alike.</summary>
    public List<(int Number, string Label)> RemovedLabels { get; } = [];

    public Task RemoveLabelAsync(string repo, int number, string label, CancellationToken cancellationToken)
    {
        _labelWrites++;
        RemovedLabels.Add((number, label));
        if (Issues.ContainsKey(number))
        {
            var remainingLabels = LabelsOf(number)
                .Where(existing => !string.Equals(existing, label, StringComparison.OrdinalIgnoreCase));
            SetLabels(number, remainingLabels);
        }

        return Task.CompletedTask;
    }

    /// <summary>The labels asked for, per repo, in order.</summary>
    public List<(string Repo, NewLabel Label)> CreatedLabels { get; } = [];

    public Task<bool> CreateLabelAsync(string repo, NewLabel label, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("label", repo));
        CreatedLabels.Add((repo, label));

        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<HostPull>> ListPullsAsync(string repo, string state, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("pulls", repo));
        var matchingPulls = Pulls
            .Where(pull => state == "all" || pull.State == state)
            .Where(pull => !PullRepos.TryGetValue(pull.Number, out var pullRepo) ||
                           string.Equals(pullRepo, repo, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Task.FromResult<IReadOnlyList<HostPull>>(matchingPulls);
    }

    public Task<HostPull> GetPullAsync(string repo, int number, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("pull-read", repo));
        var pull = Pulls.Single(existingPull => existingPull.Number == number);

        return Task.FromResult(pull);
    }

    public Task<HostPull> CreatePullAsync(string repo, NewPull newPull, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("create-pull", repo));
        var highestPullNumber = Pulls.Select(existingPull => existingPull.Number).DefaultIfEmpty(0).Max();
        var number = Math.Max(_nextNumber, highestPullNumber) + 1;
        _nextNumber = number;

        var head = new HostRef(newPull.Head, "new0000");
        var baseRef = new HostRef(newPull.BaseBranch, "base000");
        var author = new HostUser(Login);
        var pull = new HostPull(
            number,
            newPull.Title,
            head,
            baseRef,
            DateTimeOffset.UtcNow,
            newPull.Body,
            author,
            "open",
            false,
            null);
        Pulls.Add(pull);
        PullRepos[number] = repo;

        return Task.FromResult(pull);
    }
}
