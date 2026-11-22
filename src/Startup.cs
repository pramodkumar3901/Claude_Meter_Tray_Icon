using Microsoft.Win32;

namespace ClaudeMeterTrayIcon;

/// <summary>
/// "Start with Windows" via the per-user Run key (no admin rights needed).
/// It is switched on the first time the built exe runs; after that the menu toggle decides,
/// so turning it off stays off.
/// </summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppKey = @"Software\ClaudeMeterTrayIcon";
    private const string ValueName = "ClaudeMeterTrayIcon";
    private const string ExeName = "ClaudeMeterTrayIcon.exe";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>
    /// Only the real exe can be registered. Under "dotnet run" the process is dotnet.exe,
    /// and registering that would launch the wrong thing at logon.
    /// </summary>
    private static string? ExePath =>
        Environment.ProcessPath is { } p && Path.GetFileName(p).Equals(ExeName, StringComparison.OrdinalIgnoreCase)
            ? p
            : null;

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        if (ExePath is { } path)
            key.SetValue(ValueName, $"\"{path}\"");
    }

    /// <summary>Turns startup on the first time; afterwards just keeps the registered path current.</summary>
    public static void Initialize()
    {
        if (ExePath is null) return;

        using var app = Registry.CurrentUser.CreateSubKey(AppKey);
        if (app.GetValue("StartupConfigured") is null)
        {
            SetEnabled(true);
            app.SetValue("StartupConfigured", 1);
        }
        else if (IsEnabled)
        {
            SetEnabled(true); // the exe may have moved since last run
        }
    }
}
