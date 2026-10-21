namespace AiRunner.Tests.Core;

/// <summary>The health rule: starting, stale, error, paused, ok, first match wins.</summary>
public class RunnerHealthTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(90);

    private static HealthState Assess(TimeSpan sinceStart, TimeSpan? pollEndedAfterStart, int failingProjects = 0)
    {
        var now = Start + sinceStart;
        DateTimeOffset? lastPollEnded = pollEndedAfterStart is { } offset ? Start + offset : null;
        var input = new HealthInput(now, Start, lastPollEnded, Interval, failingProjects);

        return RunnerHealth.Assess(input);
    }

    [Fact]
    public void Setup_mode_wins_over_a_runner_that_never_polled()
    {
        var now = Start + TimeSpan.FromHours(1);
        var input = new HealthInput(now, Start, null, Interval, 0, IsSetupNeeded: true);

        var health = RunnerHealth.Assess(input);

        Assert.Equal(RunnerHealth.NeedsSetup, health.Code);
        Assert.Equal("needs-setup", health.Code);
    }

    [Fact]
    public void Before_the_first_poll_the_runner_is_starting()
    {
        var health = Assess(TimeSpan.FromSeconds(30), pollEndedAfterStart: null);

        Assert.Equal(RunnerHealth.Starting, health.Code);
    }

    [Fact]
    public void A_first_poll_that_never_ends_becomes_stale()
    {
        var health = Assess(TimeSpan.FromMinutes(10), pollEndedAfterStart: null);

        Assert.Equal(RunnerHealth.Stale, health.Code);
    }

    [Fact]
    public void A_recent_poll_without_errors_is_ok()
    {
        var health = Assess(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4));

        Assert.Equal(RunnerHealth.Ok, health.Code);
        Assert.Equal("Healthy", health.Label);
    }

    [Fact]
    public void A_recent_poll_with_a_failing_project_is_an_error()
    {
        var health = Assess(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4), failingProjects: 2);

        Assert.Equal(RunnerHealth.Error, health.Code);
        Assert.Equal("Poll failed in 2 projects", health.Label);
    }

    [Fact]
    public void No_poll_for_three_intervals_is_stale_even_with_errors()
    {
        // Last poll ended 10 minutes ago; 3 x 90 s = 4.5 minutes.
        var health = Assess(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(10), failingProjects: 1);

        Assert.Equal(RunnerHealth.Stale, health.Code);
        Assert.Equal("No poll for 10 min", health.Label);
    }

    private static HealthState AssessWhilePolling(TimeSpan sinceStart, TimeSpan? lastPollEnded, TimeSpan pollStarted)
    {
        var now = Start + sinceStart;
        DateTimeOffset? lastPollEndedAt = lastPollEnded is { } offset ? Start + offset : null;
        var input = new HealthInput(now, Start, lastPollEndedAt, Interval, 0, PollRunningSince: Start + pollStarted);

        return RunnerHealth.Assess(input);
    }

    [Fact]
    public void A_long_poll_that_is_still_running_is_not_stale()
    {
        // The last poll ended 10 minutes ago, but the next one started 5 minutes ago and is still going.
        var health = AssessWhilePolling(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15));

        Assert.Equal(RunnerHealth.Ok, health.Code);
    }

    [Fact]
    public void A_poll_that_runs_longer_than_the_limit_is_stale()
    {
        var health = AssessWhilePolling(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15));

        Assert.Equal(RunnerHealth.Stale, health.Code);
    }

    [Fact]
    public void With_a_long_poll_interval_a_running_poll_is_allowed_at_least_the_normal_stale_limit()
    {
        // 3 x 5 min = 15 min is the normal limit, longer than the 10 min cap for a running poll.
        var now = Start + TimeSpan.FromMinutes(60);
        var input = new HealthInput(
            now,
            Start,
            Start + TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(5),
            0,
            PollRunningSince: now - TimeSpan.FromMinutes(12));

        var health = RunnerHealth.Assess(input);

        Assert.Equal(RunnerHealth.Ok, health.Code);
    }

    [Fact]
    public void A_long_first_poll_that_is_still_running_is_still_starting()
    {
        var health = AssessWhilePolling(TimeSpan.FromMinutes(6), lastPollEnded: null, pollStarted: TimeSpan.Zero);

        Assert.Equal(RunnerHealth.Starting, health.Code);
    }

    [Fact]
    public void Exactly_three_intervals_is_not_stale_yet()
    {
        var health = Assess(TimeSpan.FromMinutes(10) + Interval * 3, TimeSpan.FromMinutes(10));

        Assert.Equal(RunnerHealth.Ok, health.Code);
    }

    private static HealthState AssessPaused(TimeSpan sinceStart, TimeSpan? pollEndedAfterStart, int failingProjects = 0)
    {
        var now = Start + sinceStart;
        DateTimeOffset? lastPollEnded = pollEndedAfterStart is { } offset ? Start + offset : null;
        var input = new HealthInput(now, Start, lastPollEnded, Interval, failingProjects, IsPaused: true);

        return RunnerHealth.Assess(input);
    }

    [Fact]
    public void A_paused_runner_with_good_polls_is_paused()
    {
        var health = AssessPaused(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4));

        Assert.Equal(RunnerHealth.Paused, health.Code);
        Assert.Equal("Paused: no new jobs", health.Label);
    }

    [Fact]
    public void A_failing_or_stale_poll_is_louder_than_a_pause()
    {
        var failing = AssessPaused(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4), failingProjects: 1);
        var stale = AssessPaused(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(4));

        Assert.Equal(RunnerHealth.Error, failing.Code);
        Assert.Equal(RunnerHealth.Stale, stale.Code);
    }
}
