namespace AiRunner.Infrastructure;

/// <summary>
/// One lock per project folder (docs/specs/dashboard-control.md section 6). A job holds it from
/// <see cref="Workspace.PrepareAsync"/> until the worker ends the job; a workspace action (fetch, re-clone) tries it
/// without waiting and refuses when it is held. Jobs run one at a time, so a job only ever waits for an action that
/// is already running (a fetch), never the other way round.
/// </summary>
public sealed class WorkspaceLocks
{
    private static readonly TimeSpan JobRetry = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly HashSet<string> _heldByJob = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _heldByAction = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while a job or an action holds the project.</summary>
    public bool IsHeld(string project)
    {
        lock (_gate)
        {
            return _heldByJob.Contains(project) || _heldByAction.Contains(project);
        }
    }

    /// <summary>
    /// The running job takes the project; a second call of the same job (a repeated prepare) changes nothing. Waits
    /// only while an action holds it.
    /// </summary>
    public async Task HoldForJobAsync(string project, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_heldByAction.Contains(project))
                {
                    _heldByJob.Add(project);

                    return;
                }
            }

            await Task.Delay(JobRetry, cancellationToken);
        }
    }

    /// <summary>The worker calls this when a job ends, whatever the outcome.</summary>
    public void ReleaseJobHolds()
    {
        lock (_gate)
        {
            _heldByJob.Clear();
        }
    }

    /// <summary>Takes the project for one action without waiting; null when a job or another action holds it.</summary>
    public IDisposable? TryHoldForAction(string project)
    {
        lock (_gate)
        {
            if (_heldByJob.Contains(project) || !_heldByAction.Add(project))
            {
                return null;
            }
        }

        return new ActionHold(this, project);
    }

    private void ReleaseAction(string project)
    {
        lock (_gate)
        {
            _heldByAction.Remove(project);
        }
    }

    private sealed class ActionHold(WorkspaceLocks owner, string project) : IDisposable
    {
        public void Dispose() => owner.ReleaseAction(project);
    }
}
