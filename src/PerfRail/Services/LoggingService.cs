using System.Globalization;
using System.Text;

namespace PerfRail.Services;

/// <summary>
/// Minimal diagnostic log for lifecycle and exceptional events.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small. PerfRail's performance budget calls for effectively zero disk
/// writes during normal monitoring, so nothing is written per sample - only things that
/// happen once, or that went wrong. A monitor that writes a line every second is a
/// monitor that wears out an SSD explaining that nothing is happening.
/// </para>
/// <para>
/// Growth is bounded by rolling a single previous file: at most two files exist, each
/// capped. No dated files accumulating forever in a folder nobody looks at.
/// </para>
/// <para>
/// Never throws. Losing a log line is not worth taking down a monitoring bar, and the
/// log exists to diagnose failures rather than to cause them.
/// </para>
/// </remarks>
internal sealed class LoggingService : IDisposable
{
    private const long MaxBytes = 256 * 1024;

    private readonly string _path;
    private readonly string _previousPath;
    private readonly object _gate = new();
    private readonly HashSet<string> _oncePerRun = [];

    private bool _failed;

    public LoggingService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PerfRail",
            "logs"))
    {
    }

    public LoggingService(string directory)
    {
        _path = Path.Combine(directory, "perfrail.log");
        _previousPath = Path.Combine(directory, "perfrail.previous.log");
        Directory = directory;
    }

    public string Directory { get; }

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("WARN ", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    /// <summary>
    /// Logs a message at most once per run, keyed by <paramref name="key"/>.
    /// </summary>
    /// <remarks>
    /// For conditions that recur every sample - a sensor that is unavailable, a counter
    /// that keeps returning nothing. The first occurrence is worth a line; the
    /// thousandth is noise that buries everything else.
    /// </remarks>
    public void Once(string key, string message)
    {
        lock (_gate)
        {
            if (!_oncePerRun.Add(key))
            {
                return;
            }
        }

        Write("INFO ", message);
    }

    private void Write(string level, string message)
    {
        if (_failed)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                RollIfTooLarge();

                var line = new StringBuilder(message.Length + 40)
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                    .Append(' ')
                    .Append(level)
                    .Append(' ')
                    .Append(message)
                    .Append(Environment.NewLine);

                File.AppendAllText(_path, line.ToString());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Stop trying rather than failing on every subsequent call.
            _failed = true;
        }
    }

    private void RollIfTooLarge()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < MaxBytes)
        {
            return;
        }

        File.Delete(_previousPath);
        File.Move(_path, _previousPath);
    }

    public void Dispose()
    {
    }
}
