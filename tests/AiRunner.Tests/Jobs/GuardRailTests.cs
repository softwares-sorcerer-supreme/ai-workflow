using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRunner.Tests.Jobs;

/// <summary>Safety and robustness paths added after the first code review.</summary>
public sealed class ImplementGuardRailTests : IDisposable
{
    private readonly JobTestKit _kit = new(r => r.Commands.Setup = "npm ci");
    private ImplementIssueJob Job => new(_kit.Config, _kit.GitBucket, _kit.Workspace, _kit.Git, _kit.Copilot, _kit.Processes, _kit.State);

    public ImplementGuardRailTests()
    {
        _kit.GitBucket.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        _kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.Spec, Markers.Spec));
    }

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task Setup_runs_before_copilot_and_the_agent_may_run_it()
    {
        await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Contains("npm ci", _kit.Processes.Commands[0]);
        Assert.Contains("shell(npm ci)", _kit.Copilot.Requests.Single().AllowTools);
    }

    [Fact]
    public async Task Failing_setup_stops_before_copilot()
    {
        _kit.Processes.ExitCodes["npm ci"] = 1;

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));
        Assert.Contains("npm ci", ex.Message);
        Assert.Empty(_kit.Copilot.Requests);
        Assert.DoesNotContain(Labels.Implementing, _kit.GitBucket.LabelsOf(12));
    }

    [Fact]
    public async Task Changed_git_config_discards_the_clone_and_pushes_nothing()
    {
        _kit.Git.Fingerprints.Enqueue("before");
        _kit.Git.Fingerprints.Enqueue("after-agent-wrote-it");

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains(".git/config", ex.Message);
        Assert.Equal([JobTestKit.Repo], _kit.Workspace.Discarded);
        Assert.Empty(_kit.Git.Commits);
        Assert.Empty(_kit.Git.Pushes);
    }

    [Fact]
    public async Task Open_pr_from_an_earlier_run_is_not_started_again()
    {
        _kit.GitBucket.AddPull(40, "ai/issue-12", "main");

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("already-open", outcome.Result);
        Assert.Empty(_kit.Copilot.Requests);
        Assert.DoesNotContain(Labels.Implement, _kit.GitBucket.LabelsOf(12));
        Assert.Contains(Labels.Implementing, _kit.GitBucket.LabelsOf(12));
    }

    [Fact]
    public async Task Parent_line_in_a_normal_issue_does_not_make_it_a_sub_issue()
    {
        _kit.GitBucket.Issues[12] = _kit.GitBucket.Issues[12] with { Body = "Parent: #3\nsee there" };

        await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal(("origin/main", "ai/issue-12"), _kit.Workspace.Prepared.Single());
    }
}

public sealed class ReviewGuardRailTests : IDisposable
{
    private readonly JobTestKit _kit = new();
    private ReviewPullRequestJob Job => new(_kit.Config, _kit.GitBucket, _kit.Workspace, _kit.Git, _kit.Copilot, _kit.State, _kit.Identity);

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task No_changed_files_means_no_copilot_run()
    {
        _kit.GitBucket.AddPull(30, "feature", "main", sha: "sha1");
        _kit.Git.ChangedFiles = [];

        var outcome = await Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 30, "sha1"), _kit.Run, default);

        Assert.Equal("skipped", outcome.Result);
        Assert.Empty(_kit.Copilot.Requests);
    }

    [Fact]
    public async Task Sub_issue_pr_gets_the_sub_issue_body_as_its_spec()
    {
        var task = new SubTask(1, "Entity", "Add the Export entity", "Domain", [], false);
        _kit.GitBucket.AddIssue(20, "#12.1 Entity", SubIssueBody.Build(12, task, 2, "entity Export"), "ai", Labels.SubIssue);
        _kit.GitBucket.AddPull(31, "ai/issue-12-1", "feat/issue-12", sha: "sha1", body: "Some text about #99.\n\nCloses #20");
        _kit.Copilot.StdOut = Markers.Wrap(Samples.Review(), Markers.Review);

        await Job.RunAsync(_kit.Request(JobType.ReviewPullRequest, 31, "sha1"), _kit.Run, default);

        var context = File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "context.md"));
        Assert.Contains("Linked issue #20", context);
        Assert.Contains("entity Export", context);
    }
}
