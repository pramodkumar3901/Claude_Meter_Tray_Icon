using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClaudeMeterTrayIcon;

/// <summary>
/// Draws the tray icon on a 16x16 pixel grid with a hand-made 5x7 digit font, then scales it
/// to the tray size. Anti-aliased system fonts are unreadable at 16px, so every pixel is placed
/// by hand. Top half = 5-hour usage, bottom half = 7-day usage. Each half is a coloured block
/// (green below 70, amber 70-89, red 90+) with the percentage in white.
/// </summary>
public static class IconRenderer
{
    private const int Grid = 16;

    private static readonly Color Green = Color.FromArgb(46, 158, 91);
    private static readonly Color Amber = Color.FromArgb(214, 150, 0);
    private static readonly Color Red = Color.FromArgb(213, 66, 66);
    private static readonly Color Grey = Color.FromArgb(107, 114, 128);

    // 5 wide x 7 tall digits, '#' = lit pixel.
    private static readonly string[][] Glyphs =
    {
        new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." }, // 0
        new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." }, // 1
        new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" }, // 2
        new[] { ".###.", "#...#", "....#", "..##.", "....#", "#...#", ".###." }, // 3
        new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." }, // 4
        new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." }, // 5
        new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." }, // 6
        new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." }, // 7
        new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." }, // 8
        new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." }, // 9
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// Creates a managed Icon sized for the tray at the current DPI
    /// (16px at 100%, 24px at 150%, 32px at 200%). Caller owns and must Dispose it.
    /// </summary>
    internal static Icon CreateIcon(UsageWindow? fiveHour, Freshness fiveFreshness, UsageWindow? sevenDay, Freshness sevenFreshness)
    {
        int size = Math.Clamp(SystemInformation.SmallIconSize.Height, Grid, 32);
        using Bitmap bmp = Render(fiveHour, fiveFreshness, sevenDay, sevenFreshness, size);
        IntPtr handle = bmp.GetHicon();
        try
        {
            // Clone so the icon owns its own handle, then release the GDI handle.
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>Renders a square icon bitmap of the given pixel size.</summary>
    internal static Bitmap Render(
        UsageWindow? fiveHour, Freshness fiveFreshness, UsageWindow? sevenDay, Freshness sevenFreshness, int size)
    {
        using var logical = new Bitmap(Grid, Grid, PixelFormat.Format32bppArgb);
        DrawBlock(logical, 0, fiveHour?.Utilization, fiveFreshness);
        DrawBlock(logical, Grid / 2, sevenDay?.Utilization, sevenFreshness);

        if (size == Grid)
            return (Bitmap)logical.Clone();

        var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(scaled);
        g.InterpolationMode = size % Grid == 0
            ? InterpolationMode.NearestNeighbor
            : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(logical, 0, 0, size, size);
        return scaled;
    }

    /// <summary>Draws one 16x8 block starting at row <paramref name="top"/>.</summary>
    private static void DrawBlock(Bitmap bmp, int top, double? utilization, Freshness freshness)
    {
        const int blockHeight = Grid / 2;

        // Expired readings show "--". Stale ones keep their digits but go grey, so a number that
        // may be outdated never looks like a live green/amber/red one.
        if (freshness == Freshness.Expired)
            utilization = null;
        Color fill = utilization is null || freshness == Freshness.Stale ? Grey : LevelColor(utilization.Value);

        for (int y = 0; y < blockHeight; y++)
        {
            for (int x = 0; x < Grid; x++)
            {
                bool corner = (x == 0 || x == Grid - 1) && (y == 0 || y == blockHeight - 1);
                if (!corner)
                    bmp.SetPixel(x, top + y, fill);
            }
        }

        // Digits are 7 tall in an 8 tall block: the top block hugs the top edge, the
        // bottom block hugs the bottom edge, which leaves the gap in the middle.
        int digitTop = top == 0 ? 0 : top + 1;

        if (utilization is null)
        {
            // "--" for no data.
            for (int x = 2; x <= 6; x++) { bmp.SetPixel(x, digitTop + 3, Color.White); }
            for (int x = 9; x <= 13; x++) { bmp.SetPixel(x, digitTop + 3, Color.White); }
            return;
        }

        // Two digits at most: 100% and over shows as 99.
        string text = Math.Clamp((int)Math.Round(utilization.Value), 0, 99).ToString();
        int width = text.Length * 5 + (text.Length - 1);
        int x0 = (Grid - width) / 2;

        for (int i = 0; i < text.Length; i++)
        {
            string[] glyph = Glyphs[text[i] - '0'];
            int gx = x0 + i * 6;
            for (int row = 0; row < 7; row++)
            {
                for (int col = 0; col < 5; col++)
                {
                    if (glyph[row][col] == '#')
                        bmp.SetPixel(gx + col, digitTop + row, Color.White);
                }
            }
        }
    }

    public static Color LevelColor(double utilization) =>
        utilization >= 90 ? Red : utilization >= 70 ? Amber : Green;
}
