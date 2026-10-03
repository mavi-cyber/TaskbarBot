using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace TaskbarBot;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "TaskbarBot.SingleInstance", out bool first);
        if (!first) return;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new BotWindow();
        window.Show();
        app.Run();
    }
}

/// <summary>
/// A transparent, click-through, always-on-top strip covering the taskbar and the space just
/// above it. The bots stand on the taskbar's top edge; the part over the taskbar is only drawn
/// on while a bot has borrowed an icon.
/// </summary>
sealed class BotWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "TaskbarBot";
    static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "size.txt");

    readonly StageView bot = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Forms.NotifyIcon tray = new();
    double lastTick;
    IntPtr hwnd;

    enum DockEdge { Bottom, Left, Top, Right }

    /// <summary>Which screen edge the taskbar is on and where exactly, in DIPs.</summary>
    readonly record struct Dock(DockEdge Edge, Rect Bar);

    Dock dock;
    DockEdge? pretendEdge;      // test switch: act as if the taskbar were docked here

    public BotWindow()
    {
        Title = AppName;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Content = bot;

        bot.Cell = LoadSize();
        ApplyDock(ReadDock());
        BuildTray();
        bot.BringToTop = RaiseToTop;

        // Test switches: --demo runs all ten moves in order, --scene <name> starts that scene,
        // --edge-tour pretends the taskbar moves to the next screen edge every few seconds.
        string[] args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "--edge-tour") >= 0)
        {
            pretendEdge = DockEdge.Bottom;
            var tour = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            tour.Tick += (_, _) => pretendEdge = (DockEdge)(((int)pretendEdge!.Value + 1) % 4);
            tour.Start();
        }
        var kickoff = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        kickoff.Tick += (_, _) =>
        {
            kickoff.Stop();
            if (Array.IndexOf(args, "--demo") >= 0) bot.PlayAll();
            int at = Array.IndexOf(args, "--scene");
            if (at >= 0 && at + 1 < args.Length) bot.PlayScene(args[at + 1]);
            at = Array.IndexOf(args, "--move");
            if (at >= 0 && at + 1 < args.Length && Enum.TryParse(args[at + 1], true, out Move move)) bot.Play(move);
        };
        kickoff.Start();

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };

        CompositionTarget.Rendering += (_, _) =>
        {
            // 30 updates a second is plenty for pixel art and halves the redraw cost.
            double now = clock.Elapsed.TotalSeconds;
            if (now - lastTick < 0.03) return;
            double dt = Math.Min(0.05, now - lastTick);
            lastTick = now;
            if (IsVisible) bot.Update(dt);
        };

        var housekeeping = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        housekeeping.Tick += (_, _) => Housekeep();
        housekeeping.Start();
    }

    /// <summary>Finds the taskbar on the primary screen: which edge it is docked to and its rectangle.</summary>
    Dock ReadDock()
    {
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        if (pretendEdge is { } pretend)
        {
            const double t = 48;
            return new Dock(pretend, pretend switch
            {
                DockEdge.Left => new Rect(0, 0, t, sh),
                DockEdge.Top => new Rect(0, 0, sw, t),
                DockEdge.Right => new Rect(sw - t, 0, t, sh),
                _ => new Rect(0, sh - t, sw, t),
            });
        }

        IntPtr bar = FindWindow("Shell_TrayWnd", null);
        if (bar != IntPtr.Zero && Forms.Screen.PrimaryScreen is { } screen && GetWindowRect(bar, out RECT r))
        {
            double scale = screen.Bounds.Width / sw;
            var rect = Rect.Intersect(new Rect(0, 0, sw, sh),
                new Rect(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale));
            if (!rect.IsEmpty)
            {
                DockEdge edge = rect.Width >= sw - 2
                    ? (rect.Top <= 1 && rect.Bottom < sh - 1 ? DockEdge.Top : DockEdge.Bottom)
                    : (rect.Left <= 1 ? DockEdge.Left : DockEdge.Right);
                return new Dock(edge, rect);
            }
        }
        Rect area = SystemParameters.WorkArea;
        return new Dock(DockEdge.Bottom, new Rect(0, area.Bottom, sw, Math.Max(0, sh - area.Bottom)));
    }

    static double AngleOf(DockEdge edge) => edge switch
    {
        DockEdge.Left => 90, DockEdge.Top => 180, DockEdge.Right => 270, _ => 0,
    };

    /// <summary>
    /// Lays the window along the taskbar's inner edge and turns the stage so that its "down"
    /// points at the taskbar: the bots stand on a bottom taskbar, hang from a top one and
    /// walk up and down the side of a left or right one.
    /// </summary>
    void ApplyDock(Dock d)
    {
        dock = d;
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        double above = 26 * bot.Cell + 40;
        switch (d.Edge)
        {
            case DockEdge.Left:
                Left = 0; Top = 0; Width = d.Bar.Right + above; Height = sh;
                break;
            case DockEdge.Top:
                Left = 0; Top = 0; Width = sw; Height = d.Bar.Bottom + above;
                break;
            case DockEdge.Right:
                Left = d.Bar.Left - above; Top = 0; Width = sw - Left; Height = sh;
                break;
            default:
                Left = 0; Top = d.Bar.Top - above; Width = sw; Height = sh - Top;
                break;
        }

        double angle = AngleOf(d.Edge);
        if (bot.StageAngle != angle || (angle == 0) != bot.LayoutTransform.Value.IsIdentity)
            bot.LayoutTransform = angle == 0 ? Transform.Identity : new RotateTransform(angle);
        bot.StageAngle = angle;
        bot.GroundY = above;
    }

    /// <summary>The taskbar changed edge: go full screen so the bots can leap across to it.</summary>
    void MoveTo(Dock next)
    {
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        Rect bar = next.Bar;
        Func<double, Point> landing = next.Edge switch
        {
            DockEdge.Left => x => new Point(bar.Right, x),
            DockEdge.Top => x => new Point(sw - x, bar.Bottom),
            DockEdge.Right => x => new Point(bar.Left, sh - x),
            _ => x => new Point(x, bar.Top),
        };
        bool sideways = next.Edge is DockEdge.Left or DockEdge.Right;
        bot.BeginTransit(bot.FeetOnScreen(), AngleOf(next.Edge), sideways ? sh : sw, landing, () => ApplyDock(next));

        dock = next;
        Left = 0; Top = 0; Width = sw; Height = sh;
        bot.LayoutTransform = Transform.Identity;
        bot.StageAngle = 0;
    }

    /// <summary>Puts the strip above the taskbar, which is itself an always-on-top window.</summary>
    void RaiseToTop()
    {
        if (IsVisible && hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    void Housekeep()
    {
        if (!bot.InTransit)
        {
            Dock now = ReadDock();
            if (now.Edge != dock.Edge && IsVisible && IsLoaded) MoveTo(now);
            else ApplyDock(now);
        }

        // Stay out of the way of fullscreen videos and games.
        bool fullscreen = ForegroundIsFullscreen();
        if (fullscreen && IsVisible)
        {
            bot.AbortAll();
            Hide();
        }
        else if (!fullscreen && !IsVisible) Show();

        RaiseToTop();
    }

    static bool ForegroundIsFullscreen()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        var name = new StringBuilder(64);
        GetClassName(fg, name, name.Capacity);
        string cls = name.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "XamlExplorerHostIslandWindow") return false;

        if (!GetWindowRect(fg, out RECT r)) return false;
        var screen = Forms.Screen.PrimaryScreen?.Bounds;
        return screen is { } s && r.Left <= s.Left && r.Top <= s.Top && r.Right >= s.Right && r.Bottom >= s.Bottom;
    }

    void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip();

        (string Text, Move Move)[] moves =
        {
            ("Walk", Move.Walk), ("Dash", Move.Dash), ("Jump", Move.Jump), ("Look around", Move.LookAround),
            ("Wave", Move.Wave), ("Dance", Move.Dance), ("Sleep", Move.Sleep), ("Spin", Move.Spin),
            ("Squash bounce", Move.Bounce), ("Peek", Move.Peek),
            ("Yawn", Move.Yawn), ("Sit and rest", Move.Rest), ("Check phone", Move.Phone), ("Sneeze", Move.Sneeze),
            ("Work out", Move.Workout), ("Drink coffee", Move.Coffee), ("Read a book", Move.Read),
            ("Eat", Move.Eat), ("Sweep up", Move.Sweep), ("Phone call", Move.Call), ("Sing", Move.Sing),
        };
        var movesMenu = new Forms.ToolStripMenuItem("Moves");
        foreach (var (text, move) in moves)
            movesMenu.DropDownItems.Add(text, null, (_, _) => bot.Play(move));
        movesMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        movesMenu.DropDownItems.Add("Show all moves", null, (_, _) => bot.PlayAll());
        menu.Items.Add(movesMenu);

        var scenes = new Forms.ToolStripMenuItem("Scenes");
        foreach (var (name, title) in StageView.Scenes)
            scenes.DropDownItems.Add(title, null, (_, _) => bot.PlayScene(name));
        menu.Items.Add(scenes);

        var size = new Forms.ToolStripMenuItem("Size");
        (string Text, double Cell)[] sizes = { ("Small", 3), ("Medium", 5), ("Large", 8) };
        foreach (var (text, cell) in sizes)
        {
            var item = new Forms.ToolStripMenuItem(text) { Checked = bot.Cell == cell };
            item.Click += (_, _) =>
            {
                bot.Cell = cell;
                SaveSize(cell);
                if (!bot.InTransit) ApplyDock(dock);
                foreach (Forms.ToolStripMenuItem other in size.DropDownItems) other.Checked = other == item;
            };
            size.DropDownItems.Add(item);
        }
        menu.Items.Add(size);

        var autostart = new Forms.ToolStripMenuItem("Start with Windows") { Checked = AutostartEnabled() };
        autostart.Click += (_, _) =>
        {
            SetAutostart(!autostart.Checked);
            autostart.Checked = AutostartEnabled();
        };
        menu.Items.Add(autostart);

        menu.Items.Add("Exit", null, (_, _) =>
        {
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        });

        tray.Text = AppName;
        tray.Icon = MakeIcon();
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
    }

    /// <summary>The tray icon is the program's own icon.</summary>
    static System.Drawing.Icon MakeIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon)
                return icon;
        }
        catch (IOException) { }
        catch (ArgumentException) { }
        return System.Drawing.SystemIcons.Application;
    }

    static double LoadSize()
    {
        try
        {
            if (File.Exists(SettingsFile) && double.TryParse(File.ReadAllText(SettingsFile), out double cell))
                return Math.Clamp(cell, 2, 12);
        }
        catch (IOException) { }
        return 5;
    }

    static void SaveSize(double cell)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, cell.ToString());
        }
        catch (IOException) { }
    }

    static bool AutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is not null;
    }

    static void SetAutostart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable) key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(AppName, throwOnMissingValue: false);
    }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
