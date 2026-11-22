using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeMeterTrayIcon;

/// <summary>One rate-limit window (5 hour or 7 day) as reported by Claude.</summary>
public sealed record UsageWindow(double? Utilization, DateTimeOffset? ResetsAt);

/// <summary>A single point-in-time reading of both windows.</summary>
public sealed record UsageSnapshot(UsageWindow? FiveHour, UsageWindow? SevenDay, DateTimeOffset FetchedAt);

/// <summary>Expected, user-displayable failure (missing/expired token, HTTP error, etc.).</summary>
public sealed class UsageException(string message, TimeSpan? retryAfter = null, bool rateLimited = false)
    : Exception(message)
{
    /// <summary>How long the server asked us to wait (Retry-After), if it said.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>True for HTTP 429, so the caller can back off.</summary>
    public bool RateLimited { get; } = rateLimited;
}

/// <summary>
/// Reads the Claude Code OAuth token from ~/.claude/.credentials.json and queries
/// the usage endpoint used by the Claude Code /usage view.
/// The token is read-only here: we never refresh it or write the file, because
/// refresh tokens rotate and Claude Code owns that file. When the access token has
/// expired, Claude Code refreshes it the next time you use it, and this app picks
/// up the new token on its next poll.
/// </summary>
public sealed class UsageService : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct = default)
    {
        string token = ReadAccessToken();

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", BetaHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UsageException("Network error: " + ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UsageException("Request timed out");
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new UsageException("Token rejected (401). Open Claude Code once to refresh login.");
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var header = response.Headers.RetryAfter;
                TimeSpan? wait = header?.Delta ?? (header?.Date is { } at ? at - DateTimeOffset.UtcNow : null);
                throw new UsageException("Rate limited by usage API (429). Will retry.", wait, rateLimited: true);
            }
            if (!response.IsSuccessStatusCode)
                throw new UsageException($"Usage API returned {(int)response.StatusCode}.");

            return Parse(body);
        }
    }

    public static string CredentialsPath()
    {
        // CLAUDE_CONFIG_DIR is honoured by Claude Code, so honour it here too.
        string? dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(dir, ".credentials.json");
    }

    private static string ReadAccessToken()
    {
        string path = CredentialsPath();
        if (!File.Exists(path))
            throw new UsageException("No Claude login found. Run Claude Code and sign in.");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
            throw new UsageException("Credentials file has no claudeAiOauth entry.");

        if (!oauth.TryGetProperty("accessToken", out var tokenEl) || tokenEl.ValueKind != JsonValueKind.String)
            throw new UsageException("Credentials file has no access token.");

        if (oauth.TryGetProperty("expiresAt", out var expEl) && expEl.ValueKind == JsonValueKind.Number)
        {
            long expiresMs = expEl.GetInt64();
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiresMs - 60_000)
                throw new UsageException("Login token expired. Open Claude Code once to refresh it.");
        }

        return tokenEl.GetString()!;
    }

    internal static UsageSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new UsageSnapshot(
            ParseWindow(root, "five_hour"),
            ParseWindow(root, "seven_day"),
            DateTimeOffset.Now);
    }

    private static UsageWindow? ParseWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var win) || win.ValueKind != JsonValueKind.Object)
            return null;

        double? utilization = null;
        if (win.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number)
            utilization = u.GetDouble();

        DateTimeOffset? resets = null;
        if (win.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out var parsed))
            resets = parsed;

        return new UsageWindow(utilization, resets);
    }

    public void Dispose() => _http.Dispose();
}
