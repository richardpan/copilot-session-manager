namespace CopilotSessionManager.Core.Cli;

public sealed class CliAvailabilityProvider : ICliAvailabilityProvider
{
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private CliAvailabilityState _current;

    public CliAvailabilityProvider()
        : this(TimeProvider.System)
    {
    }

    public CliAvailabilityProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _current = CliAvailabilityState.InitialAvailable(_timeProvider.GetUtcNow());
    }

    public CliAvailabilityState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<CliAvailabilityState>? AvailabilityChanged;

    public void Report(
        CliAvailability state,
        IReadOnlyList<CliVersionInfo>? probes = null,
        string? userMessage = null)
    {
        CliAvailabilityState? toRaise = null;
        var normalizedProbes = probes?.ToArray() ?? Array.Empty<CliVersionInfo>();
        lock (_gate)
        {
            if (_current.State == state && SameProbes(_current.Probes, normalizedProbes))
            {
                return;
            }

            _current = new CliAvailabilityState(
                state,
                normalizedProbes,
                state == CliAvailability.Available ? null : userMessage,
                _timeProvider.GetUtcNow());
            toRaise = _current;
        }

        AvailabilityChanged?.Invoke(this, toRaise);
    }

    private static bool SameProbes(IReadOnlyList<CliVersionInfo> left, IReadOnlyList<CliVersionInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }
}
