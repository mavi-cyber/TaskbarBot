using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace TaskbarBot;

/// <summary>
/// The mascots in the corner of the taskbar with the arrow, the language, the network, the
/// speaker, the battery and the clock: using the icons as stepping stones and a trampoline,
/// and reacting to the real volume, language and day of the week.
/// </summary>
public sealed partial class StageView
{
    /// <summary>A photograph of a whole tray button, so it can be drawn pushed down when trodden on.</summary>
    sealed record Pad(ImageSource Whole, ImageSource Cover, Rect Slot, double ScreenWidth, double ScreenHeight)
    {
        public Point Centre => new(Slot.X + Slot.Width / 2, Slot.Y + Slot.Height / 2);
        public double Pressed { get; set; }             // 0 = at rest, 1 = fully down
    }

    Pad? Snap(Int32Rect button)
    {
        if (Taskbar.Snapshot(button, vertical: StageAngle % 180 != 0) is not { } s) return null;
        return new Pad(s.Whole, s.Cover, ToStage(button), button.Width / dpiScale, button.Height / dpiScale);
    }

    void DrawPad(DrawingContext dc, Pad pad)
    {
        if (pad.Pressed <= 0) return;
        Point m = pad.Centre;
        var upright = new RotateTransform(-StageAngle, m.X, m.Y);
        var size = new Size(pad.ScreenWidth, pad.ScreenHeight);
        dc.PushTransform(upright);
        dc.DrawImage(pad.Cover, new Rect(new Point(m.X - size.Width / 2, m.Y - size.Height / 2), size));
        dc.Pop();
        dc.PushClip(new RectangleGeometry(pad.Slot));
        dc.PushTransform(new TranslateTransform(0, 4 * pad.Pressed));          // sinks towards the taskbar's far edge
        dc.PushTransform(upright);
        dc.DrawImage(pad.Whole, new Rect(new Point(m.X - size.Width / 2, m.Y - size.Height / 2), size));
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    /// <summary>The tray's buttons in order along the taskbar, photographed.</summary>
    List<Pad> TrayPads(Taskbar.Scan scan)
    {
        var pads = new List<Pad>();
        foreach (Int32Rect button in scan.Tray)
            if (Snap(button) is { } pad && OnTaskbar(pad.Slot)) pads.Add(pad);
        pads.Sort((p, q) => p.Slot.X.CompareTo(q.Slot.X));
        return pads;
    }

    // ---- Stepping stones: hopping along the tray icons ----

    IEnumerable<int> TrayHop(Taskbar.Scan scan)
    {
        List<Pad> pads = TrayPads(scan);
        if (pads.Count < 3) yield break;
        double c = Cell;
        if (rng.Next(2) == 0) pads.Reverse();
        Nearest(pads[0].Centre.X);
        Bot a = A;
        Solo();
        foreach (var _ in Hurry(a, pads[0].Centre.X)) yield return 0;

        sceneUnder = dc => { foreach (Pad pad in pads) DrawPad(dc, pad); };
        void Settle()
        {
            foreach (Pad pad in pads) pad.Pressed = Math.Max(0, pad.Pressed - 4 * fdt);     // springs back up
        }
        double Stone(Pad pad) => -(pad.Centre.Y - GroundY) - 0.25 * pad.Slot.Height;        // feet low on the icon

        sceneOverTaskbar = true;
        BringToTop?.Invoke();
        lowBot = a;
        lowClip = null;
        foreach (var _ in Leap(a, 0, Stone(pads[0]), 4 * c)) yield return 0;
        pads[0].Pressed = 1;

        for (int i = 1; i < pads.Count; i++)
        {
            Pad from = pads[i - 1], to = pads[i];
            double x0 = a.X, lift0 = Stone(from), lift1 = Stone(to);
            foreach (var _ in Over(0.14, u => { a.Lift = lift0; a.Squash(u); Settle(); from.Pressed = 1; })) yield return 0;
            foreach (var _ in Over(0.42, u =>
            {
                a.X = x0 + (to.Centre.X - x0) * u;
                a.Lift = lift0 + (lift1 - lift0) * u + 5 * c * Arc(u);
                a.ArmsUp();
                a.Look(Math.Sign(to.Centre.X - x0) * 0.5, u < 0.5 ? -0.3 : 0.3);
                Settle();
            })) yield return 0;
            to.Pressed = 1;
        }

        // Off the last stone and back up onto the edge.
        Pad last = pads[^1];
        foreach (var _ in Over(0.5, u => { a.Lift = Stone(last); a.Look(0, -0.3); Settle(); last.Pressed = 1; })) yield return 0;
        foreach (var _ in Leap(a, Stone(last), 0, 5 * c))
        {
            Settle();
            yield return 0;
        }
        lowBot = null;
        foreach (var _ in Over(0.6, u => { Settle(); a.ArmsUp(); })) yield return 0;
        sceneOverTaskbar = false;
    }

    // ---- The arrow is a trampoline ----

    IEnumerable<int> Trampoline(Taskbar.Scan scan)
    {
        Int32Rect? button = scan.Chevron;
        if (button is null && scan.Tray.Count > 0)
        {
            // No name to go by: the arrow is the tray button nearest the app icons.
            Int32Rect firstByX = scan.Tray[0];
            foreach (Int32Rect r in scan.Tray)
                if (ToStage(r).X < ToStage(firstByX).X) firstByX = r;
            button = firstByX;
        }
        if (button is not { } arrow || Snap(arrow) is not { } pad || !OnTaskbar(pad.Slot)) yield break;
        double c = Cell, g = Physics.Gravity * c;
        Nearest(pad.Centre.X);
        Bot a = A;
        Solo();
        foreach (var _ in Hurry(a, pad.Centre.X)) yield return 0;

        sceneUnder = dc => DrawPad(dc, pad);
        double bed = -(pad.Centre.Y - GroundY) - 0.25 * pad.Slot.Height;
        sceneOverTaskbar = true;
        BringToTop?.Invoke();
        lowBot = a;
        lowClip = null;
        foreach (var _ in Leap(a, 0, bed, 4 * c)) yield return 0;

        // Each bounce goes higher than the last; the final one is a somersault back onto the edge.
        double[] heights = { 5, 9, 13 };
        for (int bounce = 0; bounce <= heights.Length; bounce++)
        {
            foreach (var _ in Over(0.16, u => { a.Lift = bed; a.Squash(Math.Sin(Math.PI * u)); pad.Pressed = Math.Sin(Math.PI * u); }))
                yield return 0;
            pad.Pressed = 0;
            if (bounce == heights.Length) break;

            bool final = bounce == heights.Length - 1;
            double target = final ? 0 : bed;
            double speed = Math.Sqrt(2 * g * (heights[bounce] * c - bed));
            double air = speed / g + Math.Sqrt(2 * (heights[bounce] * c - target) / g);
            for (double t = 0; t < air; t += fdt)
            {
                a.Lift = bed + speed * t - 0.5 * g * t * t;
                a.ArmsUp();
                if (final) a.Tilt(360 * t / air);
                yield return 0;
            }
            if (final) break;
        }
        lowBot = null;
        sceneOverTaskbar = false;
        foreach (var _ in Over(0.7, u => a.ArmsUp())) yield return 0;
    }

    // ---- Listening at the speaker ----

    IEnumerable<int> VolumeCheck(Taskbar.Scan scan)
    {
        if (scan.Volume is not { } button) yield break;
        Rect icon = ToStage(button);
        if (!OnTaskbar(icon)) yield break;
        double c = Cell, mid = icon.X + icon.Width / 2;
        Nearest(mid);
        Bot a = A;
        Solo();
        foreach (var _ in Hurry(a, InStage(mid))) yield return 0;

        // Puts an ear to it.
        foreach (var _ in Over(1.2, u => { a.Squash(0.6); a.Tilt(12); a.Look(0.5, 0.3); })) yield return 0;

        var heard = Audio.Volume();
        if (heard is null || heard.Value.Muted || heard.Value.Level < 0.03f)
        {
            // Nothing. Taps it, then gives up.
            for (int tap = 0; tap < 3; tap++)
                foreach (var _ in Over(0.35, u => { a.Say(Props.Muted); a.Lift = 2 * c * Arc(u); a.Look(0, 0.3); })) yield return 0;
            foreach (var _ in Over(1.4, u => { a.Say(Props.Question); a.Arms(-1, -1); a.Look(u < 0.5 ? -0.5 : 0.5, 0); })) yield return 0;
        }
        else if (heard.Value.Level > 0.7f)
        {
            // Far too loud: hands over the ears, staggering back.
            double x0 = a.X, back = a.X > ActualWidth / 2 ? -1 : 1;
            foreach (var _ in Over(2.4, u =>
            {
                a.Say(Props.Loud);
                a.ArmsUp();
                a.EyesClosed();
                a.Shake(frame % 2 == 0 ? 1.5 : -1.5);
                a.X = x0 + back * 8 * c * Ease(u);
            })) yield return 0;
        }
        else
            // Just right: nods along.
            foreach (var _ in Over(2.6, u =>
            {
                bool beat = (int)(u * 8) % 2 == 0;
                a.Say(Props.Note);
                a.Tilt(beat ? -5 : 5);
                a.Arms(beat ? -1 : 0, beat ? 0 : -1);
                a.Look(0, beat ? 0.2 : -0.1);
            })) yield return 0;
    }

    // ---- Trying out the language button ----

    IEnumerable<int> LanguageTry(Taskbar.Scan scan)
    {
        if (scan.Language is not { } button) yield break;
        Rect icon = ToStage(button);
        if (!OnTaskbar(icon)) yield break;
        double c = Cell, mid = icon.X + icon.Width / 2;
        Nearest(mid);
        Bot a = A;
        Solo();
        foreach (var _ in Hurry(a, InStage(mid))) yield return 0;
        foreach (var _ in Over(1.0, u => { a.Squash(0.5); a.Look(0, 0.3); })) yield return 0;

        string[] greetings = { "Hello!", "Hola!", "Bonjour!", "Salam!", "Ciao!", "Namaste!", "Konnichiwa!", "Merhaba!", "Hallo!", "Ola!" };
        string line = "";
        sceneDraw = dc => { if (line.Length > 0) Props.Speak(dc, line, Over(a, 3.5), c); };
        for (int i = 0; i < 4; i++)
        {
            line = greetings[rng.Next(greetings.Length)];
            foreach (var _ in Over(1.1, u =>
            {
                bool up = (int)(u * 6) % 2 == 0;
                a.Arms(0, up ? -2 : -1);            // a wave with each one
                a.MouthOpen();
                a.Look(0, -0.2);
            })) yield return 0;
            line = "";
            foreach (var _ in Over(0.25, u => a.Look(0, 0.3))) yield return 0;
        }
    }

    // ---- What day is it? ----

    IEnumerable<int> DateCheck(Taskbar.Scan scan)
    {
        if (scan.Clock is not { } button) yield break;
        Rect clock = ToStage(button);
        if (!OnTaskbar(clock)) yield break;
        double c = Cell, mid = clock.X + clock.Width / 2;
        Nearest(mid);
        Bot a = A;
        Solo();
        foreach (var _ in Hurry(a, InStage(mid))) yield return 0;
        foreach (var _ in Over(1.2, u => { a.Squash(0.6); a.Look(0, 0.3); })) yield return 0;

        DateTime today = DateTime.Now;
        string line = today.ToString("dddd") + "!";
        sceneDraw = dc => Props.Speak(dc, line, Over(a, 3.5), c);
        foreach (var _ in Over(1.3, u => { a.MouthOpen(); a.Look(0, -0.2); })) yield return 0;

        // And how it feels about that.
        switch (today.DayOfWeek)
        {
            case DayOfWeek.Saturday or DayOfWeek.Sunday:
                foreach (var _ in Over(2.2, u => { a.Hold(Props.Party, 6, 6); a.ArmsUp(); a.Lift = 3 * c * Arc(u * 4 % 1); })) yield return 0;
                break;
            case DayOfWeek.Friday:
                foreach (var _ in Over(2.2, u =>
                {
                    bool ph = (int)(u * 8) % 2 == 0;
                    a.Arms(ph ? -2 : 2, ph ? 2 : -2);
                    a.Tilt(ph ? -6 : 6);
                })) yield return 0;
                break;
            case DayOfWeek.Monday:
                foreach (var _ in Over(2.4, u => { a.Sit(); a.EyesClosed(); a.Arms(1, 1); a.Squash(0.3); })) yield return 0;
                break;
            default:
                foreach (var _ in Over(1.6, u => a.Look(0, (int)(u * 6) % 2 == 0 ? 0.2 : -0.1))) yield return 0;
                break;
        }
    }
}
