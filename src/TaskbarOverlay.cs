using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeMeterTrayIcon;

/// <summary>
/// A small borderless window parked on the taskbar just left of the system tray, showing
/// readable text like the "ENG IN" and battery indicators:
///   5h 3%
///   7d 11%
/// A 16px tray icon can never hold readable text, so this carries the numbers.
/// It never takes focus, hides during full-screen apps, and re-places itself every 2 seconds.
/// Only horizontal (top or bottom) taskbars are supported.
/// </summary>
internal sealed class TaskbarOverlay : Form
{
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    private readonly System.Windows.Forms.Timer _placeTimer = new() { Interval = 2000 };
    private UsageWindow? _five;
    private UsageWindow? _seven;
    private Freshness _fiveFreshness = Freshness.Expired;
    private Freshness _sevenFreshness = Freshness.Expired;
    private Font? _regular;
    private Font? _bold;
    private float _fontPx;
    private bool _light;
    private bool _enabled = true;

    public TaskbarOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        _placeTimer.Tick += (_, _) => Reposition();
        _placeTimer.Start();
    }

    /// <summary>Turns the overlay on or off (the tray icon is unaffected).</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool OverlayEnabled
    {
        get => _enabled;
        set { _enabled = value; Reposition(); }
    }

    internal void SetUsage(UsageWindow? five, Freshness fiveFreshness, UsageWindow? seven, Freshness sevenFreshness)
    {
        _five = five;
        _seven = seven;
        _fiveFreshness = fiveFreshness;
        _sevenFreshness = sevenFreshness;
        Reposition();
        Invalidate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32));
        if (_regular is null || _bold is null) return;

        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        int lineHeight = ClientSize.Height / 2;
        // Top line = 5-hour window (percent, h:mm left). Bottom line = 7-day window (percent, d:hh:mm left).
        DrawLine(g, _five, _fiveFreshness, weekly: false, 0, lineHeight);
        DrawLine(g, _seven, _sevenFreshness, weekly: true, lineHeight, lineHeight);
    }

    private void DrawLine(Graphics g, UsageWindow? window, Freshness freshness, bool weekly, int y, int lineHeight)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.VerticalCenter;
        var box = new Rectangle(6, y, ClientSize.Width - 6, lineHeight);

        // The time left is deliberately dull so the coloured percentage stands out.
        Color dim = _light ? Color.FromArgb(110, 110, 110) : Color.FromArgb(150, 150, 150);

        // Fresh: coloured percent. Stale: grey percent with "?" (may be outdated). Expired: "--".
        string percent = window?.Utilization is double u && freshness != Freshness.Expired
            ? $"{Math.Round(u):0}%" + (freshness == Freshness.Stale ? "?" : "")
            : "--";
        Color percentColor = window?.Utilization is double v && freshness == Freshness.Fresh
            ? IconRenderer.LevelColor(v)
            : dim;
        TextRenderer.DrawText(g, percent, _bold, box, percentColor, flags);

        int percentWidth = TextRenderer.MeasureText(g, percent, _bold, Size.Empty, flags).Width;
        var timeBox = new Rectangle(box.X + percentWidth + 5, y, box.Width - percentWidth - 5, lineHeight);
        // "100%?" is the widest percent text, so the window width below leaves room for it.
        TextRenderer.DrawText(g, "· " + FormatTimeLeft(window?.ResetsAt, weekly), _regular, timeBox, dim, flags);
    }

    /// <summary>5-hour: h:mm (4:30). 7-day: d:hh:mm (1:10:23).</summary>
    private static string FormatTimeLeft(DateTimeOffset? reset, bool weekly)
    {
        if (reset is not DateTimeOffset r) return "--";
        TimeSpan left = r - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return "--"; // already reset: the next refresh brings the new window
        int minutes = (int)left.TotalMinutes;

        return weekly
            ? $"{minutes / 1440}:{minutes % 1440 / 60:00}:{minutes % 60:00}"
            : $"{minutes / 60}:{minutes % 60:00}";
    }

    private void Reposition()
    {
        if (IsDisposed || !IsHandleCreated && !_enabled) return;

        if (!_enabled || IsFullScreenAppRunning())
        {
            if (Visible) Hide();
            return;
        }

        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out RECT tr))
        {
            if (Visible) Hide();
            return;
        }

        int trayHeight = tr.Bottom - tr.Top;
        int trayWidth = tr.Right - tr.Left;
        if (trayHeight <= 0 || trayHeight > trayWidth) // vertical taskbar: not supported
        {
            if (Visible) Hide();
            return;
        }

        _light = IsLightTaskbar();

        // Text size follows the taskbar height so it scales with DPI.
        float px = Math.Max(11f, trayHeight * 0.28f);
        if (_regular is null || Math.Abs(px - _fontPx) > 0.1f)
        {
            _regular?.Dispose();
            _bold?.Dispose();
            _regular = new Font("Segoe UI", px, FontStyle.Regular, GraphicsUnit.Pixel);
            _bold = new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontPx = px;
        }

        const TextFormatFlags measureFlags = TextFormatFlags.NoPadding;
        Size huge = new(int.MaxValue, int.MaxValue);
        Size percentSize = TextRenderer.MeasureText("100%?", _bold, huge, measureFlags);
        Size timeSize = TextRenderer.MeasureText("· 6:23:59", _regular, huge, measureFlags);
        int width = percentSize.Width + 5 + timeSize.Width + 14;
        int height = Math.Min(trayHeight - 4, percentSize.Height * 2 + 4);

        // Sit just left of the notification area (the chevron, ENG IN, wifi, clock...).
        int rightEdge = tr.Right - 220; // fallback if the tray window can't be measured
        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && GetWindowRect(notify, out RECT nr) && nr.Right > nr.Left)
            rightEdge = nr.Left;

        Bounds = new Rectangle(rightEdge - width - 6, tr.Top + (trayHeight - height) / 2, width, height);

        if (!Visible) Show();
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Invalidate();
    }

    private static bool IsLightTaskbar() =>
        Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is int v && v == 1;

    /// <summary>True when a full-screen app, game or presentation is up (QUNS_BUSY, D3D full screen, presentation).</summary>
    private static bool IsFullScreenAppRunning() =>
        SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _placeTimer.Dispose();
            _regular?.Dispose();
            _bold?.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
