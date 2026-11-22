using System.Text.Json;

namespace ClaudeMeterTrayIcon;

/// <summary>User choices from the menu, remembered between runs in %LOCALAPPDATA%\ClaudeMeterTrayIcon\settings.json.</summary>
internal sealed class AppSettings
{
    /// <summary>The text readout next to the tray (percent and time left).</summary>
    public bool ShowOverlay { get; set; } = true;

    /// <summary>The small 16px icon in the tray. Off by default because the readout carries the same numbers.</summary>
    public bool ShowTrayIcon { get; set; } = false;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeMeterTrayIcon", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Best effort only.
        }
    }
}
