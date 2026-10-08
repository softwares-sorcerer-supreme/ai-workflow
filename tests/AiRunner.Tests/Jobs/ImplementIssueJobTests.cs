using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Jobs;

public sealed class ImplementIssueJobTests : IDisposable
{
    private readonly JobTestKit _kit = new();
    private ImplementIssueJob Job => new(_kit.Config, _kit.GitBucket, _kit.Workspace, _kit.Git, _kit.Copilot, _kit.Processes, _kit.State);

    public ImplementIssueJobTests() => _kit.Copilot.FilesToWrite["PR_BODY.md"] = "Adds CSV export.";

    public void Dispose() => _kit.Dispose();

    private void SimpleIssueWithSpec()
    {
        _kit.GitBucket.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        _kit.GitBucket.AddBotComment(12, Markers.Wrap(Samples.Spec, Markers.Spec));
    }

    [Fact]
    public async Task Green_run_commits_pushes_and_opens_a_pr()
    {
        SimpleIssueWithSpec();

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(("origin/main", "ai/issue-12"), _kit.Workspace.Prepared.Single());
        Assert.Equal(["#12: Export CSV (Copilot)"], _kit.Git.Commits);
        Assert.Contains(("HEAD", "ai/issue-12"), _kit.Git.Pushes);

        var pr = _kit.GitBucket.Pulls.Single();
        Assert.Equal("Export CSV", pr.Title);
        Assert.Equal(("ai/issue-12", "main"), (pr.Head.Ref, pr.Base.Ref));
        Assert.Contains("Adds CSV export.", pr.Body);
        Assert.Contains("Closes #12", pr.Body);

        var labels = _kit.GitBucket.LabelsOf(12);
        Assert.Contains(Labels.Implementing, labels);
        Assert.DoesNotContain(Labels.Implement, labels);
        Assert.Contains("SPEC - issue #12", File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "plan.md")));

        var request = _kit.Copilot.Requests.Single();
        Assert.Equal(["write", "shell(make build)", "shell(make test)"], request.AllowTools);
        Assert.Contains("shell(git:*)", request.DenyTools);
        Assert.Equal(2, _kit.Processes.Commands.Count);
    }

    [Fact]
    public async Task Failing_tests_still_open_a_wip_pr()
    {
        SimpleIssueWithSpec();
        _kit.Processes.ExitCodes["make test"] = 1;

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("wip", outcome.Result);
        var pr = _kit.GitBucket.Pulls.Single();
        Assert.StartsWith("[WIP - tests failing]", pr.Title);
        Assert.StartsWith("**WIP", pr.Body!.Replace(BotComments.Marker, "").TrimStart());
    }

    [Fact]
    public async Task Too_big_change_is_not_pushed()
    {
        SimpleIssueWithSpec();
        _kit.Git.Staged = Enumerable.Range(0, 11).Select(i => new DiffStat($"f{i}.cs", 5, 0)).ToList();

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("too-big", outcome.Result);
        Assert.Empty(_kit.Git.Pushes);
        Assert.Empty(_kit.GitBucket.Pulls);
        Assert.DoesNotContain(Labels.Implementing, _kit.GitBucket.LabelsOf(12));
        Assert.DoesNotContain(Labels.Implement, _kit.GitBucket.LabelsOf(12));
    }

    [Fact]
    public async Task Not_simple_is_rejected()
    {
        _kit.GitBucket.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Complex, Labels.Implement);

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("rejected", outcome.Result);
        Assert.DoesNotContain(Labels.Implement, _kit.GitBucket.LabelsOf(12));
        Assert.Empty(_kit.Copilot.Requests);
    }

    [Fact]
    public async Task Branch_with_foreign_commits_is_refused()
    {
        SimpleIssueWithSpec();
        _kit.Git.Refs.Add("origin/ai/issue-12");
        _kit.Git.CommitsAhead["origin/ai/issue-12"] = 2;

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));
        Assert.Contains("already has commits", ex.Message);
        Assert.Empty(_kit.Copilot.Requests);
    }

    [Fact]
    public async Task Copilot_failure_removes_implementing_so_a_retry_is_possible()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.ExitCode = 2;

        await Assert.ThrowsAsync<JobFailedException>(() => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));
        Assert.DoesNotContain(Labels.Implementing, _kit.GitBucket.LabelsOf(12));
    }

    [Fact]
    public async Task Sub_issue_builds_on_the_feature_branch_after_the_previous_one_is_merged()
    {
        _kit.GitBucket.AddIssue(12, "Export", null, "ai", Labels.Parent);
        var t1 = new SubTask(1, "Entity", "Add entity", "Domain", [], false);
        var t2 = new SubTask(2, "Command", "Add command", "App", [1], false);
        _kit.GitBucket.AddIssue(20, SubIssueBody.Title(12, t1), SubIssueBody.Build(12, t1, 2, "Export"), "ai", Labels.SubIssue, Labels.Ready, Labels.Simple);
        _kit.GitBucket.AddIssue(21, SubIssueBody.Title(12, t2), SubIssueBody.Build(12, t2, 2, "Export"), "ai", Labels.SubIssue, Labels.Ready, Labels.Simple, Labels.Implement);
        _kit.GitBucket.AddBotComment(12, SubIssueBody.ListMarker([20, 21]));
        _kit.Git.Refs.Add("origin/feat/issue-12");

        // previous one not merged yet -> wait
        var waiting = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 21), _kit.Run, default);
        Assert.Equal("rejected", waiting.Result);

        _kit.GitBucket.AddPull(50, "ai/issue-12-1", "feat/issue-12", merged: true, state: "closed");
        await _kit.GitBucket.AddLabelsAsync(JobTestKit.Repo, 21, [Labels.Implement], default);

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 21), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Contains(("origin/feat/issue-12", "ai/issue-12-2"), _kit.Workspace.Prepared);
        var pr = _kit.GitBucket.Pulls.Single(p => p.State == "open");
        Assert.Equal(("ai/issue-12-2", "feat/issue-12"), (pr.Head.Ref, pr.Base.Ref));
        Assert.Contains("Shared contract", File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "plan.md")));
    }
}
