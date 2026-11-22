using System.Text.Json;

namespace ClaudeMeterTrayIcon;

/// <summary>
/// Last good reading plus "don't call the API before" time, saved between runs.
/// Without it every restart fires an immediate API call, which is what trips the rate limit
/// when you relaunch a few times in a row.
/// </summary>
internal sealed record CachedState(
    double? FiveUtilization, DateTimeOffset? FiveResetsAt,
    double? SevenUtilization, DateTimeOffset? SevenResetsAt,
    DateTimeOffset FetchedAt, DateTimeOffset? NotBefore)
{
    public bool HasValues => FiveUtilization is not null || SevenUtilization is not null;

    public UsageSnapshot ToSnapshot() => new(
        new UsageWindow(FiveUtilization, FiveResetsAt),
        new UsageWindow(SevenUtilization, SevenResetsAt),
        FetchedAt);

    public static CachedState From(UsageSnapshot? s, DateTimeOffset? notBefore = null) => new(
        s?.FiveHour?.Utilization, s?.FiveHour?.ResetsAt,
        s?.SevenDay?.Utilization, s?.SevenDay?.ResetsAt,
        s?.FetchedAt ?? DateTimeOffset.Now, notBefore);
}

internal static class StateCache
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeMeterTrayIcon", "state.json");

    public static CachedState? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<CachedState>(File.ReadAllText(FilePath))
                : null;
        }
        catch
        {
            return null; // a corrupt cache just means a fresh start
        }
    }

    public static void Save(CachedState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Best effort only.
        }
    }
}
