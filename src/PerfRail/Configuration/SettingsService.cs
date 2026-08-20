using System.Text.Json;

namespace PerfRail.Configuration;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> under %LOCALAPPDATA%\PerfRail.
/// </summary>
/// <remarks>
/// <para>
/// Never throws on load. A monitoring bar that refuses to start because its config file
/// is malformed has failed at the only job it has; a bad file is renamed aside and
/// defaults are used instead.
/// </para>
/// <para>
/// LocalApplicationData rather than a path beside the executable: under MSIX the install
/// directory is read-only, and LocalApplicationData is transparently redirected into the
/// package's private store, so the same code works in both distributions.
/// </para>
/// </remarks>
internal sealed class SettingsService
{
    private readonly string _directory;
    private readonly string _path;

    public SettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PerfRail"))
    {
    }

    public SettingsService(string directory)
    {
        _directory = directory;
        _path = Path.Combine(directory, "settings.json");
    }

    public string Path_ => _path;

    /// <summary>Raised when settings could not be read or written. Diagnostics only.</summary>
    public event Action<string, Exception>? Failed;

    /// <summary>
    /// Reads settings, falling back to defaults for anything missing, malformed or absent.
    /// </summary>
    public AppSettings Load()
    {
        AppSettings settings = LoadCore();
        settings.Normalize();
        return settings;
    }

    private AppSettings LoadCore()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            using FileStream stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize(stream, AppSettingsContext.Default.AppSettings)
                ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep the bad file rather than silently deleting it: if a user reports lost
            // settings, the evidence is still on disk next to the replacement.
            Failed?.Invoke("load", ex);

            var defaults = new AppSettings();

            // Recovery is deliberately visible on disk: the unreadable file is renamed
            // to .corrupt and a valid one is written in its place. Otherwise the user
            // opens the folder, finds only a .corrupt file, and reasonably concludes
            // their configuration was lost. This is the one path that writes during
            // load, and it only happens after a failure.
            if (TryQuarantine())
            {
                Save(defaults);
            }

            return defaults;
        }
    }

    private bool TryQuarantine()
    {
        try
        {
            string corrupt = _path + ".corrupt";
            File.Delete(corrupt);
            File.Move(_path, corrupt);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failed?.Invoke("quarantine", ex);
            return false;
        }
    }

    /// <summary>
    /// Writes settings atomically.
    /// </summary>
    /// <remarks>
    /// Written to a temporary file and moved into place, so a crash or power loss during
    /// the write cannot leave a half-written file that fails to parse on next launch.
    /// Called only when something actually changes - the performance budget allows for
    /// effectively zero disk writes during normal monitoring.
    /// </remarks>
    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_directory);

            string temp = _path + ".tmp";
            using (FileStream stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, settings, AppSettingsContext.Default.AppSettings);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a preference is not worth taking the app down for.
            Failed?.Invoke("save", ex);
        }
    }
}
