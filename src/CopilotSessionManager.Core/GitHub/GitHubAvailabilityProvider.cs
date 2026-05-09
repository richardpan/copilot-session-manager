namespace CopilotSessionManager.Core.GitHub;

/// <summary>
/// Default <see cref="IGitHubAvailabilityProvider"/>. Thread-safe; only
/// fires <see cref="AvailabilityChanged"/> when the
/// <see cref="GitHubAvailability"/> classification actually changes (the
/// <c>UserMessage</c> alone does not trigger an event — we don't want to
/// spam subscribers on every failed call with identical wording).
/// </summary>
public sealed class GitHubAvailabilityProvider : IGitHubAvailabilityProvider
{
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private GitHubAvailabilityState _current;

    public GitHubAvailabilityProvider()
        : this(TimeProvider.System)
    {
    }

    public GitHubAvailabilityProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _current = GitHubAvailabilityState.InitialAvailable(_timeProvider.GetUtcNow());
    }

    public GitHubAvailabilityState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<GitHubAvailabilityState>? AvailabilityChanged;

    public void Report(GitHubAvailability state, string? userMessage = null)
    {
        GitHubAvailabilityState? toRaise = null;
        lock (_gate)
        {
            if (_current.State == state)
            {
                // Same classification — debounce. Keep the first DetectedAt
                // so callers can see how long we've been in this state.
                return;
            }

            _current = new GitHubAvailabilityState(
                state,
                state == GitHubAvailability.Available ? null : userMessage,
                _timeProvider.GetUtcNow());
            toRaise = _current;
        }

        AvailabilityChanged?.Invoke(this, toRaise);
    }
}
