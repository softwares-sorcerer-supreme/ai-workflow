namespace AiRunner.Tests.Fakes;

/// <summary>
/// Waits for a background service (poller, worker) to reach a state, with a deadline instead of a sleep.
/// </summary>
public static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <param name="describe">What was expected, for the timeout message (read only when the wait fails).</param>
    public static async Task UntilAsync(Func<bool> condition, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                var expected = describe is null ? "The condition did not hold in time." : describe();

                throw new TimeoutException(expected);
            }

            await Task.Delay(20);
        }
    }
}
