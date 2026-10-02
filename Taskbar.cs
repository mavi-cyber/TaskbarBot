using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace TaskbarBot;

/// <summary>Finds the app buttons on the Windows taskbar and takes pictures of them.</summary>
static class Taskbar
{
    /// <summary>A picture of one taskbar icon plus a patch of empty taskbar to hide the real one.</summary>
    public sealed record IconShot(BitmapSource Icon, BitmapSource Cover);

    /// <summary>What is on the taskbar right now. All rectangles are screen pixels.</summary>
    public sealed class Scan
    {
        public List<Int32Rect> Apps { get; } = new();
        public List<Int32Rect> All { get; } = new();
        public Int32Rect? Start { get; set; }
        public Int32Rect? Clock { get; set; }
    }

    /// <summary>
    /// Reads the taskbar's buttons, or null when they cannot be read.
    /// UI Automation is slow, so call this off the UI thread.
    /// </summary>
    public static Scan? Read()
    {
        try
        {
            IntPtr bar = FindWindow("Shell_TrayWnd", null);
            if (bar == IntPtr.Zero) return null;

            var scan = new Scan();
            AutomationElement? classicList = null;
            var elements = AutomationElement.FromHandle(bar).FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            foreach (AutomationElement element in elements)
            {
                var info = element.Current;
                string cls = info.ClassName ?? "";
                if (cls == "MSTaskListWClass") classicList = element;
                if (info.ControlType != ControlType.Button && !cls.Contains("Button")
                    && cls != "Start" && cls != "TrayClockWClass") continue;
                Rect r = info.BoundingRectangle;
                if (r.IsEmpty || r.Width < 8 || r.Height < 8) continue;

                var rect = new Int32Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
                scan.All.Add(rect);
                if (cls == "Taskbar.TaskListButtonAutomationPeer") scan.Apps.Add(rect);
                else if (info.AutomationId == "StartButton" || cls == "Start") scan.Start = rect;
                else if (cls is "SystemTray.OmniButton" or "TrayClockWClass") scan.Clock = rect;
            }

            // The older taskbar (Windows 10, or Windows 11 with a classic-taskbar tool) names things
            // differently: its app buttons are the items of the MSTaskListWClass list.
            if (scan.Apps.Count == 0 && classicList is not null)
                foreach (AutomationElement item in classicList.FindAll(TreeScope.Descendants,
                             System.Windows.Automation.Condition.TrueCondition))
                {
                    var info = item.Current;
                    Rect r = info.BoundingRectangle;
                    if (info.ControlType != ControlType.Button && info.ControlType != ControlType.MenuItem) continue;
                    if (r.IsEmpty || r.Width < 16 || r.Height < 16) continue;
                    var rect = new Int32Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
                    scan.Apps.Add(rect);
                    scan.All.Add(rect);
                }
            scan.Apps.Sort((p, q) => p.X.CompareTo(q.X));
            return scan;
        }
        catch (Exception)
        {
            // UI Automation throws a wide mix of exceptions when Explorer is busy or restarting.
            return null;
        }
    }

    /// <summary>Seconds since the last key press or mouse movement.</summary>
    public static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0 : 0;
    }

    public static bool LeftButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    public static long RecycleBinItems()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64NumItems : 0;
    }

    /// <summary>Visible bounds of the window the user is working in, or null for the desktop and shell.</summary>
    public static Int32Rect? ForegroundBounds()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        var name = new System.Text.StringBuilder(64);
        GetClassName(fg, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return null;
        if (DwmGetWindowAttribute(fg, 9, out RECT r, Marshal.SizeOf<RECT>()) != 0) return null;
        return new Int32Rect(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top));
    }

    /// <summary>
    /// Photographs the icon in one taskbar button. Returns null when the button is highlighted
    /// (active or hovered app), because its background cannot be told apart from the icon.
    /// </summary>
    public static IconShot? Capture(Int32Rect button, bool vertical)
    {
        int w = button.Width, h = button.Height;
        var px = new int[w * h];
        using (var bmp = new Drawing.Bitmap(w, h, Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(button.X, button.Y, 0, 0, new Drawing.Size(w, h));
            var data = bmp.LockBits(new Drawing.Rectangle(0, 0, w, h),
                Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppArgb);
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            bmp.UnlockBits(data);
        }

        // The line of pixels along the button's edge lies between two buttons, so it is bare
        // taskbar: a column when the buttons sit side by side, a row when they are stacked.
        int Bare(int x, int y) => vertical ? px[w + x] : px[y * w + 1];

        int highlighted = 0, probes = 0;
        if (vertical)
        {
            for (int x = w / 4; x < w * 3 / 4; x++, probes++)
                if (Differs(px[(int)(h * 0.13) * w + x], Bare(x, 0), 10)) highlighted++;
        }
        else
        {
            for (int y = h / 4; y < h * 3 / 4; y++, probes++)
                if (Differs(px[y * w + (int)(w * 0.13)], Bare(0, y), 10)) highlighted++;
        }
        if (highlighted > probes / 2) return null;

        var cover = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                cover[y * w + x] = Bare(x, y) | unchecked((int)0xFF000000);

        // Cut the icon out of the middle: flood in from the border over everything that still
        // looks like bare taskbar and make that transparent.
        int s = (int)Math.Round(Math.Min(w, h) * 0.62), x0 = (w - s) / 2, y0 = (h - s) / 2;
        var icon = new int[s * s];
        var isBack = new bool[s * s];
        var queue = new Queue<int>();
        void Visit(int x, int y)
        {
            if (x < 0 || y < 0 || x >= s || y >= s || isBack[y * s + x]) return;
            if (Differs(px[(y0 + y) * w + x0 + x], Bare(x0 + x, y0 + y), 12)) return;
            isBack[y * s + x] = true;
            queue.Enqueue(y * s + x);
        }
        for (int i = 0; i < s; i++) { Visit(i, 0); Visit(i, s - 1); Visit(0, i); Visit(s - 1, i); }
        while (queue.Count > 0)
        {
            int p = queue.Dequeue(), x = p % s, y = p / s;
            Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
        }
        int solid = 0;
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
                if (!isBack[y * s + x])
                {
                    icon[y * s + x] = px[(y0 + y) * w + x0 + x] | unchecked((int)0xFF000000);
                    solid++;
                }
        if (solid < s * s / 10) return null;   // nothing there worth throwing around

        return new IconShot(ToBitmap(icon, s, s), ToBitmap(cover, w, h));
    }

    static bool Differs(int a, int b, int tolerance)
    {
        for (int shift = 0; shift <= 16; shift += 8)
            if (Math.Abs(((a >> shift) & 0xFF) - ((b >> shift) & 0xFF)) > tolerance) return true;
        return false;
    }

    static BitmapSource ToBitmap(int[] pixels, int w, int h)
    {
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT rect, int size);
}
