using AiRunner.Configuration;
using AiRunner.Core;
using AiRunner.Hosting;
using AiRunner.Infrastructure;
using AiRunner.Jobs;
using AiRunner.Tests.Fakes;
using static AiRunner.Tests.Fakes.DeliveryLayouts;

namespace AiRunner.Tests.Hosting;

/// <summary>Claims (claim_enabled): several runners share projects without doing the same job twice.</summary>
public sealed class ClaimTests
{
    private const string Me = "alpha";
    private const string Other = "beta";

    private static JobTestKit NewKit(bool isClaimEnabled = true, ProjectConfig? project = null)
    {
        var kit = new JobTestKit(settings => settings.ClaimEnabled = isClaimEnabled, project);
        kit.Config.RunnerName = Me;
        kit.CodeHost.AddIssue(7, "Do it", "body", "ai");

        return kit;
    }

    private static async Task<StubJob> RunStubAsync(JobTestKit kit, Func<Task<JobOutcome>>? run = null)
    {
        var job = new StubJob(run ?? (() => Task.FromResult(JobOutcome.Ok())));
        var (worker, queue, _) = kit.CreateWorker(job);
        queue.Enqueue(kit.Request(JobType.AnalyzeIssue, 7));
        await worker.DrainAsync(default);

        return job;
    }

    private static void ClaimBy(JobTestKit kit, string runner, ClaimState state = ClaimState.Claimed)
    {
        kit.CodeHost.AddBotComment(7, Claims.Text(runner, state, kit.Time.GetUtcNow()));
        kit.CodeHost.AddLabelsAsync(kit.Project.IssuesRepo, 7, [Labels.Claimed], default).GetAwaiter().GetResult();
    }

    private static IEnumerable<ClaimInfo> ClaimsOn(JobTestKit kit) => Claims.All(kit.CodeHost.CommentsOf(7));

    // ---- the worker ----

    [Fact]
    public async Task With_claims_off_nothing_is_claimed()
    {
        using var kit = NewKit(isClaimEnabled: false);

        var job = await RunStubAsync(kit);

        Assert.True(job.HasRun);
        Assert.Empty(ClaimsOn(kit));
        Assert.DoesNotContain(Labels.Claimed, kit.CodeHost.LabelsOf(7));
    }

    [Fact]
    public async Task A_job_holds_the_label_and_comment_while_it_runs_and_releases_both_after()
    {
        using var kit = NewKit();
        HashSet<string>? labelsWhileRunning = null;
        ClaimInfo? claimWhileRunning = null;

        await RunStubAsync(kit, () =>
        {
            labelsWhileRunning = kit.CodeHost.LabelsOf(7);
            claimWhileRunning = ClaimsOn(kit).Single();

            return Task.FromResult(JobOutcome.Ok());
        });

        Assert.Contains(Labels.Claimed, labelsWhileRunning!);
        Assert.Equal((Me, ClaimState.Claimed), (claimWhileRunning!.Runner, claimWhileRunning.State));
        Assert.DoesNotContain(Labels.Claimed, kit.CodeHost.LabelsOf(7));
        Assert.Equal(ClaimState.Released, ClaimsOn(kit).Single().State);
    }

    [Fact]
    public async Task The_dashboard_status_says_this_runner_holds_the_claim_while_a_normal_job_runs()
    {
        using var kit = NewKit();
        bool? holdsClaimWhileRunning = null;

        await RunStubAsync(kit, () =>
        {
            holdsClaimWhileRunning = kit.Status.Snapshot().Running?.HoldsClaim;

            return Task.FromResult(JobOutcome.Ok());
        });

        Assert.True(holdsClaimWhileRunning);
        Assert.Null(kit.Status.Snapshot().Running);
    }

    /// <summary>A routine job that looks at the dashboard status before and after it claims.</summary>
    private sealed class ClaimingRoutineJob(RunnerStatus status) : IJob
    {
        public JobType Type => JobType.AnalyzeIssue;

        public JobInfo Info => new(Workflows.Analyze, "Routine", TimeSpan.FromMinutes(1), Routine: true);

        public (bool? Before, bool? After) HoldsClaim { get; private set; }

        public async Task<JobOutcome> RunAsync(JobRequest request, JobRun jobRun, CancellationToken cancellationToken)
        {
            var before = status.Snapshot().Running?.HoldsClaim;
            await jobRun.ClaimAsync(cancellationToken);
            var after = status.Snapshot().Running?.HoldsClaim;
            HoldsClaim = (before, after);

            return JobOutcome.Ok();
        }
    }

    [Fact]
    public async Task A_routine_job_holds_the_claim_in_the_dashboard_status_only_after_it_claimed()
    {
        using var kit = NewKit();
        var job = new ClaimingRoutineJob(kit.Status);
        var (worker, queue, _) = kit.CreateWorker(job);
        queue.Enqueue(kit.Request(JobType.AnalyzeIssue, 7));

        await worker.DrainAsync(default);

        Assert.Equal((false, true), job.HoldsClaim);
    }

    [Fact]
    public async Task A_failed_job_still_releases_its_claim()
    {
        using var kit = NewKit();

        await RunStubAsync(kit, () => throw new JobFailedException("no ANALYSIS block"));

        Assert.Equal(ClaimState.Released, ClaimsOn(kit).Single().State);
        Assert.DoesNotContain(Labels.Claimed, kit.CodeHost.LabelsOf(7));
        Assert.Contains(Labels.Failed, kit.CodeHost.LabelsOf(7));
    }

    [Fact]
    public async Task One_claim_comment_is_edited_in_place_by_the_next_job()
    {
        using var kit = NewKit();

        await RunStubAsync(kit);
        await RunStubAsync(kit);

        Assert.Single(ClaimsOn(kit));
    }

    [Fact]
    public async Task A_fresh_claim_of_another_runner_skips_the_job_without_a_trace()
    {
        using var kit = NewKit();
        ClaimBy(kit, Other);

        var job = await RunStubAsync(kit);

        Assert.False(job.HasRun);
        var claim = ClaimsOn(kit).Single();
        Assert.Equal((Other, ClaimState.Claimed), (claim.Runner, claim.State));
        Assert.DoesNotContain(Labels.Failed, kit.CodeHost.LabelsOf(7));
        Assert.Contains(Labels.Claimed, kit.CodeHost.LabelsOf(7));   // the other runner's label is not touched
    }

    [Fact]
    public async Task A_claim_older_than_30_minutes_with_no_result_is_taken_over()
    {
        using var kit = NewKit();
        ClaimBy(kit, Other);
        kit.Time.Advance(TimeSpan.FromMinutes(31));

        var job = await RunStubAsync(kit);

        Assert.True(job.HasRun);
        var claim = ClaimsOn(kit).Single();
        Assert.Equal((Me, ClaimState.Released), (claim.Runner, claim.State));
    }

    [Fact]
    public async Task A_claim_exactly_at_the_limit_or_younger_is_respected()
    {
        using var kit = NewKit();
        ClaimBy(kit, Other);
        kit.Time.Advance(TimeSpan.FromMinutes(30));

        var job = await RunStubAsync(kit);

        Assert.False(job.HasRun);
    }

    [Fact]
    public async Task An_old_claim_with_a_bot_comment_after_it_is_not_stale()
    {
        using var kit = NewKit();
        ClaimBy(kit, Other);
        kit.CodeHost.AddBotComment(7, "Analysis posted.");   // the other runner did produce a result
        kit.Time.Advance(TimeSpan.FromHours(2));

        var job = await RunStubAsync(kit);

        Assert.False(job.HasRun);
    }

    [Theory]
    [InlineData(ClaimState.Released)]
    [InlineData(ClaimState.Withdrawn)]
    public async Task A_released_or_withdrawn_claim_is_free(ClaimState state)
    {
        using var kit = NewKit();
        ClaimBy(kit, Other, state);

        var job = await RunStubAsync(kit);

        Assert.True(job.HasRun);
        Assert.Single(ClaimsOn(kit));
    }

    [Fact]
    public async Task A_human_cannot_fake_a_claim_with_the_tag()
    {
        using var kit = NewKit();
        var forged = Claims.Text(Other, ClaimState.Claimed, kit.Time.GetUtcNow());
        kit.CodeHost.AddHumanComment(7, forged);

        var job = await RunStubAsync(kit);

        Assert.True(job.HasRun);
    }

    [Fact]
    public async Task The_loser_of_a_race_withdraws_its_duplicate_and_does_not_run()
    {
        using var kit = NewKit();

        // Right after this runner writes its claim, the other runner's older claim comment shows up.
        kit.CodeHost.OnCommentAdded = _ =>
        {
            var older = Samples.Bot(5, Claims.Text(Other, ClaimState.Claimed, kit.Time.GetUtcNow()));
            kit.CodeHost.Comments[7].Insert(0, older);
            kit.CodeHost.OnCommentAdded = null;
        };

        var job = await RunStubAsync(kit);

        Assert.False(job.HasRun);
        var claims = ClaimsOn(kit).ToList();
        Assert.Equal(2, claims.Count);
        Assert.Equal((Other, ClaimState.Claimed), (claims[0].Runner, claims[0].State));
        Assert.Equal((Me, ClaimState.Withdrawn), (claims[1].Runner, claims[1].State));
        Assert.DoesNotContain(Labels.Claimed, kit.CodeHost.LabelsOf(7));
    }

    // ---- routine jobs claim just before their first write ----

    private static async Task RunFinishAsync(JobTestKit kit)
    {
        var finish = new FinishIssueJob(kit.CodeHost, kit.Loader);
        var (worker, queue, _) = kit.CreateWorker(finish);
        queue.Enqueue(kit.Request(JobType.FinishIssue, 12));
        await worker.DrainAsync(default);
    }

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task A_routine_job_with_nothing_to_do_claims_nothing(string layout)
    {
        using var kit = NewKit(project: Create(layout));
        var (_, second) = Pair(kit.Project);
        ArrangeSimpleIssue(kit, second);

        await RunFinishAsync(kit);   // no PR yet

        Assert.Empty(Claims.All(kit.CodeHost.CommentsOf(12)));
        Assert.DoesNotContain(Labels.Claimed, kit.CodeHost.LabelsOf(12));
    }

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task A_routine_job_claims_before_it_closes_and_releases_after(string layout)
    {
        using var kit = NewKit(project: Create(layout));
        var (_, second) = Pair(kit.Project);
        ArrangeSimpleIssue(kit, second);
        kit.CodeHost.AddPull(30, "ai/issue-12", "main", merged: true, state: "closed", repo: second.Repo);

        await RunFinishAsync(kit);

        Assert.False(kit.CodeHost.Issues[12].IsOpen());
        var claim = Claims.All(kit.CodeHost.CommentsOf(12)).Single();
        Assert.Equal((Me, ClaimState.Released), (claim.Runner, claim.State));
        Assert.DoesNotContain(Labels.Claimed, kit.CodeHost.LabelsOf(12));
    }

    [Theory]
    [MemberData(nameof(Names), MemberType = typeof(DeliveryLayouts))]
    public async Task A_routine_job_leaves_an_item_claimed_by_another_runner_alone(string layout)
    {
        using var kit = NewKit(project: Create(layout));
        var (_, second) = Pair(kit.Project);
        ArrangeSimpleIssue(kit, second);
        kit.CodeHost.AddPull(30, "ai/issue-12", "main", merged: true, state: "closed", repo: second.Repo);
        kit.CodeHost.AddBotComment(12, Claims.Text(Other, ClaimState.Claimed, kit.Time.GetUtcNow()));

        await RunFinishAsync(kit);

        Assert.True(kit.CodeHost.Issues[12].IsOpen());
        Assert.Equal(Other, Claims.All(kit.CodeHost.CommentsOf(12)).Single().Runner);
    }

    // ---- the pure rules ----

    [Fact]
    public void Decide_creates_edits_or_skips()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        HostComment ClaimComment(long id, string runner, ClaimState state, DateTimeOffset at) =>
            Samples.Bot(id, Claims.Text(runner, state, at));

        Assert.Equal(ClaimAction.Create, Claims.Decide([], Me, now).Action);
        Assert.Equal(ClaimAction.Edit, Claims.Decide([ClaimComment(1, Me, ClaimState.Claimed, now)], Me, now).Action);
        Assert.Equal(ClaimAction.Skip, Claims.Decide([ClaimComment(1, Other, ClaimState.Claimed, now)], Me, now).Action);
        Assert.Equal(
            ClaimAction.Edit,
            Claims.Decide([ClaimComment(1, Other, ClaimState.Claimed, now.AddMinutes(-31))], Me, now).Action);
    }

    [Fact]
    public void A_claim_with_a_deadline_goes_stale_after_it_whatever_comments_follow()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var until = now.AddMinutes(45);
        var claim = Samples.Bot(1, Claims.Text(Other, ClaimState.Claimed, new ClaimWindow(now, until)));

        // "Copilot started ..." is a bot comment after the claim; it must not keep a dead claim alive.
        var progress = Samples.Bot(2, "Copilot started on branch `ai/issue-12`.");
        HostComment[] thread = [claim, progress];

        // A job may run until its deadline, even when that is later than the 30 minute mark.
        Assert.Equal(ClaimAction.Skip, Claims.Decide(thread, Me, now.AddMinutes(40)).Action);
        Assert.Equal(ClaimAction.Edit, Claims.Decide(thread, Me, now.AddMinutes(46)).Action);
    }

    [Fact]
    public void A_deadline_shorter_than_30_minutes_still_holds_the_claim_for_30_minutes()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var claim = Samples.Bot(1, Claims.Text(Other, ClaimState.Claimed, new ClaimWindow(now, now.AddMinutes(10))));

        Assert.Equal(ClaimAction.Skip, Claims.Decide([claim], Me, now.AddMinutes(20)).Action);
        Assert.Equal(ClaimAction.Edit, Claims.Decide([claim], Me, now.AddMinutes(31)).Action);
    }

    [Fact]
    public void The_deadline_survives_the_tag()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var text = Claims.Text(Me, ClaimState.Claimed, new ClaimWindow(now, now.AddMinutes(45)));

        Assert.Equal(now.AddMinutes(45), Claims.Read(Samples.Bot(1, text))!.Until);
        Assert.Null(Claims.Read(Samples.Bot(1, Claims.Text(Me, ClaimState.Claimed, now)))!.Until);
    }

    [Fact]
    public void A_runner_name_is_made_safe_for_the_tag()
    {
        Assert.Equal("my_laptop--_1", Claims.SafeName(" my laptop-->1 "));
        Assert.Equal("runner", Claims.SafeName("  "));

        var text = Claims.Text("a b --> c", ClaimState.Claimed, DateTimeOffset.UtcNow);
        var parsed = Claims.Read(Samples.Bot(1, text));
        Assert.NotNull(parsed);
        Assert.Equal(Claims.SafeName("a b --> c"), parsed.Runner);
    }
}
