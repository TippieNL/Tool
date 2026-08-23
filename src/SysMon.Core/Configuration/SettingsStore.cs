using System.Text.Json;
using System.Text.Json.Serialization;
using SysMon.Core.Diagnostics;

namespace SysMon.Core.Configuration;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON.
///
/// Two properties matter here: a corrupt or partially-written file must never stop the app from
/// starting (it is backed up and replaced with defaults), and saving must be atomic so that a
/// crash mid-write cannot produce the corrupt file we just promised to survive.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(1);

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Timer _debounceTimer;

    private AppSettings _pending = new();
    private bool _hasPendingSave;
    private bool _disposed;

    public SettingsStore(string path)
    {
        _path = path;
        _debounceTimer = new Timer(_ => FlushPending(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Path => _path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Log.Info("No settings file found; starting with defaults.");
                return Defaults();
            }

            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);

            if (settings is null)
            {
                Log.Warn("Settings file deserialized to null; using defaults.");
                BackupCorruptFile();
                return Defaults();
            }

            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Failed to read settings; falling back to defaults.", ex);
            BackupCorruptFile();
            return Defaults();
        }
    }

    /// <summary>
    /// Queues a save. Repeated calls inside the debounce window collapse into one write, so
    /// dragging a slider in the settings window does not hammer the disk.
    /// </summary>
    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = settings.Clone();
            _hasPendingSave = true;
            _debounceTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes any queued save immediately. Called on shutdown.</summary>
    public void Flush() => FlushPending();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        FlushPending();
        _debounceTimer.Dispose();
    }

    private void FlushPending()
    {
        AppSettings settings;

        lock (_gate)
        {
            if (!_hasPendingSave)
            {
                return;
            }

            settings = _pending;
            _hasPendingSave = false;
        }

        SaveNow(settings);
    }

    private void SaveNow(AppSettings settings)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);

            // Write to a sibling temp file and move it into place, so an interrupted write
            // leaves the previous good file intact rather than a truncated one.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings.", ex);
        }
    }

    private void BackupCorruptFile()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Move(_path, _path + ".corrupt", overwrite: true);
                Log.Info($"Backed up unreadable settings file to {_path}.corrupt");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not back up the unreadable settings file.", ex);
        }
    }

    private static AppSettings Defaults()
    {
        var settings = new AppSettings();
        settings.Normalize();
        return settings;
    }
}
