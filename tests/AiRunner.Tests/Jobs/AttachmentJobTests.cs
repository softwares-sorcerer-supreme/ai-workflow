using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Jobs;

/// <summary>The files people attach to issues and PRs reach the agent: saved in the clone, listed, passed on.</summary>
public sealed class AttachmentJobTests : IDisposable
{
    private const string Base = JobTestKit.BaseUrl + "/james/demo/_attached/";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    private readonly JobTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private string AttachmentFolder => Path.Combine(_kit.Workspace.Dir, ".ai-workflow", "attachments");

    private string ReadWorkFile(string name) => File.ReadAllText(Path.Combine(_kit.Workspace.Dir, name));

    private async Task<AttachmentSet> FetchAsync(string label, string text)
    {
        var sources = new[] { new AttachmentSource(label, text) };
        var request = new AttachmentRequest(_kit.Workspace.Dir, sources);

        return await _kit.Attachments.FetchAsync(request, _kit.Run, default);
    }

    private AnalyzeIssueJob AnalyzeJob =>
        new(_kit.Config, _kit.CodeHost, _kit.Loader, _kit.Workspace, _kit.Copilot, _kit.State, _kit.Attachments);

    // ---- the fetcher ----

    [Fact]
    public async Task Fetcher_saves_files_lists_them_and_passes_images_and_pdfs_only()
    {
        _kit.CodeHost.AddAttachment(Base + "img1", Png, "screen.png");
        _kit.CodeHost.AddAttachment(Base + "log1", [65, 66], "run.log");
        var body = $"![screen]({Base}img1) and [run.log]({Base}log1)";

        var set = await FetchAsync("the issue body", body);

        Assert.Equal(2, set.Results.Count);
        Assert.True(File.Exists(Path.Combine(AttachmentFolder, "1-img1.png")));
        Assert.True(File.Exists(Path.Combine(AttachmentFolder, "2-log1.log")));
        Assert.Equal([Path.Combine(AttachmentFolder, "1-img1.png")], set.ForCopilot);
        Assert.Contains("`.ai-workflow/attachments/1-img1.png` - \"screen\", image", set.Section);
        Assert.Contains("run.log\", text", set.Section);
    }

    [Fact]
    public async Task Fetcher_does_not_pass_files_when_the_cli_has_no_attachment_flag()
    {
        _kit.Copilot.CanAttachFiles = false;
        _kit.CodeHost.AddAttachment(Base + "img1", Png, "screen.png");

        var set = await FetchAsync("the issue body", $"![]({Base}img1)");

        Assert.Empty(set.ForCopilot);
        Assert.DoesNotContain(", also in the prompt", set.Section);
        Assert.Contains("1-img1.png", set.Section);
    }

    [Fact]
    public async Task Fetcher_stops_at_the_prompt_cap_but_still_saves_the_rest()
    {
        _kit.Config.Attachments.MaxPassedToCopilot = 1;
        _kit.CodeHost.AddAttachment(Base + "a", Png, "a.png");
        _kit.CodeHost.AddAttachment(Base + "b", Png, "b.png");

        var set = await FetchAsync("the issue body", $"![]({Base}a) ![]({Base}b)");

        Assert.Single(set.ForCopilot);
        Assert.Equal(2, set.Results.Count(result => result.Problem is null));
    }

    [Fact]
    public async Task Fetcher_ignores_other_hosts_and_never_fetches_a_link_twice()
    {
        _kit.CodeHost.AddAttachment(Base + "a", Png, "a.png");
        var text = $"![]({Base}a) again ![]({Base}a) ![](http://evil.test/james/demo/_attached/x)";

        var set = await FetchAsync("the issue body", text);

        Assert.Single(set.Results);
        Assert.Equal([Base + "a"], _kit.CodeHost.Downloads);
    }

    [Fact]
    public async Task Fetcher_reports_a_failed_download_instead_of_failing()
    {
        var set = await FetchAsync("comment 7 by alice", $"[gone.pdf]({Base}missing)");

        var result = Assert.Single(set.Results);
        Assert.Contains("404", result.Problem);
        Assert.Contains("\"gone.pdf\" (comment 7 by alice): not available - the server answered 404", set.Section);
    }

    [Fact]
    public async Task Fetcher_never_downloads_executables()
    {
        _kit.CodeHost.AddAttachment(Base + "tool", [1], "tool.exe");

        var set = await FetchAsync("the issue body", $"[tool.exe]({Base}tool)");

        Assert.Empty(_kit.CodeHost.Downloads);
        Assert.Contains(".exe files are never fetched", set.Results.Single().Problem);
    }

    [Fact]
    public async Task Fetcher_deletes_a_file_whose_real_type_is_on_the_skip_list()
    {
        _kit.CodeHost.AddAttachment(Base + "x1", [1], "setup.msi");

        var set = await FetchAsync("the issue body", $"![look]({Base}x1)");

        Assert.Contains(".msi files are never fetched", set.Results.Single().Problem);
        Assert.Empty(Directory.GetFiles(AttachmentFolder));
    }

    [Fact]
    public async Task Fetcher_respects_the_file_count_limit()
    {
        _kit.Config.Attachments.MaxFiles = 1;
        _kit.CodeHost.AddAttachment(Base + "a", Png, "a.png");
        _kit.CodeHost.AddAttachment(Base + "b", Png, "b.png");

        var set = await FetchAsync("the issue body", $"![]({Base}a) ![]({Base}b)");

        Assert.Null(set.Results[0].Problem);
        Assert.Contains("more than 1 attachments", set.Results[1].Problem);
        Assert.Equal([Base + "a"], _kit.CodeHost.Downloads);
    }

    [Fact]
    public async Task Fetcher_counts_failed_downloads_against_the_limit()
    {
        _kit.Config.Attachments.MaxFiles = 2;
        var links = Enumerable.Range(1, 6).Select(number => $"[f{number}.pdf]({Base}dead{number})");
        var deadLinks = string.Join(' ', links);

        var set = await FetchAsync("the issue body", deadLinks);

        Assert.Equal(2, _kit.CodeHost.Downloads.Count);
        Assert.Equal(6, set.Results.Count);
        Assert.Contains("more than 2 attachments", set.Results[5].Problem);
    }

    [Fact]
    public async Task Fetcher_treats_an_embedded_image_with_an_unknown_type_as_an_image()
    {
        _kit.CodeHost.AddAttachment(Base + "blob1", Png, null, "application/octet-stream");

        var set = await FetchAsync("the issue body", $"![shot]({Base}blob1)");

        var result = Assert.Single(set.Results);
        Assert.Equal(AttachmentKind.Image, result.Kind);
        Assert.True(result.PassToCopilot);
    }

    [Fact]
    public async Task Fetcher_does_nothing_when_switched_off()
    {
        _kit.Config.Attachments.Enabled = false;
        _kit.CodeHost.AddAttachment(Base + "a", Png, "a.png");

        var set = await FetchAsync("the issue body", $"![]({Base}a)");

        Assert.Empty(set.Results);
        Assert.Equal("", set.Section);
        Assert.Empty(_kit.CodeHost.Downloads);
    }

    // ---- the jobs ----

    [Fact]
    public async Task Analysis_reads_attachments_of_the_issue_and_human_comments_but_not_of_the_bot()
    {
        _kit.CodeHost.AddAttachment(Base + "img1", Png, "screen.png");
        _kit.CodeHost.AddAttachment(Base + "log1", [65], "run.log");
        _kit.CodeHost.AddAttachment(Base + "bot1", Png, "bot.png");
        _kit.CodeHost.AddIssue(12, "Export CSV", $"Wrong layout: ![screen]({Base}img1)", "ai", Labels.QuestionsOpen);
        _kit.CodeHost.AddHumanComment(12, $"Log: [run.log]({Base}log1)", "bob");
        _kit.CodeHost.AddBotComment(12, $"My own picture ![]({Base}bot1)");
        _kit.Copilot.StdOut = Samples.AgentOutput(Samples.Analysis("ready", "simple"), Samples.Spec);
        var jobRequest = _kit.Request(JobType.AnalyzeIssue, 12);

        var outcome = await AnalyzeJob.RunAsync(jobRequest, _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal([Base + "img1", Base + "log1"], _kit.CodeHost.Downloads);

        var context = ReadWorkFile("context.md");
        Assert.Contains("## Attachments", context);
        Assert.Contains("from the issue body, also in the prompt", context);
        Assert.Contains("from comment", context);
        Assert.Equal([Path.Combine(AttachmentFolder, "1-img1.png")], _kit.Copilot.Requests.Single().Attachments);
    }

    [Fact]
    public async Task Analysis_without_attachments_is_unchanged()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", "Please add CSV export", "ai", Labels.QuestionsOpen);
        _kit.Copilot.StdOut = Samples.AgentOutput(Samples.Analysis("ready", "simple"), Samples.Spec);
        var jobRequest = _kit.Request(JobType.AnalyzeIssue, 12);

        await AnalyzeJob.RunAsync(jobRequest, _kit.Run, default);

        Assert.DoesNotContain("## Attachments", ReadWorkFile("context.md"));
        Assert.Empty(_kit.Copilot.Requests.Single().Attachments);
    }

    [Fact]
    public async Task Review_sees_human_comments_and_the_files_in_them()
    {
        _kit.CodeHost.AddAttachment(Base + "mock1", Png, "mockup.png");
        _kit.CodeHost.AddPull(30, "feat/x", "main", sha: "sha1", body: "Adds the export", labels: "ai");
        _kit.CodeHost.AddHumanComment(30, $"It should look like ![mockup]({Base}mock1)", "pm");
        _kit.Copilot.StdOut = Markers.Wrap(Samples.Review(), Markers.Review);
        var job = new ReviewPullRequestJob(
            _kit.Config,
            _kit.CodeHost,
            _kit.Loader,
            _kit.Workspace,
            _kit.Git,
            _kit.Copilot,
            _kit.State,
            _kit.Attachments);
        var jobRequest = _kit.Request(JobType.ReviewPullRequest, 30, "sha1");

        await job.RunAsync(jobRequest, _kit.Run, default);

        var context = ReadWorkFile("context.md");
        Assert.Contains("## Comments on the pull request", context);
        Assert.Contains("It should look like", context);
        Assert.Contains(".ai-workflow/attachments/1-mock1.png", context);
        Assert.Equal([Path.Combine(AttachmentFolder, "1-mock1.png")], _kit.Copilot.Requests.Single().Attachments);
    }

    [Fact]
    public async Task Review_reads_the_comments_once_and_still_updates_its_earlier_comment()
    {
        _kit.CodeHost.AddPull(30, "feat/x", "main", sha: "sha1", body: "Adds the export", labels: "ai");
        var earlier = _kit.CodeHost.AddBotComment(30, Markers.Wrap(Samples.Review(), Markers.Review));
        _kit.Copilot.StdOut = Markers.Wrap(Samples.Review(), Markers.Review);
        var job = new ReviewPullRequestJob(
            _kit.Config,
            _kit.CodeHost,
            _kit.Loader,
            _kit.Workspace,
            _kit.Git,
            _kit.Copilot,
            _kit.State,
            _kit.Attachments);
        var jobRequest = _kit.Request(JobType.ReviewPullRequest, 30, "sha1");

        await job.RunAsync(jobRequest, _kit.Run, default);

        Assert.Equal(earlier.Id, _kit.CodeHost.Updated.Single().Id);
        Assert.Single(_kit.CodeHost.CommentsOf(30));
    }

    [Fact]
    public async Task Implementation_plan_lists_the_attachments_and_round_one_gets_the_files()
    {
        _kit.CodeHost.AddAttachment(Base + "mock1", Png, "mockup.png");
        _kit.CodeHost.AddIssue(
            12, "Export CSV", $"Design: ![]({Base}mock1)", "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        _kit.CodeHost.AddBotComment(12, Markers.Wrap(Samples.Spec, Markers.Spec));
        var job = new ImplementIssueJob(
            _kit.Config, _kit.CodeHost, _kit.Loader, _kit.Workspace, _kit.Git, _kit.Session, _kit.Attachments);
        var jobRequest = _kit.Request(JobType.ImplementIssue, 12);

        var outcome = await job.RunAsync(jobRequest, _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Contains("## Attachments", ReadWorkFile("plan.md"));
        Assert.Equal([Path.Combine(AttachmentFolder, "1-mock1.png")], _kit.Copilot.Requests.First().Attachments);
    }

    [Fact]
    public async Task Review_fix_reads_attachments_of_the_new_requests()
    {
        _kit.CodeHost.AddAttachment(Base + "err1", Png, "error.png");
        _kit.CodeHost.AddIssue(12, "Export", null, "ai");
        _kit.CodeHost.AddBotComment(12, Markers.Wrap(Samples.Spec, Markers.Spec));
        _kit.CodeHost.AddPull(30, "ai/issue-12", "main", sha: "sha1", body: "Closes #12", labels: "ai");
        _kit.Git.Refs["origin/ai/issue-12"] = "sha1";
        _kit.CodeHost.AddBotComment(30, Markers.Wrap(Samples.Review(), Markers.Review));
        var trigger = _kit.CodeHost.AddHumanComment(30, $"@ai the error looks like this ![]({Base}err1)");
        _kit.Copilot.FilesToWrite["PR_BODY.md"] = Samples.PrBody();
        var job = new FixReviewJob(
            _kit.Config,
            _kit.CodeHost,
            _kit.Loader,
            _kit.Workspace,
            _kit.Git,
            _kit.Session,
            _kit.State,
            _kit.Attachments);
        var jobRequest = _kit.Request(JobType.FixReview, 30, trigger.Id.ToString());

        await job.RunAsync(jobRequest, _kit.Run, default);

        Assert.Contains("## Attachments", ReadWorkFile("plan.md"));
        Assert.Equal([Path.Combine(AttachmentFolder, "1-err1.png")], _kit.Copilot.Requests.First().Attachments);
    }
}
