using AiRunner.Infrastructure;

namespace AiRunner.Tests.Fakes;

/// <summary>A file the fake serves for an _attached URL.</summary>
public sealed record FakeAttachment(byte[] Content, string? Name, string? ContentType);

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

    // ---- arrange helpers ----

    public GbIssue AddIssue(int number, string title, string? body = null, params string[] labels)
    {
        var labelList = labels.Select(label => new GbLabel(label)).ToList();
        var issue = new GbIssue(
            number,
            title,
            body,
            new GbUser("alice"),
            labelList,
            DateTimeOffset.UtcNow,
            null,
            "open");
        Issues[number] = issue;
        Comments.TryAdd(number, []);

        return issue;
    }

    public GbComment AddHumanComment(int number, string body, string user = "alice") => AddRaw(number, body, user);

    public GbComment AddBotComment(int number, string body) => AddRaw(number, BotComments.Marker + "\n" + body, Login);

    public GbPull AddPull(
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
        var labelList = labels.Select(label => new GbLabel(label)).ToList();
        var pull = new GbPull(
            number,
            title,
            new GbRef(head, sha),
            new GbRef(baseRef, "base000"),
            DateTimeOffset.UtcNow,
            body,
            new GbUser("alice"),
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

    public IReadOnlyList<GbComment> CommentsOf(int number) => Comments.GetValueOrDefault(number) ?? [];

    private GbComment AddRaw(int number, string body, string user)
    {
        var comment = new GbComment(++_nextCommentId, body, new GbUser(user), DateTimeOffset.UtcNow);
        Comments.TryAdd(number, []);
        Comments[number].Add(comment);

        return comment;
    }

    private void SetLabels(int number, IEnumerable<string> labels) =>
        Issues[number] = Issues[number] with
        {
            Labels = labels.Distinct().Select(label => new GbLabel(label)).ToList()
        };

    public void AddAttachment(string url, byte[] content, string? name = null, string? contentType = null) =>
        Attachments[url] = new FakeAttachment(content, name, contentType);

    // ---- IGitBucketClient ----

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

    public Task<GbUser> GetCurrentUserAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new GbUser(Login));

    public Task<GbRepo> GetRepoAsync(string repo, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("repo", repo));

        return Task.FromResult(new GbRepo(DefaultBranch));
    }

    public Task<IReadOnlyList<GbIssue>> ListOpenIssuesAsync(
        string repo,
        string? label,
        string? assignee,
        CancellationToken cancellationToken)
    {
        RepoCalls.Add(("issues", repo));
        if (IssueListError is not null)
        {
            throw new HttpRequestException(IssueListError);
        }

        var matchingIssues = Issues.Values
            .Where(issue => issue.IsOpen()
                && (label is null || issue.HasLabel(label))
                && (assignee is null || issue.IsAssignedTo(assignee)))
            .ToList();

        return Task.FromResult<IReadOnlyList<GbIssue>>(matchingIssues);
    }

    public Task<IReadOnlyList<GbIssue>> ListClosedIssuesAsync(
        string repo,
        string label,
        CancellationToken cancellationToken)
    {
        RepoCalls.Add(("closed-issues", repo));
        var matchingIssues = Issues.Values
            .Where(issue => !issue.IsOpen() && issue.HasLabel(label))
            .ToList();

        return Task.FromResult<IReadOnlyList<GbIssue>>(matchingIssues);
    }

    public int CommentListCalls { get; private set; }

    public Task<GbIssue> GetIssueAsync(string repo, int number, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("issue-read", repo));

        return Task.FromResult(Issues[number]);
    }

    public Task<IReadOnlyList<GbComment>> ListCommentsAsync(string repo, int number, CancellationToken cancellationToken)
    {
        CommentListCalls++;
        RepoCalls.Add(("comments-read", repo));
        var comments = CommentsOf(number);
        return Task.FromResult(comments);
    }

    public Task<GbIssue> CreateIssueAsync(string repo, NewIssue issue, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("create-issue", repo));
        while (Issues.ContainsKey(++_nextNumber))
        {
        }

        var labels = issue.Labels.ToArray();
        var created = AddIssue(_nextNumber, issue.Title, issue.Body, labels);

        return Task.FromResult(created);
    }

    public Task<GbComment> AddCommentAsync(string repo, int number, string body, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("comment", repo));
        var markedBody = body.Contains(BotComments.Marker) ? body : BotComments.Marker + "\n" + body;
        var comment = AddRaw(number, markedBody, Login);
        OnCommentAdded?.Invoke(number);

        return Task.FromResult(comment);
    }

    public Task<GbComment> UpdateCommentAsync(
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

        throw new GitBucketException(System.Net.HttpStatusCode.NotFound, "comment not found");
    }

    public Task CloseIssueAsync(string repo, int number, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("close-issue", repo));
        if (CloseError is not null)
        {
            throw new GitBucketException(System.Net.HttpStatusCode.Unauthorized, CloseError);
        }

        Issues[number] = Issues[number] with
        {
            State = "closed"
        };

        return Task.CompletedTask;
    }

    public Task AddLabelsAsync(string repo, int number, IEnumerable<string> labels, CancellationToken cancellationToken)
    {
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

    public Task<IReadOnlyList<GbPull>> ListPullsAsync(string repo, string state, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("pulls", repo));
        var matchingPulls = Pulls
            .Where(pull => state == "all" || pull.State == state)
            .Where(pull => !PullRepos.TryGetValue(pull.Number, out var pullRepo) ||
                           string.Equals(pullRepo, repo, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Task.FromResult<IReadOnlyList<GbPull>>(matchingPulls);
    }

    public Task<GbPull> GetPullAsync(string repo, int number, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("pull-read", repo));
        var pull = Pulls.Single(existingPull => existingPull.Number == number);

        return Task.FromResult(pull);
    }

    public Task<GbPull> CreatePullAsync(string repo, NewPull newPull, CancellationToken cancellationToken)
    {
        RepoCalls.Add(("create-pull", repo));
        var highestPullNumber = Pulls.Select(existingPull => existingPull.Number).DefaultIfEmpty(0).Max();
        var number = Math.Max(_nextNumber, highestPullNumber) + 1;
        _nextNumber = number;

        var head = new GbRef(newPull.Head, "new0000");
        var baseRef = new GbRef(newPull.BaseBranch, "base000");
        var author = new GbUser(Login);
        var pull = new GbPull(
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
