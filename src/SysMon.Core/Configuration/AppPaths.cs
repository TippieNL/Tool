namespace SysMon.Core.Configuration;

/// <summary>
/// Where the app keeps its files. Everything lives under the roaming profile so settings follow
/// the user, and nothing is written next to the executable, which may sit in a read-only location.
/// </summary>
public static class AppPaths
{
    public const string AppName = "PulseMonitor";

    public const string DisplayName = "Pulse Monitor";

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        AppName);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LogFile => Path.Combine(DataDirectory, "logs", "app.log");

    /// <summary>Creates the data directory, returning false if it cannot be created.</summary>
    public static bool EnsureDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
