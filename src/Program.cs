using System.Drawing;
using System.Windows.Forms;

namespace ClaudeMeterTrayIcon;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Developer aid: "--preview <file.png>" renders the icon for several levels and exits.
        if (args.Length == 2 && args[0] == "--preview")
            return SavePreview(args[1]);

        using var mutex = new Mutex(initiallyOwned: true, "ClaudeMeterTrayIcon.SingleInstance", out bool first);
        if (!first)
            return 0;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayAppContext());
        return 0;
    }

    private static int SavePreview(string path)
    {
        var samples = new (double? five, double? seven, Freshness freshness)[]
        {
            (12, 4, Freshness.Fresh), (64, 31, Freshness.Fresh), (78, 45, Freshness.Fresh),
            (96, 92, Freshness.Fresh), (45, 20, Freshness.Stale), (null, null, Freshness.Expired),
        };

        // Render each sample at the real tray sizes (16/24/32px), then enlarge 4x for inspection.
        int[] sizes = { 16, 24, 32 };
        const int scale = 4;
        int cell = 36;
        using var sheet = new Bitmap(samples.Length * cell * scale, sizes.Length * cell * scale);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(32, 32, 32)); // dark taskbar background
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            for (int row = 0; row < sizes.Length; row++)
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    var (five, seven, freshness) = samples[i];
                    using var icon = IconRenderer.Render(
                        five is null ? null : new UsageWindow(five, null), freshness,
                        seven is null ? null : new UsageWindow(seven, null), freshness,
                        sizes[row]);
                    int x = (i * cell + 2) * scale;
                    int y = (row * cell + 2) * scale;
                    g.DrawImage(icon, x, y, sizes[row] * scale, sizes[row] * scale);
                }
            }
        }
        sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return 0;
    }
}

/// <summary>Owns the tray icon, the poll timer and the context menu.</summary>
internal sealed class TrayAppContext : ApplicationContext
{
    // The usage endpoint rate-limits aggressively, so poll gently. The countdown on screen
    // ticks locally from the known reset time and does not need API calls.
    private const int PollIntervalMs = 180_000;
    private const int ErrorRetryMs = 60_000;
    private const int MinRateLimitBackoffMs = 120_000;
    private const int FirstRateLimitBackoffMs = 300_000;
    private const int MaxRateLimitBackoffMs = 20 * 60_000;
    private const int ManualRefreshCooldownMs = 30_000;

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _fiveItem;
    private readonly ToolStripMenuItem _sevenItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly TaskbarOverlay _overlay = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly UsageService _service = new();
    private readonly CancellationTokenSource _cts = new();

    private Icon? _currentIcon;
    private bool _refreshing;
    private UsageSnapshot? _last;            // last good reading, kept on screen during errors
    private bool _lastRefreshFailed;         // true after a failed refresh, until the next success
    private readonly System.Windows.Forms.Timer _viewTimer = new() { Interval = 30_000 };
    private int _backoffMs;                  // current rate-limit backoff, 0 when healthy
    private DateTime _blockedUntilUtc = DateTime.MinValue;
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    public TrayAppContext()
    {
        _fiveItem = new ToolStripMenuItem("5-hour: --") { Enabled = false };
        _sevenItem = new ToolStripMenuItem("7-day: --") { Enabled = false };
        _statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };

        // The default menu font is tiny on high-DPI screens, so use a readable size.
        var menuFont = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
        _menu = new ContextMenuStrip { Font = menuFont, ShowImageMargin = false };
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _fiveItem,
            _sevenItem,
            _statusItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Refresh now", null, (_, _) => _ = RefreshAsync(manual: true)),
            CreateOverlayToggle(),
            CreateStartupToggle(),
            new ToolStripMenuItem("Quit", null, (_, _) => ExitThread()),
        });
        foreach (ToolStripItem item in _menu.Items)
            item.Font = menuFont;

        // The overlay shows the numbers as readable text next to the tray.
        _overlay.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _ = RefreshAsync(manual: true);
            else if (e.Button == MouseButtons.Right) _menu.Show(Cursor.Position);
        };
        _overlay.SetUsage(null, Freshness.Expired, null, Freshness.Expired);

        _tray = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
            Text = "Claude usage",
            Icon = SwapIcon(IconRenderer.CreateIcon(null, Freshness.Expired, null, Freshness.Expired)),
        };

        // Readings age even when nothing new arrives, so re-judge freshness regularly.
        _viewTimer.Tick += (_, _) => UpdateView();
        _viewTimer.Start();
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _ = RefreshAsync(manual: true);
        };

        _timer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _timer.Tick += (_, _) => _ = RefreshAsync();

        // Show the saved reading straight away, and respect any wait left over from the last run
        // so quick restarts don't each hit the API.
        int firstDelayMs = 0;
        if (StateCache.Load() is { } cached)
        {
            if (cached.HasValues)
            {
                _last = cached.ToSnapshot();
                _statusItem.Text = $"Saved reading from {cached.FetchedAt.LocalDateTime:h:mm tt}, refreshing soon";
                UpdateView();
            }

            DateTimeOffset now = DateTimeOffset.Now;
            DateTimeOffset notBefore = cached.NotBefore ?? DateTimeOffset.MinValue;
            DateTimeOffset tooSoon = cached.FetchedAt + TimeSpan.FromMilliseconds(ErrorRetryMs);
            DateTimeOffset allowed = notBefore > tooSoon ? notBefore : tooSoon;
            if (allowed > now)
            {
                firstDelayMs = (int)Math.Min((allowed - now).TotalMilliseconds, MaxRateLimitBackoffMs);
                _blockedUntilUtc = DateTime.UtcNow.AddMilliseconds(firstDelayMs);
            }
        }

        if (firstDelayMs > 0)
        {
            _timer.Interval = Math.Max(1000, firstDelayMs);
            _timer.Start();
        }
        else
        {
            _timer.Start();
            _ = RefreshAsync();
        }
    }

    private static ToolStripMenuItem CreateStartupToggle()
    {
        Startup.Initialize();
        var item = new ToolStripMenuItem("Start with Windows") { Checked = Startup.IsEnabled, CheckOnClick = true };
        item.CheckedChanged += (_, _) => Startup.SetEnabled(item.Checked);
        return item;
    }

    private ToolStripMenuItem CreateOverlayToggle()
    {
        var item = new ToolStripMenuItem("Show text on taskbar") { Checked = true, CheckOnClick = true };
        item.CheckedChanged += (_, _) => _overlay.OverlayEnabled = item.Checked;
        return item;
    }

    private async Task RefreshAsync(bool manual = false)
    {
        if (_refreshing) return;

        DateTime now = DateTime.UtcNow;
        if (manual)
        {
            // Clicking repeatedly would just extend a rate limit, so throttle manual refreshes.
            if (now < _blockedUntilUtc)
            {
                _statusItem.Text = $"Waiting until {_blockedUntilUtc.ToLocalTime():h:mm tt} (rate limit)";
                return;
            }
            if ((now - _lastAttemptUtc).TotalMilliseconds < ManualRefreshCooldownMs)
                return;
        }

        _refreshing = true;
        _lastAttemptUtc = now;
        int nextDelayMs = PollIntervalMs;
        try
        {
            var snapshot = await _service.FetchAsync(_cts.Token);
            _last = snapshot;
            _backoffMs = 0;
            _blockedUntilUtc = DateTime.MinValue;
            StateCache.Save(CachedState.From(snapshot));
            _lastRefreshFailed = false;
            _statusItem.Text = $"Updated {snapshot.FetchedAt:h:mm:ss tt}";
            UpdateView();
        }
        catch (UsageException ex)
        {
            nextDelayMs = ex.RateLimited ? NextBackoff(ex.RetryAfter) : ErrorRetryMs;
            OnFailure(ex.Message, nextDelayMs, ex.RateLimited);
        }
        catch (Exception ex)
        {
            nextDelayMs = ErrorRetryMs;
            OnFailure("Unexpected error: " + ex.Message, nextDelayMs, rateLimited: false);
        }
        finally
        {
            _refreshing = false;
            _timer.Interval = Math.Max(1000, nextDelayMs);
        }
    }

    private int NextBackoff(TimeSpan? retryAfter)
    {
        _backoffMs = retryAfter is { } wait
            ? (int)Math.Min(Math.Max(wait.TotalMilliseconds, MinRateLimitBackoffMs), MaxRateLimitBackoffMs)
            : _backoffMs == 0 ? FirstRateLimitBackoffMs : Math.Min(_backoffMs * 2, MaxRateLimitBackoffMs);
        return _backoffMs;
    }

    /// <summary>
    /// A failed refresh marks the numbers as outdated (greyed with "?", then "--" if too old).
    /// They are never shown as if they were live.
    /// </summary>
    private void OnFailure(string message, int retryInMs, bool rateLimited)
    {
        if (rateLimited)
        {
            _blockedUntilUtc = DateTime.UtcNow.AddMilliseconds(retryInMs);
            StateCache.Save(CachedState.From(_last, DateTimeOffset.Now.AddMilliseconds(retryInMs)));
        }

        _lastRefreshFailed = true;
        _statusItem.Text = _last is null
            ? message
            : $"{message} Numbers may be outdated (last read {_last.FetchedAt.LocalDateTime:h:mm tt}).";
        UpdateView();
    }

    /// <summary>Redraws icon, tooltip, taskbar text and menu from the last reading and how fresh it is.</summary>
    private void UpdateView()
    {
        DateTimeOffset? fetched = _last?.FetchedAt;
        UsageWindow? five = _last?.FiveHour;
        UsageWindow? seven = _last?.SevenDay;
        Freshness fiveFreshness = FreshnessRules.Evaluate(five, fetched, _lastRefreshFailed);
        Freshness sevenFreshness = FreshnessRules.Evaluate(seven, fetched, _lastRefreshFailed);

        _tray.Icon = SwapIcon(IconRenderer.CreateIcon(five, fiveFreshness, seven, sevenFreshness));

        bool nothing = fiveFreshness == Freshness.Expired && sevenFreshness == Freshness.Expired;
        bool outdated = fiveFreshness == Freshness.Stale || sevenFreshness == Freshness.Stale;
        _tray.Text = Clip(
            nothing ? "Claude usage: no current data"
            : $"Claude 5h {Pct(five, fiveFreshness)}  7d {Pct(seven, sevenFreshness)}" + (outdated ? " (outdated)" : ""),
            63);

        _overlay.SetUsage(five, fiveFreshness, seven, sevenFreshness);
        _fiveItem.Text = "5-hour: " + Describe(five, fiveFreshness);
        _sevenItem.Text = "7-day: " + Describe(seven, sevenFreshness);
    }

    /// <summary>Installs a new icon and disposes the previous one so GDI handles don't leak.</summary>
    private Icon SwapIcon(Icon next)
    {
        var old = _currentIcon;
        _currentIcon = next;
        old?.Dispose();
        return next;
    }

    private static string Pct(UsageWindow? w, Freshness f) =>
        w?.Utilization is double u && f != Freshness.Expired
            ? $"{Math.Round(u):0}%" + (f == Freshness.Stale ? "?" : "")
            : "--";

    private static string Describe(UsageWindow? w, Freshness f)
    {
        if (w?.Utilization is not double u || f == Freshness.Expired)
            return "no current data";

        string text = $"{Math.Round(u):0}% used" + (f == Freshness.Stale ? " (may be outdated)" : "");
        if (w.ResetsAt is DateTimeOffset reset)
            text += $" · resets {FormatRemaining(reset)} ({reset.LocalDateTime:ddd h:mm tt})";
        return text;
    }

    private static string FormatRemaining(DateTimeOffset reset)
    {
        TimeSpan left = reset - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return "now";
        if (left.TotalDays >= 1) return $"in {(int)left.TotalDays}d {left.Hours}h";
        if (left.TotalHours >= 1) return $"in {(int)left.TotalHours}h {left.Minutes}m";
        return $"in {Math.Max(1, left.Minutes)}m";
    }

    private static string Clip(string s, int max) =>
        s.Length <= max ? s : s[..max];

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _viewTimer.Dispose();
        _cts.Cancel();
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Dispose();
        _currentIcon?.Dispose();
        _menu.Dispose();
        _timer.Dispose();
        _service.Dispose();
        _cts.Dispose();
        base.ExitThreadCore();
    }
}
