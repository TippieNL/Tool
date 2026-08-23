using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using SysMon.Core.Configuration;
using SysMon.Core.Diagnostics;

namespace SysMon.App.Services;

/// <summary>
/// Start-with-Windows, via the per-user Run key.
///
/// HKCU rather than HKLM or a scheduled task: it needs no elevation, which matters because the
/// app deliberately runs unelevated by default. A machine-wide or elevated autostart would drag
/// a UAC prompt into every login.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsRunAtLoginEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppPaths.AppName) is string;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the startup registry key.", ex);
            return false;
        }
    }

    /// <summary>Enables or disables autostart. Returns null on success, or a message to show the user.</summary>
    public static string? SetRunAtLogin(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key is null)
            {
                return "Could not open the Windows startup registry key.";
            }

            if (!enabled)
            {
                key.DeleteValue(AppPaths.AppName, throwOnMissingValue: false);
                Log.Info("Removed the start-with-Windows entry.");
                return null;
            }

            var executable = GetExecutablePath();
            if (executable is null)
            {
                return "Could not determine the application path.";
            }

            key.SetValue(AppPaths.AppName, $"\"{executable}\"", RegistryValueKind.String);
            Log.Info("Registered the start-with-Windows entry.");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn("Access denied writing the startup registry key.", ex);
            return "Windows denied access to the startup registry key.";
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update the startup registry key.", ex);
            return "Could not update the Windows startup entry.";
        }
    }

    /// <summary>
    /// Path to the running executable. Uses the process path rather than the assembly location,
    /// which is empty in a single-file publish.
    /// </summary>
    internal static string? GetExecutablePath()
    {
        try
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not determine the executable path.", ex);
            return null;
        }
    }
}

/// <summary>Relaunches the app with administrator rights, which unlocks the driver-backed sensors.</summary>
public static class ElevationService
{
    public static void RestartAsAdministrator()
    {
        var executable = StartupService.GetExecutablePath();
        if (executable is null)
        {
            return;
        }

        try
        {
            // "runas" triggers the UAC prompt. If the user declines, Windows raises a
            // Win32Exception, which is a normal outcome rather than an error to report.
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas",
            });

            System.Windows.Application.Current?.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Log.Info("The user declined the elevation prompt.");
        }
        catch (Exception ex)
        {
            Log.Error("Could not restart with administrator rights.", ex);
        }
    }
}

public static class ShellService
{
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open the folder '{path}'.", ex);
        }
    }
}
