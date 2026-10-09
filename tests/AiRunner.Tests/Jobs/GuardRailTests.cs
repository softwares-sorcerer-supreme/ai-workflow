using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Jobs;

/// <summary>Safety and robustness paths added after the first code review.</summary>
public sealed class ImplementGuardRailTests : IDisposable
{
    private readonly JobTestKit _kit = new(repo => repo.Commands.Setup = "npm ci");
    private ImplementIssueJob Job =>
        new(_kit.Config, _kit.GitBucket, _kit.Loader, _kit.Workspace, _kit.Git, _kit.Session, _kit.Attachments);

    public ImplementGuardRailTests()
    {
        _kit.GitBucket.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        var specComment = Markers.Wrap(Samples.Spec, Markers.Spec);
        _kit.GitBucket.AddBotComment(12, specComment);
    }

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task Setup_runs_before_copilot_and_the_agent_may_run_it()
    {
        var jobRequest = _kit.Request(JobType.ImplementIssue, 12);
        await Job.RunAsync(jobRequest, _kit.Run, default);

        Assert.Contains("npm ci", _kit.Processes.Commands[0]);
        Assert.Contains("shell(npm:*)", _kit.Copilot.Requests.Single().AllowTools);
    }

    [Fact]
    public async Task Failing_setup_stops_before_copilot()
    {
        _kit.Processes.ExitCodes["npm ci"] = 1;

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains("npm ci", exception.Message);
        Assert.Empty(_kit.Copilot.Requests);
        Assert.DoesNotContain(Labels.Implementing, _kit.GitBucket.LabelsOf(12));
    }

    [Fact]
    public async Task Changed_git_config_discards_the_clone_and_pushes_nothing()
    {
        _kit.Git.Fingerprints.Enqueue("before");
        _kit.Git.Fingerprints.Enqueue("after-agent-wrote-it");

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains(".git/config", exception.Message);
        Assert.Equal([JobTestKit.Repo], _kit.Workspace.Discarded);
        Assert.Empty(_kit.Git.Commits);
        Assert.Empty(_kit.Git.Pushes);
    }

    [Fact]
    public async Task Changed_git_config_after_a_failed_copilot_run_saves_nothing()
    {
        _kit.Copilot.ExitCode = 2;
        _kit.Git.Fingerprints.Enqueue("before");
        _kit.Git.Fingerprints.Enqueue("after-agent-wrote-it");

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains(".git/config", exception.Message);
        Assert.Empty(_kit.Git.Commits);
        Assert.Empty(_kit.Git.Pushes);
    }

    [Fact]
    public async Task Open_pr_from_an_earlier_run_is_not_started_again()
    {
        _kit.GitBucket.AddPull(40, "ai/issue-12", "main");

        var jobRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(jobRequest, _kit.Run, default);

        Assert.Equal("already-open", outcome.Result);
        Assert.Empty(_kit.Copilot.Requests);
        Assert.DoesNotContain(Labels.Implement, _kit.GitBucket.LabelsOf(12));
        Assert.Contains(Labels.Implementing, _kit.GitBucket.LabelsOf(12));
    }

    [Fact]
    public async Task Parent_line_in_a_normal_issue_does_not_make_it_a_sub_issue()
    {
        _kit.GitBucket.Issues[12] = _kit.GitBucket.Issues[12] with
        {
            Body = "Parent: #3\nsee there"
        };

        var jobRequest = _kit.Request(JobType.ImplementIssue, 12);
        await Job.RunAsync(jobRequest, _kit.Run, default);

        Assert.Equal(("origin/main", "ai/issue-12"), _kit.Workspace.Prepared.Single());
    }
}

public sealed class ReviewGuardRailTests : IDisposable
{
    private readonly JobTestKit _kit = new();
    private ReviewPullRequestJob Job =>
        new(_kit.Config, _kit.GitBucket, _kit.Loader, _kit.Workspace, _kit.Git, _kit.Copilot, _kit.State, _kit.Attachments);

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task No_changed_files_means_no_copilot_run()
    {
        _kit.GitBucket.AddPull(30, "feature", "main", sha: "sha1", labels: "ai");
        _kit.Git.ChangedFiles = [];

        var jobRequest = _kit.Request(JobType.ReviewPullRequest, 30, "sha1");
        var outcome = await Job.RunAsync(jobRequest, _kit.Run, default);

        Assert.Equal("skipped", outcome.Result);
        Assert.Empty(_kit.Copilot.Requests);
    }

    [Fact]
    public async Task Sub_issue_pr_gets_the_sub_issue_body_as_its_spec()
    {
        var task = new SubTask(1, "Entity", "Add the Export entity", "Domain", [], false);
        var draft = new SubIssueDraft(12, task, 2, "entity Export");
        var subIssueBody = SubIssueBody.Build(draft);
        _kit.GitBucket.AddIssue(20, "#12.1 Entity", subIssueBody, "ai", Labels.SubIssue);
        _kit.GitBucket.AddPull(
            31,
            "ai/issue-12-1",
            "feat/issue-12",
            sha: "sha1",
            body: "Some text about #99.\n\nCloses #20",
            labels: "ai");
        var review = Samples.Review();
        _kit.Copilot.StdOut = Markers.Wrap(review, Markers.Review);

        var jobRequest = _kit.Request(JobType.ReviewPullRequest, 31, "sha1");
        await Job.RunAsync(jobRequest, _kit.Run, default);

        var contextFile = Path.Combine(_kit.Workspace.Dir, "context.md");
        var context = File.ReadAllText(contextFile);
        Assert.Contains("Linked issue #20", context);
        Assert.Contains("entity Export", context);
    }
}
