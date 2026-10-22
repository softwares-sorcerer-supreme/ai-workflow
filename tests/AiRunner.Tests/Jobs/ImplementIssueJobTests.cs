using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.Jobs;

public sealed class ImplementIssueJobTests : IDisposable
{
    private readonly JobTestKit _kit = new();
    private ImplementIssueJob Job =>
        new(_kit.Config, _kit.CodeHost, _kit.Loader, _kit.Workspace, _kit.Git, _kit.Session, _kit.Attachments);

    public void Dispose() => _kit.Dispose();

    private void SimpleIssueWithSpec()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Simple, Labels.Implement);
        var specComment = Markers.Wrap(Samples.Spec, Markers.Spec);
        _kit.CodeHost.AddBotComment(12, specComment);
    }

    [Fact]
    public async Task Green_run_commits_pushes_and_opens_a_pr()
    {
        SimpleIssueWithSpec();

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(("origin/main", "ai/issue-12"), _kit.Workspace.Prepared.Single());
        // A simple issue reads the others at their default branch.
        Assert.Null(_kit.Workspace.Requests.Single().OthersAt);
        Assert.Equal(["#12: Export CSV (Copilot)"], _kit.Git.Commits);
        Assert.Contains(("HEAD", "ai/issue-12"), _kit.Git.Pushes);

        var pull = _kit.CodeHost.Pulls.Single();
        Assert.Equal("Export CSV", pull.Title);
        Assert.Equal(("ai/issue-12", "main"), (pull.Head.Ref, pull.Base.Ref));
        Assert.Contains("Adds CSV export.", pull.Body);
        Assert.Contains("Issue: james/demo#12", pull.Body);
        Assert.DoesNotContain("Closes", pull.Body);

        var labels = _kit.CodeHost.LabelsOf(12);
        Assert.Contains(Labels.Implementing, labels);
        Assert.DoesNotContain(Labels.Implement, labels);
        var planFile = Path.Combine(_kit.Workspace.Dir, "plan.md");
        Assert.Contains("SPEC - issue #12", File.ReadAllText(planFile));

        var request = _kit.Copilot.Requests.Single();
        Assert.Contains("AGENTS.md in this folder", request.Prompt);
        Assert.Equal(["write", "shell(make:*)"], request.AllowTools);
        Assert.Contains("shell(git:*)", request.DenyTools);
        Assert.Contains("write(.ai-workflow)", request.DenyTools);
        Assert.Equal(2, _kit.Processes.Commands.Count);
        Assert.Contains("### Rounds by ai-runner", pull.Body);
        Assert.Contains("1 copilot calls", outcome.Detail);
        Assert.Equal(1, outcome.CopilotCalls);
    }

    [Fact]
    public async Task Closing_keywords_written_by_the_agent_are_not_in_the_pr_body()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.FilesToWrite["PR_BODY.md"] = Samples.PrBody().Replace("Adds CSV export.", "Adds CSV export. Closes #12");

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        await Job.RunAsync(implementRequest, _kit.Run, default);

        var pull = _kit.CodeHost.Pulls.Single();
        Assert.DoesNotContain("Closes", pull.Body);
        Assert.Contains("Refs #12", pull.Body);
        Assert.Contains("Issue: james/demo#12", pull.Body);
    }

    [Fact]
    public async Task Failing_tests_still_open_a_wip_pr()
    {
        SimpleIssueWithSpec();
        _kit.Processes.ExitCodes["make test"] = 1;

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("wip", outcome.Result);

        var pull = _kit.CodeHost.Pulls.Single();
        Assert.StartsWith("[WIP - checks failing]", pull.Title);
        Assert.StartsWith("**WIP", pull.Body!.Replace(BotComments.Marker, "").TrimStart());
    }

    [Fact]
    public async Task Too_big_change_is_saved_on_the_branch_without_a_pr()
    {
        SimpleIssueWithSpec();
        _kit.Git.Staged = Enumerable.Range(0, 16)
            .Select(index => new DiffStat($"f{index}.cs", 5, 0))
            .ToList();

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("too-big", outcome.Result);
        Assert.Contains(("HEAD", "ai/issue-12"), _kit.Git.Pushes);
        Assert.Empty(_kit.CodeHost.Pulls);
        Assert.DoesNotContain(Labels.Implementing, _kit.CodeHost.LabelsOf(12));
        Assert.DoesNotContain(Labels.Implement, _kit.CodeHost.LabelsOf(12));
    }

    [Fact]
    public async Task Too_big_sub_issue_gets_failed_so_the_sequencer_does_not_start_it_again()
    {
        var task = new SubTask(1, Samples.DefaultComponent, "Task", "Do the work.", "src/x.cs", [], false);
        var body = SubIssueBody.Build(new SubIssueDraft(12, task, 3, "contract"));
        _kit.CodeHost.AddIssue(
            21, "#12.1 Task", body, "ai", Labels.SubIssue, Labels.Ready, Labels.Simple, Labels.Implement);
        _kit.Git.Staged = Enumerable.Range(0, 16)
            .Select(index => new DiffStat($"f{index}.cs", 5, 0))
            .ToList();

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 21), _kit.Run, default);

        Assert.Equal("too-big", outcome.Result);
        Assert.Contains(Labels.Failed, _kit.CodeHost.LabelsOf(21));
        Assert.DoesNotContain(Labels.Implement, _kit.CodeHost.LabelsOf(21));
        Assert.DoesNotContain(Labels.Implementing, _kit.CodeHost.LabelsOf(21));
        Assert.Contains("cannot be split again", _kit.CodeHost.CommentsOf(21).Last().Body);
    }

    [Fact]
    public async Task Too_big_simple_issue_does_not_get_failed_so_the_author_can_ask_for_a_breakdown()
    {
        SimpleIssueWithSpec();
        _kit.Git.Staged = Enumerable.Range(0, 16)
            .Select(index => new DiffStat($"f{index}.cs", 5, 0))
            .ToList();

        await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default);

        Assert.DoesNotContain(Labels.Failed, _kit.CodeHost.LabelsOf(12));
    }

    [Fact]
    public async Task Not_simple_is_rejected()
    {
        _kit.CodeHost.AddIssue(12, "Export CSV", "body", "ai", Labels.Ready, Labels.Complex, Labels.Implement);

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("rejected", outcome.Result);
        Assert.DoesNotContain(Labels.Implement, _kit.CodeHost.LabelsOf(12));
        Assert.Empty(_kit.Copilot.Requests);
    }

    [Fact]
    public async Task Branch_with_foreign_commits_is_refused()
    {
        SimpleIssueWithSpec();
        _kit.Git.Refs["origin/ai/issue-12"] = "saved01";
        _kit.Git.CommitsAhead["origin/ai/issue-12"] = 2;

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains("already has commits", exception.Message);
        Assert.Empty(_kit.Copilot.Requests);
    }

    [Fact]
    public async Task Copilot_failure_removes_implementing_so_a_retry_is_possible()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.ExitCode = 2;

        await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.DoesNotContain(Labels.Implementing, _kit.CodeHost.LabelsOf(12));
    }

    [Fact]
    public async Task Copilot_failure_still_pushes_the_partial_work_without_a_pr()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.ExitCode = 2;

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains("saved on branch `ai/issue-12`", exception.Message);
        Assert.Contains(("HEAD", "ai/issue-12"), _kit.Git.Pushes);
        Assert.Empty(_kit.CodeHost.Pulls);
        Assert.Empty(_kit.Processes.Commands);
    }

    [Fact]
    public async Task Copilot_timeout_still_pushes_the_partial_work()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.ExitCode = 1;
        _kit.Copilot.TimedOut = true;

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains("did not finish", exception.Message);
        Assert.Contains(("HEAD", "ai/issue-12"), _kit.Git.Pushes);
    }

    [Fact]
    public async Task Copilot_failure_without_files_pushes_nothing()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.ExitCode = 2;
        _kit.Git.Staged = [];

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains("no files", exception.Message);
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

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(("origin/ai/issue-12", "ai/issue-12"), _kit.Workspace.Prepared.Last());
        var planFile = Path.Combine(_kit.Workspace.Dir, "plan.md");
        Assert.Contains("Earlier work from a previous run", File.ReadAllText(planFile));
    }

    [Fact]
    public async Task Sub_issue_builds_on_the_feature_branch_after_the_previous_one_is_merged()
    {
        _kit.CodeHost.AddIssue(12, "Export", null, "ai", Labels.Parent);
        var firstTask = new SubTask(1, "demo", "Entity", "Add entity", "Domain", [], false);
        var secondTask = new SubTask(2, "demo", "Command", "Add command", "App", [1], false);
        var firstTitle = SubIssueBody.Title(12, firstTask);
        var firstDraft = new SubIssueDraft(12, firstTask, 2, "Export");
        var firstBody = SubIssueBody.Build(firstDraft);
        _kit.CodeHost.AddIssue(
            20,
            firstTitle,
            firstBody,
            "ai",
            Labels.SubIssue, Labels.Ready, Labels.Simple);

        var secondTitle = SubIssueBody.Title(12, secondTask);
        var secondDraft = new SubIssueDraft(12, secondTask, 2, "Export");
        var secondBody = SubIssueBody.Build(secondDraft);
        _kit.CodeHost.AddIssue(
            21,
            secondTitle,
            secondBody,
            "ai",
            Labels.SubIssue, Labels.Ready, Labels.Simple, Labels.Implement);
        var subIssueList = SubIssueBody.ListMarker([20, 21]);
        _kit.CodeHost.AddBotComment(12, subIssueList);
        _kit.Git.Refs["origin/feat/issue-12"] = "saved01";

        // previous one not merged yet -> wait
        var waitingRequest = _kit.Request(JobType.ImplementIssue, 21);
        var waiting = await Job.RunAsync(waitingRequest, _kit.Run, default);
        Assert.Equal("rejected", waiting.Result);

        _kit.CodeHost.AddPull(50, "ai/issue-12-1", "feat/issue-12", merged: true, state: "closed");
        await _kit.CodeHost.AddLabelsAsync(JobTestKit.Repo, 21, [Labels.Implement], default);

        var retryRequest = _kit.Request(JobType.ImplementIssue, 21);
        var outcome = await Job.RunAsync(retryRequest, _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Contains(("origin/feat/issue-12", "ai/issue-12-2"), _kit.Workspace.Prepared);

        var pull = _kit.CodeHost.Pulls.Single(candidate => candidate.State == "open");
        Assert.Equal(("ai/issue-12-2", "feat/issue-12"), (pull.Head.Ref, pull.Base.Ref));
        var planFile = Path.Combine(_kit.Workspace.Dir, "plan.md");
        Assert.Contains("Shared contract", File.ReadAllText(planFile));
    }

    /// <summary>Parent 12 with an optional SPEC comment, and its first sub-issue 21 ready to build.</summary>
    private void ParentWithSubIssue(string? parentSpec)
    {
        _kit.CodeHost.AddIssue(12, "Export", null, "ai", Labels.Parent);
        if (parentSpec is not null)
        {
            _kit.CodeHost.AddBotComment(12, Markers.Wrap(parentSpec, Markers.Spec));
        }

        var task = new SubTask(1, Samples.DefaultComponent, "Task", "Do the work.", "src/x.cs", [], false);
        var body = SubIssueBody.Build(new SubIssueDraft(12, task, 2, "contract"));
        _kit.CodeHost.AddIssue(
            21, "#12.1 Task", body, "ai", Labels.SubIssue, Labels.Ready, Labels.Simple, Labels.Implement);
    }

    [Fact]
    public async Task A_sub_issue_plan_has_the_parent_spec_and_the_others_are_read_at_the_feature_branch()
    {
        var parentSpec = Samples.SpecWith([Samples.DefaultComponent], Samples.ApiTable, Samples.UiTable);
        ParentWithSubIssue(parentSpec);

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 21), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        var planFile = Path.Combine(_kit.Workspace.Dir, "plan.md");
        var plan = File.ReadAllText(planFile);
        Assert.Contains("### Scope", plan);
        Assert.Contains("## Parent SPEC (issue #12)", plan);
        Assert.Contains("PaginationResponse<ExportRow>", plan);
        Assert.Contains("are binding for every sub-issue", plan);
        Assert.Equal("feat/issue-12", _kit.Workspace.Requests.Single().OthersAt);
    }

    [Fact]
    public async Task A_sub_issue_still_runs_when_the_parent_has_no_spec()
    {
        ParentWithSubIssue(parentSpec: null);

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 21), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        var planFile = Path.Combine(_kit.Workspace.Dir, "plan.md");
        Assert.Contains("None: the parent issue has no SPEC", File.ReadAllText(planFile));
    }

    [Fact]
    public async Task The_acceptance_criteria_of_the_parent_spec_are_not_counted_against_the_sub_issue_pr()
    {
        var parentSpec = Samples.SpecFor(Samples.DefaultComponent) + "\n\n### Acceptance criteria\n1. A\n2. B\n3. C";
        ParentWithSubIssue(parentSpec);

        var outcome = await Job.RunAsync(_kit.Request(JobType.ImplementIssue, 21), _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Single(_kit.Copilot.Requests);   // one criterion in PR_BODY.md, no fix round about the count
    }

    [Fact]
    public async Task Failing_check_gets_a_fix_round_and_then_a_normal_pr()
    {
        SimpleIssueWithSpec();
        _kit.Processes.ExitCodeSequence["make test"] = new Queue<int>([1, 0]);

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(
            [ImplementIssueJob.Prompt, ImplementIssueJob.FixPrompt],
            _kit.Copilot.Requests.Select(request => request.Prompt));
        // Layout of fix.md: FixContextTests.
        var fixFile = Path.Combine(_kit.Workspace.Dir, "fix.md");
        Assert.Contains("make test", File.ReadAllText(fixFile));
        // diff.patch, then "did the code change?"
        Assert.Equal(["origin/main", "origin/main"], _kit.Git.StageAndDiffs);
        var diffFile = Path.Combine(_kit.Workspace.Dir, "diff.patch");
        Assert.True(File.Exists(diffFile));
        Assert.Equal(["#12: Export CSV (Copilot)"], _kit.Git.Commits);

        var pull = _kit.CodeHost.Pulls.Single();
        Assert.Equal("Export CSV", pull.Title);
        Assert.Contains("Round 1 (implement): `make build` passed, `make test` failed (exit 1)", pull.Body);
        Assert.Contains("Round 2 (fix): `make build` passed, `make test` passed", pull.Body);

        // Logs: fix-N.md is the input of round N, each round has its checks and its transcript.
        var fixInputFile = Path.Combine(_kit.Run.Dir, "fix-2.md");
        Assert.True(File.Exists(fixInputFile));
        var checkFile = Path.Combine(_kit.Run.Dir, "round-1-check-2.txt");
        Assert.True(File.Exists(checkFile));
        var stdoutFile = Path.Combine(_kit.Run.Dir, "round-2-copilot-stdout.txt");
        Assert.True(File.Exists(stdoutFile));
        Assert.Equal(Path.Combine(_kit.Run.Dir, "round-2-transcript.md"), _kit.Copilot.Requests[1].TranscriptFile);
    }

    [Fact]
    public async Task Fix_round_that_changed_no_code_keeps_the_checks_of_the_round_before()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.FilesToWrite["PR_BODY.md"] = "Adds CSV export.";
        _kit.Git.FixedDiff = "diff --git a/src/a.txt b/src/a.txt\n";

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(2, _kit.Copilot.Requests.Count);
        Assert.Equal(2, _kit.Processes.Commands.Count);   // build and test of round 1 only
        Assert.Contains("Round 2 (fix): `make build` passed, `make test` passed", _kit.CodeHost.Pulls.Single().Body);
    }

    [Fact]
    public async Task Checks_still_failing_after_the_last_fix_round_open_a_wip_pr()
    {
        SimpleIssueWithSpec();
        _kit.Settings.Implement.MaxFixRounds = 1;
        _kit.Processes.ExitCodes["make test"] = 1;

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("wip", outcome.Result);
        Assert.Equal(2, _kit.Copilot.Requests.Count);
        Assert.Equal(2, outcome.CopilotCalls);
        Assert.StartsWith("[WIP - checks failing]", _kit.CodeHost.Pulls.Single().Title);
    }

    [Fact]
    public async Task A_fix_round_that_fails_the_same_way_stops_the_rounds_early()
    {
        SimpleIssueWithSpec();
        _kit.Settings.Implement.MaxFixRounds = 5;
        _kit.Processes.ExitCodes["make test"] = 1;

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("wip", outcome.Result);
        Assert.Equal(2, _kit.Copilot.Requests.Count);
        Assert.Contains(
            "no progress since round 1 (same failing checks), stopped",
            _kit.CodeHost.Pulls.Single().Body);
    }

    [Fact]
    public async Task A_fix_round_that_fails_differently_gets_another_round()
    {
        SimpleIssueWithSpec();
        _kit.Settings.Implement.MaxFixRounds = 5;
        _kit.Processes.ExitCodeSequence["make test"] = new Queue<int>([1, 2, 0]);

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(3, _kit.Copilot.Requests.Count);
        Assert.DoesNotContain("no progress", _kit.CodeHost.Pulls.Single().Body);
    }

    [Fact]
    public async Task No_fix_rounds_configured_means_one_copilot_run()
    {
        SimpleIssueWithSpec();
        _kit.Settings.Implement.MaxFixRounds = 0;
        _kit.Processes.ExitCodes["make test"] = 1;

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("wip", outcome.Result);
        Assert.Single(_kit.Copilot.Requests);
    }

    [Fact]
    public async Task Failed_build_skips_the_other_checks()
    {
        SimpleIssueWithSpec();
        _kit.Settings.Implement.MaxFixRounds = 0;
        _kit.Component.Commands.Lint = "make lint";
        _kit.Processes.ExitCodes["make build"] = 1;

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Single(_kit.Processes.Commands);
    }

    [Fact]
    public async Task Lint_is_run_by_the_runner_and_its_tool_is_allowed_for_the_agent()
    {
        SimpleIssueWithSpec();
        _kit.Component.Commands.Lint = "npx eslint . && dotnet format --verify-no-changes";
        _kit.Processes.ExitCodeSequence["eslint"] = new Queue<int>([1, 0]);

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Contains(_kit.Processes.Commands, command => command.Contains("eslint"));
        Assert.Equal(
            ["write", "shell(make:*)", "shell(npx:*)", "shell(dotnet:*)"],
            _kit.Copilot.Requests[0].AllowTools);
        var fixFile = Path.Combine(_kit.Workspace.Dir, "fix.md");
        Assert.Contains("eslint", File.ReadAllText(fixFile));
    }

    [Fact]
    public async Task Missing_pr_body_gets_a_fix_round()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.FilesToWrite.Clear();
        _kit.Copilot.OnRun = (request, call) =>
        {
            if (call == 2)
            {
                var prBodyFile = Path.Combine(request.WorkDir, "PR_BODY.md");
                var prBody = Samples.PrBody();
                File.WriteAllText(prBodyFile, prBody);
            }
        };

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("ok", outcome.Result);
        Assert.Equal(2, _kit.Copilot.Requests.Count);
        var fixFile = Path.Combine(_kit.Workspace.Dir, "fix.md");
        Assert.Contains("PR_BODY.md is missing", File.ReadAllText(fixFile));
        Assert.DoesNotContain("PR description problems", _kit.CodeHost.Pulls.Single().Body);
    }

    [Fact]
    public async Task Pr_body_still_incomplete_after_the_rounds_is_listed_in_the_pr()
    {
        SimpleIssueWithSpec();
        _kit.Copilot.FilesToWrite["PR_BODY.md"] = "Adds CSV export.";

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        // Checks pass, only the text is wrong: one fix round, not every round.
        Assert.Equal("ok", outcome.Result);
        Assert.Equal(2, _kit.Copilot.Requests.Count);
        var body = _kit.CodeHost.Pulls.Single().Body!;
        Assert.Contains("Adds CSV export.", body);
        Assert.Contains("### PR description problems", body);
        Assert.Contains("The heading `### What changed` is missing.", body);
    }

    [Fact]
    public async Task Copilot_failure_in_a_fix_round_still_opens_the_pr_and_stops_the_rounds()
    {
        SimpleIssueWithSpec();
        _kit.Processes.ExitCodes["make test"] = 1;
        _kit.Copilot.OnRun = (_, call) =>
        {
            if (call == 2)
            {
                _kit.Copilot.ExitCode = 2;
            }
        };

        var implementRequest = _kit.Request(JobType.ImplementIssue, 12);
        var outcome = await Job.RunAsync(implementRequest, _kit.Run, default);

        Assert.Equal("wip", outcome.Result);
        Assert.Equal(2, _kit.Copilot.Requests.Count);   // maxFixRounds is 2, but a failed call ends the rounds
        Assert.Contains(("HEAD", "ai/issue-12"), _kit.Git.Pushes);

        var pull = _kit.CodeHost.Pulls.Single();
        Assert.StartsWith("[WIP - checks failing]", pull.Title);
        Assert.Contains("Round 2 (fix): Copilot failed (Copilot exited with code 2", pull.Body);
    }

    [Fact]
    public async Task Changed_git_config_in_a_fix_round_discards_the_clone_and_pushes_nothing()
    {
        SimpleIssueWithSpec();
        _kit.Processes.ExitCodes["make test"] = 1;
        // setup baseline, after round 1, after the checks of round 1, after the fix round
        foreach (var fingerprint in new[] { "c1", "c1", "c1", "changed" })
        {
            _kit.Git.Fingerprints.Enqueue(fingerprint);
        }

        var exception = await Assert.ThrowsAsync<JobFailedException>(
            () => Job.RunAsync(_kit.Request(JobType.ImplementIssue, 12), _kit.Run, default));

        Assert.Contains(".git/config", exception.Message);
        Assert.Equal([JobTestKit.Repo], _kit.Workspace.Discarded);
        Assert.Empty(_kit.Git.Pushes);
    }
}
