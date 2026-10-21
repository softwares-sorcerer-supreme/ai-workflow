using System.Collections.Concurrent;

namespace AiRunner.Infrastructure;

/// <summary>
/// The size on disk of folders, computed in the background: walking a clone with a node_modules folder takes seconds,
/// and a dashboard request must not wait for it. <see cref="Get"/> answers from the cache (null until the first walk
/// is done) and starts a new walk when the answer is old.
/// </summary>
public sealed class DirectorySizes(TimeProvider time)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (long Bytes, DateTimeOffset At)> _known =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, bool> _walking = new(StringComparer.OrdinalIgnoreCase);

    public long? Get(string directory)
    {
        var now = time.GetUtcNow();
        var isKnown = _known.TryGetValue(directory, out var entry);
        var isOld = !isKnown || now - entry.At > MaxAge;
        if (isOld && Directory.Exists(directory) && _walking.TryAdd(directory, true))
        {
            StartWalk(directory);
        }

        return isKnown ? entry.Bytes : null;
    }

    /// <summary>Completes when no walk is running (tests wait on it; nothing else needs to).</summary>
    public async Task WhenIdleAsync()
    {
        while (!_walking.IsEmpty)
        {
            await Task.Delay(10);
        }
    }

    private void StartWalk(string directory) =>
        Task.Run(() =>
        {
            try
            {
                var bytes = Measure(directory);
                _known[directory] = (bytes, time.GetUtcNow());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A folder that changes or locks while it is walked: no size now, the next request tries again.
            }
            finally
            {
                _walking.TryRemove(directory, out _);
            }
        });

    private static long Measure(string directory)
    {
        // Links are skipped: a junction in node_modules must not count twice or lead out of the clone.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        long total = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", options))
        {
            total += file.Length;
        }

        return total;
    }
}
