using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace TaskbarBot;

/// <summary>
/// The strip the bots live in. It covers the area just above the taskbar plus the taskbar itself;
/// GroundY is the taskbar's top edge. This file holds the frame loop, the mouse tracking, the
/// drawing and the icon-passing game; the scripted scenes and what triggers them are in Scenes.cs.
/// </summary>
public sealed partial class StageView : FrameworkElement
{
    enum Phase { None, Scanning, Fetch, Grab, Hold, Windup, Flight, Return }

    /// <summary>A photographed taskbar icon, placed in this element's coordinates.</summary>
    sealed record Shot(ImageSource Icon, ImageSource Cover, Rect Slot, double Size, double ScreenWidth, double ScreenHeight)
    {
        public Point Centre => new(Slot.X + Slot.Width / 2, Slot.Y + Slot.Height / 2);
    }

    readonly Bot[] bots = { new Bot(), new Bot() };
    readonly Random rng = new();

    /// <summary>Y of the taskbar's top edge inside this element.</summary>
    public double GroundY { get; set; }

    /// <summary>Asked for when the stage needs to draw over the taskbar.</summary>
    public Action? BringToTop { get; set; }

    public double Cell
    {
        get => bots[0].Cell;
        set { foreach (Bot b in bots) b.Cell = value; }
    }

    double Edge => Bot.Cols * Cell / 2 + 8;

    // Icon game state.
    Phase phase;
    double pt, holdFor, flightTime, otherSpot, spin, ballAngle, returnAngle;
    int holder, passesLeft, scanId;
    bool hop, urgent;
    Shot? shot;
    Point ball, from;
    Throw thrown;

    // Mouse tracking, in this element's own coordinates: x runs along the taskbar and y grows
    // towards it, whichever screen edge the taskbar is docked to.
    double cursorVx, cursorVy, dpiScale = 1, stageAngle;
    Point cursor;
    bool cursorSeen;

    /// <summary>How this element is turned on screen: 0 taskbar at the bottom, 90 left, 180 top, 270 right.</summary>
    public double StageAngle
    {
        get => stageAngle;
        set
        {
            if (value == stageAngle) return;
            stageAngle = value;
            cursorSeen = false;
        }
    }

    // A move to another screen edge in progress: one hop per bot, in screen coordinates.
    sealed record Hop(Point From, Point To, double FromAngle, double Turn, double NewX, double Delay, double Time);
    Hop[]? hops;
    double hopClock;
    Action? landed;

    public bool InTransit => hops is not null;

    double fdt;         // seconds in the current frame
    int lastHash, frame;

    public StageView()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        IsHitTestVisible = false;
        bots[0].OtherX = () => bots[1].X;
        bots[1].OtherX = () => bots[0].X;
        nextAmbient = 30 + rng.NextDouble() * 40;
    }

    public void Play(Move move) => bots[rng.Next(bots.Length)].Play(move);

    public void PlayAll() => bots[0].PlayAll();

    /// <summary>Stops whatever scene or game is running and uncovers anything hidden on the taskbar.</summary>
    public void AbortAll()
    {
        if (phase != Phase.None) EndGame();
        if (scene is not null) EndScene();
    }

    public void Update(double dt)
    {
        double w = ActualWidth;
        if (w <= 0 || PresentationSource.FromVisual(this) is null) return;
        fdt = dt;
        frame++;
        if (hops is not null)
        {
            StepTransit(dt);
            InvalidateVisual();
            return;
        }

        TrackCursor(dt);
        foreach (Bot b in bots)
        {
            if (b.External) b.BeginPose(dt, w);
            else b.Update(dt, w, cursor, GroundY);
        }

        if (scene is not null) StepScene();
        else if (phase != Phase.None) StepGame(dt);
        else Direct(dt);
        foreach (Bot b in bots) b.EndFrame(dt);

        var h = new HashCode();
        foreach (Bot b in bots) h.Add(b.PoseHash());
        h.Add(phase); h.Add(ball); h.Add(ballAngle);
        if (scene is not null) h.Add(frame);
        int hash = h.ToHashCode();
        if (hash != lastHash)
        {
            lastHash = hash;
            InvalidateVisual();
        }
    }

    void TrackCursor(double dt)
    {
        dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        if (!GetCursorPos(out POINT p)) return;
        Point at = PointFromScreen(new Point(p.X, p.Y));
        cursorVx = dt > 0 && cursorSeen ? (at.X - cursor.X) / dt : 0;
        cursorVy = dt > 0 && cursorSeen ? (at.Y - cursor.Y) / dt : 0;
        cursor = at;
        cursorSeen = true;
        WatchMouse(dt);
    }

    bool CursorOnTaskbar => cursor.Y >= GroundY;

    /// <summary>True when the mouse is close to the taskbar or will get there within 0.4 s.</summary>
    bool CursorThreatens()
    {
        double gap = GroundY - cursor.Y;
        return gap < 110 || (cursorVy > 0 && gap / cursorVy < 0.4);
    }

    static double Clamp01(double u) => Math.Clamp(u, 0, 1);
    static double Ease(double u) { u = Clamp01(u); return u * u * (3 - 2 * u); }
    static double Arc(double u) => 4 * u * (1 - u);
    static Point Lerp(Point a, Point b, double u) => new(a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u);

    /// <summary>A screen rectangle (pixels) in this element's coordinates, however the stage is turned.</summary>
    Rect ToStage(Int32Rect r) =>
        new(PointFromScreen(new Point(r.X, r.Y)), PointFromScreen(new Point(r.X + r.Width, r.Y + r.Height)));

    /// <summary>True when the button sits in the taskbar the bots are standing on.</summary>
    bool OnTaskbar(Rect slot) => Math.Abs(slot.Top - GroundY) < 8;

    /// <summary>Photographs one taskbar button. Null when it is highlighted or empty.</summary>
    Shot? Shoot(Int32Rect button) =>
        Taskbar.Capture(button, vertical: StageAngle % 180 != 0) is { } s
            ? new Shot(s.Icon, s.Cover, ToStage(button), s.Icon.PixelWidth / dpiScale,
                button.Width / dpiScale, button.Height / dpiScale)
            : null;

    double Snap(double v) => Math.Round(v * dpiScale) / dpiScale;

    void DrawIcon(DrawingContext dc, Shot s, Point centre, double angle = 0)
    {
        angle -= StageAngle;        // pictures of the screen stay upright on the screen
        double left = Snap(centre.X - s.Size / 2), top = Snap(centre.Y - s.Size / 2);
        if (angle != 0) dc.PushTransform(new RotateTransform(angle, centre.X, centre.Y));
        dc.DrawImage(s.Icon, new Rect(left, top, s.Size, s.Size));
        if (angle != 0) dc.Pop();
    }

    /// <summary>Hides the real icon under a patch of bare taskbar.</summary>
    void DrawCover(DrawingContext dc, Shot s)
    {
        Point m = s.Centre;
        dc.PushTransform(new RotateTransform(-StageAngle, m.X, m.Y));
        dc.DrawImage(s.Cover, new Rect(m.X - s.ScreenWidth / 2, m.Y - s.ScreenHeight / 2, s.ScreenWidth, s.ScreenHeight));
        dc.Pop();
    }

    // ---- Moving to another screen edge ----

    /// <summary>Where each bot's feet are on the screen right now, in DIPs.</summary>
    public Point[] FeetOnScreen()
    {
        var feet = new Point[bots.Length];
        for (int i = 0; i < bots.Length; i++)
        {
            Point p = PointToScreen(new Point(double.IsNaN(bots[i].X) ? ActualWidth / 2 : bots[i].X, GroundY));
            feet[i] = new Point(p.X / dpiScale, p.Y / dpiScale);
        }
        return feet;
    }

    /// <summary>
    /// The taskbar has moved. Call with the old layout still in place, then make this element cover
    /// the whole screen unrotated: the bots crouch, leap across the screen turning to the new "down",
    /// and land on the new edge. landing maps a position along the new taskbar to a screen point.
    /// </summary>
    public void BeginTransit(Point[] from, double toAngle, double newLength, Func<double, Point> landing, Action done)
    {
        AbortAll();
        double turn = (toAngle - StageAngle + 540) % 360 - 180;         // the short way round
        var next = new Hop[bots.Length];
        for (int i = 0; i < bots.Length; i++)
        {
            Bot b = bots[i];
            b.External = true;
            double along = Math.Clamp((b.X - Edge) / Math.Max(1, ActualWidth - 2 * Edge), 0, 1);
            double newX = Edge + along * (newLength - 2 * Edge);
            Point to = landing(newX);
            double time = Math.Clamp((to - from[i]).Length / 900, 0.7, 1.5);
            next[i] = new Hop(from[i], to, StageAngle, turn, newX, 0.3 * i, time);
        }
        hops = next;
        hopClock = 0;
        landed = done;
    }

    const double CrouchTime = 0.25;

    void StepTransit(double dt)
    {
        hopClock += dt;
        bool allDown = true;
        for (int i = 0; i < bots.Length; i++)
        {
            Bot b = bots[i];
            Hop h = hops![i];
            double t = hopClock - h.Delay;
            b.BeginPose(dt, ActualWidth);
            if (t < CrouchTime)
            {
                b.Squash(Clamp01(t / CrouchTime));
                allDown = false;
            }
            else if (t < CrouchTime + h.Time)
            {
                double u = (t - CrouchTime) / h.Time;
                b.ArmsUp();
                b.Stretch(Math.Max(0, 1 - u * 3));
                b.Look(0, u < 0.5 ? -0.3 : 0.3);
                allDown = false;
            }
        }
        if (!allDown) return;

        Hop[] finished = hops!;
        hops = null;
        landed?.Invoke();                   // the window takes its place on the new edge
        landed = null;
        for (int i = 0; i < bots.Length; i++)
        {
            bots[i].X = finished[i].NewX;
            bots[i].External = false;
            bots[i].Kick(5);
        }
    }

    void DrawTransit(DrawingContext dc)
    {
        for (int i = 0; i < bots.Length; i++)
        {
            Bot b = bots[i];
            Hop h = hops![i];
            double u = Ease((hopClock - h.Delay - CrouchTime) / h.Time);
            Point p = Lerp(h.From, h.To, u);
            dc.PushTransform(new TranslateTransform(p.X - b.X, p.Y - GroundY));
            dc.PushTransform(new RotateTransform(h.FromAngle + h.Turn * u, b.X, GroundY));
            b.Draw(dc, GroundY);
            dc.Pop();
            dc.Pop();
        }
    }

    // ---- The icon-passing game ----

    void SetPhase(Phase next)
    {
        phase = next;
        pt = 0;
    }

    void StepGame(double dt)
    {
        if (shot is null && phase != Phase.Scanning) { EndGame(); return; }
        pt += dt;
        double c = Cell;
        Bot a = bots[holder], b = bots[1 - holder];
        double size = shot?.Size ?? 0;

        switch (phase)
        {
            case Phase.Scanning:
                return;

            case Phase.Fetch:
            {
                if (CursorThreatens() || pt > 25) { EndGame(); return; }
                bool there = a.WalkToward(shot!.Centre.X, dt);
                bool ready = b.WalkToward(otherSpot, dt);
                if (!ready) return;
                if (!there) { b.LookAt(new Point(a.X, GroundY), GroundY); return; }
                ball = shot.Centre;
                ballAngle = 0;
                BringToTop?.Invoke();
                SetPhase(Phase.Grab);
                return;
            }

            case Phase.Grab:
            {
                double u = Clamp01(pt / 0.55);
                a.Squash(Math.Sin(Math.PI * u));
                if (u > 0.4) a.ArmsUp();
                ball = Lerp(shot!.Centre, a.HoldPoint(GroundY, size), Ease(u));
                a.LookAt(ball, GroundY);
                b.LookAt(ball, GroundY);
                if (u >= 1)
                {
                    passesLeft = rng.Next(4, 9);
                    EnterHold(0.8);
                }
                break;
            }

            case Phase.Hold:
            {
                a.ArmsUp();
                a.Squash(Math.Max(0, 1 - pt / 0.15));
                if (hop && pt > 0.2 && pt < 1.0) a.Lift = 3 * c * Arc((pt - 0.2) / 0.4 % 1);
                ball = a.HoldPoint(GroundY, size);
                ballAngle = 0;
                a.LookAt(new Point(b.X, GroundY), GroundY);
                b.LookAt(ball, GroundY);
                if (pt >= holdFor)
                {
                    if (passesLeft <= 0) BeginReturn(false);
                    else SetPhase(Phase.Windup);
                }
                break;
            }

            case Phase.Windup:
            {
                double u = Clamp01(pt / 0.22);
                a.ArmsUp();
                a.Squash(u);
                ball = a.HoldPoint(GroundY, size);
                b.ArmsUp();
                b.LookAt(ball, GroundY);
                if (u >= 1)
                {
                    // A real throw: it peaks a set height above the hands and gravity decides how long it takes.
                    double apex = Math.Clamp(Math.Abs(b.X - a.X) * 0.3, 6 * c, 13 * c);
                    b.ArmsUp();
                    thrown = Throw.Between(ball, b.HoldPoint(GroundY, size), apex, Physics.Gravity * c);
                    spin = (rng.Next(2) == 0 ? -1 : 1) * (300 + rng.NextDouble() * 300);
                    SetPhase(Phase.Flight);
                }
                break;
            }

            case Phase.Flight:
            {
                double u = Clamp01(pt / thrown.Time);
                if (pt < 0.2)
                {
                    a.ArmsUp();
                    a.Stretch(1 - pt / 0.2);
                }
                b.ArmsUp();
                ball = thrown.At(Math.Min(pt, thrown.Time));
                ballAngle += spin * dt;
                a.LookAt(ball, GroundY);
                b.LookAt(ball, GroundY);
                if (u >= 1)
                {
                    holder = 1 - holder;
                    passesLeft--;
                    EnterHold(0.5 + rng.NextDouble() * 1.4);
                }
                break;
            }

            case Phase.Return:
            {
                double u = Clamp01(pt / flightTime);
                if (pt < 0.2) a.ArmsUp();
                ball = urgent ? Lerp(from, shot!.Centre, u) : thrown.At(Math.Min(pt, flightTime));
                ballAngle = returnAngle * (1 - u);
                a.LookAt(ball, GroundY);
                b.LookAt(ball, GroundY);
                if (u >= 1) { EndGame(); return; }
                break;
            }
        }

        // The icon is out of its slot: get it home before the mouse reaches the taskbar.
        if (CursorOnTaskbar) EndGame();
        else if (CursorThreatens() && !(phase == Phase.Return && urgent)) BeginReturn(true);
    }

    void EnterHold(double seconds)
    {
        hop = rng.Next(4) == 0;
        holdFor = hop ? Math.Max(seconds, 1.1) : seconds;
        SetPhase(Phase.Hold);
    }

    void BeginReturn(bool inAHurry)
    {
        from = ball;
        urgent = inAHurry;
        if (!inAHurry) thrown = Throw.Between(ball, shot!.Centre, 7 * Cell, Physics.Gravity * Cell);
        flightTime = inAHurry ? 0.16 : thrown.Time;
        returnAngle = ballAngle % 360;
        SetPhase(Phase.Return);
    }

    void EndGame()
    {
        scanId++;
        shot = null;
        foreach (Bot b in bots) b.External = false;
        SetPhase(Phase.None);
        InvalidateVisual();
    }

    void BeginCatch()
    {
        foreach (Bot b in bots) b.External = true;
        SetPhase(Phase.Scanning);
        int id = ++scanId;
        Task.Run(Taskbar.Read).ContinueWith(task =>
            Dispatcher.BeginInvoke(() => OnScanned(id, task.Result)));
    }

    void OnScanned(int id, Taskbar.Scan? scan)
    {
        if (id != scanId || phase != Phase.Scanning) return;
        if (scan is null || PresentationSource.FromVisual(this) is null) { EndGame(); return; }

        var candidates = new List<Int32Rect>();
        foreach (Int32Rect r in scan.Apps)
        {
            Rect slot = ToStage(r);
            double centre = slot.X + slot.Width / 2;
            if (OnTaskbar(slot) && centre > Edge && centre < ActualWidth - Edge)
                candidates.Add(r);
        }

        for (int attempt = 0; attempt < 6 && candidates.Count > 0; attempt++)
        {
            int pick = rng.Next(candidates.Count);
            Int32Rect r = candidates[pick];
            candidates.RemoveAt(pick);
            if (Shoot(r) is not { } taken) continue;

            shot = taken;
            double x = taken.Centre.X;
            holder = Math.Abs(bots[0].X - x) <= Math.Abs(bots[1].X - x) ? 0 : 1;
            Bot other = bots[1 - holder];
            double gap = Math.Abs(other.X - x);
            otherSpot = other.X;
            if (gap < 45 * Cell || gap > 110 * Cell)
            {
                int side = other.X >= x ? 1 : -1;
                otherSpot = x + side * 70 * Cell;
                if (otherSpot < Edge || otherSpot > ActualWidth - Edge) otherSpot = x - side * 70 * Cell;
            }
            SetPhase(Phase.Fetch);
            return;
        }
        EndGame();
    }

    // ---- Drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        if (hops is not null)
        {
            DrawTransit(dc);
            return;
        }

        bool iconOut = phase >= Phase.Grab && shot is not null;
        if (iconOut) DrawCover(dc, shot!);
        sceneUnder?.Invoke(dc);

        // Anything below the ground line is "behind" the taskbar.
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, GroundY)));
        foreach (Bot b in bots) b.Draw(dc, GroundY);
        dc.Pop();

        sceneDraw?.Invoke(dc);
        if (iconOut) DrawIcon(dc, shot!, ball, ballAngle);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
}
