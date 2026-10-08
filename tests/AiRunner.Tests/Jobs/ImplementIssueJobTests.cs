using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Jobs;

public sealed class ImplementIssueJobTests : IDisposable
{
    private readonly JobTestKit _kit = new();
    private ImplementIssueJob Job => new(_kit.Config, _kit.GitBucket, _kit.Workspace, _kit.Git, _kit.Session, _kit.State);

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
        Assert.Equal(["write", "shell(make:*)"], request.AllowTools);
        Assert.Contains("shell(git:*)", request.DenyTools);
        Assert.Contains("write(.ai-workflow)", request.DenyTools);
        Assert.Equal(2, _kit.Processes.Commands.Count);
        Assert.Contains("### Rounds by ai-runner", pr.Body);
        Assert.Contains("1 copilot calls", outcome.Detail);
    }

    [Fact]
    public async Task Failing_tests_still_open_a_wip_pr()
    {
        SimpleIssueWithSpec();
        _kit.Processes.ExitCodes["make test"] = 1;

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("wip", outcome.Result);
        var pr = _kit.GitBucket.Pulls.Single();
        Assert.StartsWith("[WIP - checks failing]", pr.Title);
        Assert.StartsWith("**WIP", pr.Body!.Replace(BotComments.Marker, "").TrimStart());
    }

    [Fact]
    public async Task Too_big_change_is_saved_on_the_branch_without_a_pr()
    {
        SimpleIssueWithSpec();
        _kit.Git.Staged = Enumerable.Range(0, 16).Select(i => new DiffStat($"f{i}.cs", 5, 0)).ToList();

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("too-big", outcome.Result);
        Assert.Contains(("HEAD", "ai/issue-12"), _kit.Git.Pushes);
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
        _kit.Git.Refs["origin/ai/issue-12"] = "saved01";
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
    public async Task Copilot_failure_still_pushes_the_partial_work_without_a_pr()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.ExitCode = 2;

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains("saved on branch `ai/issue-12`", ex.Message);
        Assert.Contains(("HEAD", "ai/issue-12"), _kit.Git.Pushes);
        Assert.Empty(_kit.GitBucket.Pulls);
        Assert.Empty(_kit.Processes.Commands);
    }

    [Fact]
    public async Task Copilot_timeout_still_pushes_the_partial_work()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.ExitCode = 1;
        _kit.Copilot.TimedOut = true;

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains("did not finish", ex.Message);
        Assert.Contains(("HEAD", "ai/issue-12"), _kit.Git.Pushes);
    }

    [Fact]
    public async Task Copilot_failure_without_files_pushes_nothing()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.ExitCode = 2;
        _kit.Git.Staged = [];

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains("no files", ex.Message);
        Assert.Empty(_kit.Git.Pushes);
        Assert.Empty(_kit.Git.Commits);
    }

    [Fact]
    public async Task Branch_with_only_runner_commits_is_continued_not_refused()
    {
        SimpleIssueWithSpec();
        _kit.Git.Refs["origin/ai/issue-12"] = "saved01";
        _kit.Git.CommitsAhead["origin/ai/issue-12"] = 1;
        _kit.Git.Authors = [_kit.Config.Git.UserEmail];

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(("origin/ai/issue-12", "ai/issue-12"), _kit.Workspace.Prepared.Last());
        Assert.Contains("Earlier work from a previous run", File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "plan.md")));
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
        _kit.Git.Refs["origin/feat/issue-12"] = "saved01";

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

    [Fact]
    public async Task Failing_check_gets_a_fix_round_and_then_a_normal_pr()
    {
        SimpleIssueWithSpec();
        _kit.Processes.ExitCodeSequence["make test"] = new Queue<int>([1, 0]);

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal([ImplementIssueJob.Prompt, ImplementIssueJob.FixPrompt], _kit.Copilot.Requests.Select(r => r.Prompt));
        Assert.Contains("make test", File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "fix.md")));   // layout: FixContextTests
        Assert.Equal(["origin/main", "origin/main"], _kit.Git.StageAndDiffs);   // diff.patch, then "did the code change?"
        Assert.True(File.Exists(Path.Combine(_kit.Workspace.Dir, "diff.patch")));
        Assert.Equal(["#12: Export CSV (Copilot)"], _kit.Git.Commits);

        var pr = _kit.GitBucket.Pulls.Single();
        Assert.Equal("Export CSV", pr.Title);
        Assert.Contains("Round 1 (implement): `make build` passed, `make test` failed (exit 1)", pr.Body);
        Assert.Contains("Round 2 (fix): `make build` passed, `make test` passed", pr.Body);

        // Logs: fix-N.md is the input of round N, each round has its checks and its transcript.
        Assert.True(File.Exists(Path.Combine(_kit.Run.Dir, "fix-2.md")));
        Assert.True(File.Exists(Path.Combine(_kit.Run.Dir, "round-1-check-2.txt")));
        Assert.True(File.Exists(Path.Combine(_kit.Run.Dir, "round-2-copilot-stdout.txt")));
        Assert.Equal(Path.Combine(_kit.Run.Dir, "round-2-transcript.md"), _kit.Copilot.Requests[1].TranscriptFile);
    }

    [Fact]
    public async Task Fix_round_that_changed_no_code_keeps_the_checks_of_the_round_before()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.FilesToWrite["PR_BODY.md"] = "Adds CSV export.";
        _kit.Git.FixedDiff = "diff --git a/src/a.txt b/src/a.txt\n";

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(2, _kit.Copilot.Requests.Count);
        Assert.Equal(2, _kit.Processes.Commands.Count);   // build and test of round 1 only
        Assert.Contains("Round 2 (fix): `make build` passed, `make test` passed", _kit.GitBucket.Pulls.Single().Body);
    }

    [Fact]
    public async Task Checks_still_failing_after_the_last_fix_round_open_a_wip_pr()
    {
        SimpleIssueWithSpec();
        _kit.RepoConfig.Implement.MaxFixRounds = 1;
        _kit.Processes.ExitCodes["make test"] = 1;

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("wip", outcome.Result);
        Assert.Equal(2, _kit.Copilot.Requests.Count);
        Assert.StartsWith("[WIP - checks failing]", _kit.GitBucket.Pulls.Single().Title);
    }

    [Fact]
    public async Task No_fix_rounds_configured_means_one_copilot_run()
    {
        SimpleIssueWithSpec();
        _kit.RepoConfig.Implement.MaxFixRounds = 0;
        _kit.Processes.ExitCodes["make test"] = 1;

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("wip", outcome.Result);
        Assert.Single(_kit.Copilot.Requests);
    }

    [Fact]
    public async Task Failed_build_skips_the_other_checks()
    {
        SimpleIssueWithSpec();
        _kit.RepoConfig.Implement.MaxFixRounds = 0;
        _kit.RepoConfig.Commands.Lint = "make lint";
        _kit.Processes.ExitCodes["make build"] = 1;

        await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Single(_kit.Processes.Commands);
    }

    [Fact]
    public async Task Lint_is_run_by_the_runner_and_its_tool_is_allowed_for_the_agent()
    {
        SimpleIssueWithSpec();
        _kit.RepoConfig.Commands.Lint = "npx eslint . && dotnet format --verify-no-changes";
        _kit.Processes.ExitCodeSequence["eslint"] = new Queue<int>([1, 0]);

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Contains(_kit.Processes.Commands, c => c.Contains("eslint"));
        Assert.Equal(["write", "shell(make:*)", "shell(npx:*)", "shell(dotnet:*)"], _kit.Copilot.Requests[0].AllowTools);
        Assert.Contains("eslint", File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "fix.md")));
    }

    [Fact]
    public async Task Missing_pr_body_gets_a_fix_round()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.FilesToWrite.Clear();
        _kit.Copilot.OnRun = (r, call) =>
        {
            if (call == 2) File.WriteAllText(Path.Combine(r.WorkDir, "PR_BODY.md"), Samples.PrBody());
        };

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(2, _kit.Copilot.Requests.Count);
        Assert.Contains("PR_BODY.md is missing", File.ReadAllText(Path.Combine(_kit.Workspace.Dir, "fix.md")));
        Assert.DoesNotContain("PR description problems", _kit.GitBucket.Pulls.Single().Body);
    }

    [Fact]
    public async Task Pr_body_still_incomplete_after_the_rounds_is_listed_in_the_pr()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.FilesToWrite["PR_BODY.md"] = "Adds CSV export.";

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        // Checks pass, only the text is wrong: one fix round, not every round.
        Assert.Equal("ok", outcome.Result);
        Assert.Equal(2, _kit.Copilot.Requests.Count);
        var body = _kit.GitBucket.Pulls.Single().Body!;
        Assert.Contains("Adds CSV export.", body);
        Assert.Contains("### PR description problems", body);
        Assert.Contains("The heading `### What changed` is missing.", body);
    }

    [Fact]
    public async Task Copilot_failure_in_a_fix_round_still_opens_the_pr_and_stops_the_rounds()
    {
        SimpleIssueWithSpec();
        _kit.Processes.ExitCodes["make test"] = 1;
        _kit.Copilot.OnRun = (_, call) => { if (call == 2) _kit.Copilot.ExitCode = 2; };

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.Equal("wip", outcome.Result);
        Assert.Equal(2, _kit.Copilot.Requests.Count);   // maxFixRounds is 2, but a failed call ends the rounds
        Assert.Contains(("HEAD", "ai/issue-12"), _kit.Git.Pushes);
        var pr = _kit.GitBucket.Pulls.Single();
        Assert.StartsWith("[WIP - checks failing]", pr.Title);
        Assert.Contains("Round 2 (fix): Copilot failed (Copilot exited with code 2", pr.Body);
    }

    [Fact]
    public async Task Changed_git_config_in_a_fix_round_discards_the_clone_and_pushes_nothing()
    {
        SimpleIssueWithSpec();
        _kit.Processes.ExitCodes["make test"] = 1;
        // setup baseline, after round 1, after the checks of round 1, after the fix round
        foreach (var f in new[] { "c1", "c1", "c1", "changed" }) _kit.Git.Fingerprints.Enqueue(f);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains(".git/config", ex.Message);
        Assert.Equal([JobTestKit.Repo], _kit.Workspace.Discarded);
        Assert.Empty(_kit.Git.Pushes);
    }
}
