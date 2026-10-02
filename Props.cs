using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskbarBot;

/// <summary>
/// Things the bots handle in scenes. Most are Windows' own colour emoji; the few with no emoji
/// (crowbar, spray can, sign, graffiti) are built from flat blocks like the bots themselves.
/// </summary>
static class Props
{
    public const string
        Can = "\U0001F96B", Bone = "\U0001F9B4", Fish = "\U0001F41F", Boot = "\U0001F97E",
        Paper = "\U0001F4C4", Banana = "\U0001F34C", Bin = "\U0001F5D1️", Sponge = "\U0001F9FD",
        Ball = "\U0001F3B1", Bang = "❗", Anger = "\U0001F4A2";

    public static readonly Brush Steel = Frozen(0x6B, 0x72, 0x7C);
    public static readonly Brush Wood = Frozen(0x9A, 0x6B, 0x3F);
    public static readonly Brush Line = Frozen(0xDD, 0xDD, 0xDD);
    public static readonly Brush Pink = Frozen(0xFF, 0x3D, 0xB4);
    public static readonly Brush Lime = Frozen(0xB6, 0xFF, 0x3D);
    static readonly Brush White = Frozen(0xFA, 0xFA, 0xF5);
    static readonly Brush Red = Frozen(0xE0, 0x2B, 0x2B);
    static readonly Brush Teal = Frozen(0x2E, 0xC4, 0xB6);

    static readonly Dictionary<string, ImageSource> glyphs = new();

    /// <summary>The colour emoji as a square picture. WPF cannot draw colour emoji itself, Emoji.Wpf can.</summary>
    static ImageSource Glyph(string emoji)
    {
        if (glyphs.TryGetValue(emoji, out ImageSource? cached)) return cached;

        var text = new global::Emoji.Wpf.TextBlock { Text = emoji, FontSize = 64 };
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size s = text.DesiredSize;
        double side = Math.Ceiling(Math.Max(s.Width, s.Height));
        text.Arrange(new Rect((side - s.Width) / 2, (side - s.Height) / 2, s.Width, s.Height));
        int n = (int)side;
        var bitmap = new RenderTargetBitmap(n, n, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(text);

        // Trim the empty line spacing around the glyph so "size" means the visible picture.
        var px = new int[n * n];
        bitmap.CopyPixels(px, n * 4, 0);
        int x0 = n, y0 = n, x1 = -1, y1 = -1;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if ((px[y * n + x] >>> 24) > 16)
                {
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                }
        ImageSource result = bitmap;
        if (x1 >= x0)
        {
            int box = Math.Max(x1 - x0, y1 - y0) + 1;
            int left = Math.Clamp((x0 + x1 + 1 - box) / 2, 0, n - box), top = Math.Clamp((y0 + y1 + 1 - box) / 2, 0, n - box);
            result = new CroppedBitmap(bitmap, new Int32Rect(left, top, box, box));
        }
        result.Freeze();
        return glyphs[emoji] = result;
    }

    public static void Draw(DrawingContext dc, string emoji, Point centre, double size, double angle = 0, double opacity = 1)
    {
        if (opacity <= 0) return;
        dc.PushOpacity(opacity);
        dc.PushTransform(new RotateTransform(angle, centre.X, centre.Y));
        dc.DrawImage(Glyph(emoji), new Rect(centre.X - size / 2, centre.Y - size / 2, size, size));
        dc.Pop();
        dc.Pop();
    }

    /// <summary>A bar with a hooked end; the hook sits at the pivot.</summary>
    public static void Crowbar(DrawingContext dc, Point pivot, double angle, double c, int side)
    {
        dc.PushTransform(new ScaleTransform(side, 1, pivot.X, pivot.Y));     // side -1 mirrors it
        dc.PushTransform(new RotateTransform(angle, pivot.X, pivot.Y));
        dc.DrawRectangle(Steel, null, new Rect(pivot.X, pivot.Y - 0.5 * c, 8 * c, 0.9 * c));
        dc.DrawRectangle(Steel, null, new Rect(pivot.X - 0.2 * c, pivot.Y - 0.5 * c, 0.9 * c, 1.8 * c));
        dc.Pop();
        dc.Pop();
    }

    public static void SprayCan(DrawingContext dc, Point centre, double angle, double c)
    {
        dc.PushTransform(new RotateTransform(angle, centre.X, centre.Y));
        dc.DrawRectangle(Teal, null, new Rect(centre.X - c, centre.Y - 1.2 * c, 2 * c, 3 * c));
        dc.DrawRectangle(White, null, new Rect(centre.X - c, centre.Y - 0.2 * c, 2 * c, 0.8 * c));
        dc.DrawRectangle(Steel, null, new Rect(centre.X - 0.5 * c, centre.Y - 2 * c, c, 0.8 * c));
        dc.Pop();
    }

    /// <summary>A STRIKE! board on a stick, held from the bottom of the stick.</summary>
    public static void Sign(DrawingContext dc, Point foot, double c)
    {
        dc.DrawRectangle(Wood, null, new Rect(foot.X - 0.35 * c, foot.Y - 3 * c, 0.7 * c, 3 * c));
        var board = new Rect(foot.X - 9 * c, foot.Y - 8 * c, 18 * c, 5 * c);
        dc.DrawRectangle(Red, null, board);
        board.Inflate(-0.5 * c, -0.5 * c);
        dc.DrawRectangle(White, null, board);
        Text(dc, "STRIKE!", Red, 3.3 * c, new Point(foot.X, foot.Y - 5.6 * c), "Segoe UI Black");
    }

    /// <summary>Scrawl over a rectangle, as wet (1) or as wiped off (0) as asked.</summary>
    public static void Graffiti(DrawingContext dc, Rect r, double opacity)
    {
        if (opacity <= 0) return;
        dc.PushOpacity(opacity);
        dc.PushTransform(new RotateTransform(-7, r.X + r.Width / 2, r.Y + r.Height / 2));
        Text(dc, "BOTS", Pink, r.Height * 0.5, new Point(r.X + r.Width / 2, r.Y + r.Height * 0.27), "Ink Free");
        Text(dc, "RULE!", Lime, r.Height * 0.5, new Point(r.X + r.Width / 2, r.Y + r.Height * 0.72), "Ink Free");
        dc.Pop();
        dc.Pop();
    }

    static void Text(DrawingContext dc, string s, Brush brush, double size, Point centre, string font)
    {
        var face = new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var text = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, 1.0);
        dc.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
