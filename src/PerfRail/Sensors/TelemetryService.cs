namespace PerfRail.Sensors;

/// <summary>
/// Samples every source on a background loop and publishes the latest snapshot.
/// </summary>
/// <remarks>
/// <para>
/// The UI never waits on this. Sampling runs on a background task and the result is
/// stored; the rail reads <see cref="Current"/> on its own timer. That is deliberately
/// a pull rather than a push: marshalling each sample to the UI thread would mean
/// calling back into a form that may be mid-disposal, and <c>Control.Invoke</c> from a
/// sampler can deadlock against a shutdown that is waiting for that sampler to stop.
/// </para>
/// <para>
/// A source that throws is disabled rather than allowed to kill the loop. CPU and
/// memory must keep working when an optional GPU source fails.
/// </para>
/// </remarks>
internal sealed class TelemetryService : IDisposable
{
    private readonly List<ISensorSource> _sources;
    private readonly CancellationTokenSource _cts = new();
    // Plain object rather than System.Threading.Lock: that type needs C# 13 and the
    // project is pinned to 12. Uncontended at 1 Hz either way.
    private readonly object _gate = new();
    private readonly HashSet<string> _failed = [];

    private Task? _loop;
    private HardwareSnapshot _current = HardwareSnapshot.Empty;
    private TimeSpan _interval;

    public TelemetryService(IEnumerable<ISensorSource> sources, TimeSpan interval)
    {
        _sources = [.. sources];
        _interval = interval;
    }

    /// <summary>Raised the first time a source fails, so it can be logged once.</summary>
    public event Action<string, Exception>? SourceFailed;

    /// <summary>The most recent snapshot. Safe to read from any thread.</summary>
    public HardwareSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// When true, sampling is suspended but the service stays alive and the last
    /// snapshot remains readable.
    /// </summary>
    public bool IsPaused { get; set; }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_interval);

        // Sample immediately so the first value does not wait a whole interval. CPU
        // utilisation still needs two samples before it can report anything.
        Sample();

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (!IsPaused)
                {
                    Sample();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void Sample()
    {
        HardwareSnapshot snapshot = HardwareSnapshot.Empty;

        foreach (ISensorSource source in _sources)
        {
            if (!source.IsAvailable || _failed.Contains(source.Name))
            {
                continue;
            }

            try
            {
                source.Contribute(ref snapshot);
            }
            catch (Exception ex)
            {
                // Isolate and disable. One bad sensor must not take the rail down, and
                // it must not spam the log once per second either.
                _failed.Add(source.Name);
                SourceFailed?.Invoke(source.Name, ex);
            }
        }

        lock (_gate)
        {
            _current = snapshot;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();

        // Join before disposing the sources, so nothing is sampling a disposed handle.
        // Bounded: a stuck sampler must not stop the process from exiting, because the
        // AppBar band is only released once shutdown completes.
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();

        foreach (ISensorSource source in _sources)
        {
            source.Dispose();
        }
    }
}
