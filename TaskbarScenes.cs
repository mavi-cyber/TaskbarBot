using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace TaskbarBot;

/// <summary>
/// The mascots dealing with the real taskbar: climbing down into it, the search box, the clock,
/// the battery and network icons, newly opened apps, and whatever music is playing.
/// What they do here depends on the PC's actual state (time, charge, connection, sound).
/// </summary>
public sealed partial class StageView
{
    // A bot that has climbed down into the taskbar is drawn over it (optionally cut off by lowClip)
    // instead of being hidden below the ground line like everyone else.
    Bot? lowBot;
    Geometry? lowClip;

    /// <summary>Thickness of the taskbar under the bots' feet.</summary>
    double BarDepth => ActualHeight - GroundY;

    bool InBar(Rect slot) => slot.Top >= GroundY - 2 && slot.Bottom <= ActualHeight + 2;

    /// <summary>For scenes with a single actor: the partner goes back to its own business.</summary>
    void Solo() => B.External = false;

    /// <summary>
    /// A jump under gravity from one height to another, peaking apex above the higher of the two.
    /// Heights are lifts: 0 is the taskbar's top edge, negative is down inside the taskbar.
    /// </summary>
    IEnumerable<int> Leap(Bot bot, double from, double to, double apex)
    {
        double g = Physics.Gravity * Cell;
        double speed = Math.Sqrt(2 * g * (Math.Max(from, to) + apex - from));
        for (double t = 0; ; t += fdt)
        {
            double lift = from + speed * t - 0.5 * g * t * t;
            if (lift <= to && t > speed / g) break;
            bot.Lift = lift;
            bot.ArmsUp();
            bot.Look(0, t < speed / g ? -0.3 : 0.3);
            yield return 0;
        }
        bot.Lift = to;
    }

    // ---- Into the search box ----

    IEnumerable<int> SearchDive(Taskbar.Scan scan)
    {
        if (scan.Search is not { } button) yield break;
        Rect box = ToStage(button);
        double c = Cell;
        if (!InBar(box) || box.Width < 30 * c) yield break;         // search is hidden or just an icon
        Nearest(box.X + box.Width / 2);
        Bot a = A;
        Solo();

        double near = box.X + 8 * c, far = box.Right - 8 * c;
        if (Math.Abs(a.X - far) < Math.Abs(a.X - near)) (near, far) = (far, near);
        foreach (var _ in Hurry(a, near)) yield return 0;

        // In it goes, waist-deep: the box's bottom edge cuts it off.
        double floor = -(box.Bottom - GroundY) - 3.5 * c;
        sceneOverTaskbar = true;
        BringToTop?.Invoke();
        lowBot = a;
        lowClip = new RectangleGeometry(new Rect(box.X, 0, box.Width, box.Bottom));
        foreach (var _ in Leap(a, 0, floor, 8 * c)) yield return 0;

        foreach (var _ in Over(0.5, u => { a.Lift = floor - 2 * c * Math.Sin(Math.PI * u) * (1 - u); a.EyesClosed(); }))
            yield return 0;
        foreach (var _ in Over(1.6, u => { a.Lift = floor; a.Look(u < 0.5 ? -0.5 : 0.5, 0); })) yield return 0;

        // Wades to the other end.
        double wade = 0;
        while (!a.WalkToward(far, fdt, 9, false))
        {
            wade += fdt;
            a.Lift = floor + 0.5 * c * Math.Sin(wade * 7);
            a.Arms((int)(wade * 3) % 2 == 0 ? -1 : 0, (int)(wade * 3) % 2 == 0 ? 0 : -1);
            yield return 0;
        }
        foreach (var _ in Over(1.0, u =>
        {
            bool up = (int)(u * 6) % 2 == 0;
            a.Lift = floor;
            a.Arms(up ? -2 : -1, up ? -1 : -2);         // waves from inside the box
            a.Look(0, -0.3);
        })) yield return 0;

        foreach (var _ in Leap(a, floor, 0, 6 * c)) yield return 0;
        lowBot = null;
        sceneOverTaskbar = false;
        foreach (var _ in Over(0.5, u => a.Look(0, 0.3))) yield return 0;
    }

    // ---- A walk inside the taskbar, in front of the icons ----

    IEnumerable<int> Stroll()
    {
        double c = Cell, depth = BarDepth;
        if (depth < 4 * c) yield break;                              // no taskbar to climb into
        sa = rng.Next(2);
        Bot a = A;
        Solo();

        double floor = -(depth - 2);
        sceneOverTaskbar = true;
        BringToTop?.Invoke();
        lowBot = a;
        lowClip = null;
        foreach (var _ in Leap(a, 0, floor, 3 * c)) yield return 0;

        int legs = 2 + rng.Next(2);
        for (int leg = 0; leg < legs; leg++)
        {
            double target = InStage(a.X + (rng.Next(2) == 0 ? -1 : 1) * (20 + rng.NextDouble() * 40) * c);
            while (!a.WalkToward(target, fdt, 9, false))
            {
                a.Lift = floor;
                yield return 0;
            }
            // Stops to look at whatever is around it.
            foreach (var _ in Over(0.9 + rng.NextDouble(), u => { a.Lift = floor; a.Look(u < 0.5 ? -0.5 : 0.5, -0.2); }))
                yield return 0;
        }

        foreach (var _ in Leap(a, floor, 0, 4 * c)) yield return 0;
        lowBot = null;
        sceneOverTaskbar = false;
    }

    // ---- Checking the time ----

    IEnumerable<int> ClockCheck(Taskbar.Scan scan)
    {
        if (scan.Clock is not { } button) yield break;
        Rect clock = ToStage(button);
        if (!OnTaskbar(clock)) yield break;
        double c = Cell, mid = clock.X + clock.Width / 2;
        Nearest(mid);
        Bot a = A;
        Solo();
        foreach (var _ in Hurry(a, InStage(mid))) yield return 0;

        // Bends down to read it.
        foreach (var _ in Over(1.4, u => { a.Squash(0.6 * Math.Sin(Math.PI * Math.Min(1, u * 2) / 2)); a.Look(0, 0.3); }))
            yield return 0;

        // What it makes of the time depends on what the time really is.
        int hour = DateTime.Now.Hour;
        if (hour >= 23 || hour < 6)
            foreach (var _ in Over(2.4, u =>
            {
                double open = Math.Sin(Math.PI * u);
                a.Say(Props.Zzz);
                a.EyesClosed();
                if (open > 0.3) a.MouthOpen();
                if (open > 0.5) a.ArmsUp();
                a.Stretch(0.6 * open);
            })) yield return 0;
        else
        {
            string thought = hour < 10 ? Props.Coffee : hour is >= 12 and < 14 ? Props.Pizza : hour >= 18 ? Props.Moon : Props.Sun;
            foreach (var _ in Over(2.2, u =>
            {
                a.Say(thought);
                a.Look(0, -0.3);
                if (thought == Props.Pizza || thought == Props.Coffee) a.Lift = 2 * c * Arc(u * 3 % 1);
            })) yield return 0;
        }
    }

    // ---- Looking after the battery ----

    IEnumerable<int> BatteryCare(Taskbar.Scan scan)
    {
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        if (scan.Battery is not { } button
            || power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)) yield break;
        Rect icon = ToStage(button);
        if (!OnTaskbar(icon)) yield break;
        double c = Cell, mid = icon.X + icon.Width / 2;
        Nearest(mid);
        Bot a = A;
        Solo();
        foreach (var _ in Hurry(a, InStage(mid))) yield return 0;
        foreach (var _ in Over(1.1, u => { a.Squash(0.5); a.Look(0, 0.3); })) yield return 0;

        bool charging = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        if (charging)
            foreach (var _ in Over(2.2, u => { a.Say(Props.Plug); a.ArmsUp(); a.Lift = 2 * c * Arc(u * 3 % 1); })) yield return 0;
        else if (power.BatteryLifePercent < 0.3f)
        {
            // Worried, then tries to charge it by sheer effort.
            foreach (var _ in Over(1.4, u => { a.Say(Props.LowBattery); a.Look(u < 0.5 ? -0.5 : 0.5, 0); })) yield return 0;
            foreach (var _ in Over(3.2, u =>
            {
                bool up = (int)(u * 13) % 2 == 0;
                a.Say(Props.Zap);
                a.Arms(up ? -2 : 1, up ? -2 : 1);
                a.Lift = 2 * c * Arc(u * 6.5 % 1);
            })) yield return 0;
            foreach (var _ in Over(1.4, u => { a.Sit(); a.Squash(0.25 + 0.2 * Math.Sin(u * 28)); a.EyesClosed(); })) yield return 0;
        }
        else
            foreach (var _ in Over(1.8, u => { a.Say(Props.Battery); a.Look(0, (int)(u * 6) % 2 == 0 ? 0.2 : -0.1); })) yield return 0;
    }

    // ---- The network dropped ----

    IEnumerable<int> WifiFix(Taskbar.Scan scan)
    {
        if (scan.Network is not { } button) yield break;
        Rect icon = ToStage(button);
        if (!OnTaskbar(icon)) yield break;
        double c = Cell, mid = icon.X + icon.Width / 2;
        int sd = mid > ActualWidth / 2 ? 1 : -1;                    // room for the helper is on this side
        Nearest(mid);
        Bot a = A, b = B;
        foreach (var _ in WalkBoth(InStage(mid), InStage(mid) - sd * 13 * c, 40)) yield return 0;

        foreach (var _ in Over(1.3, u => { a.Say(Props.Question); a.Look(0, 0.3); b.Look(sd * 0.5, 0.2); })) yield return 0;

        // Percussive maintenance.
        for (int thump = 0; thump < 3; thump++)
            foreach (var _ in Over(0.5, u =>
            {
                double p = Math.Sin(Math.PI * u);
                a.Lift = 3 * c * p;
                a.Squash(u > 0.85 ? 1 : 0);
                a.Look(0, 0.3);
                b.Look(sd * 0.5, 0);
            })) yield return 0;

        bool back = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        foreach (var _ in Over(2.0, u =>
        {
            if (back)
            {
                a.Say(Props.Signal);
                a.ArmsUp();
                b.ArmsUp();
                a.Lift = 2 * c * Arc(u * 3 % 1);
                b.Lift = 2 * c * Arc((u * 3 + 0.5) % 1);
            }
            else
            {
                a.Say(Props.Question);
                a.Look(-sd * 0.5, 0);
                b.Look(sd * 0.5, 0);
                b.Arms(-1, -1);                 // a shrug
            }
        })) yield return 0;
    }

    // ---- A new app appeared on the taskbar ----

    Int32Rect newAppButton;

    IEnumerable<int> NewApp()
    {
        Rect slot = ToStage(newAppButton);
        if (!OnTaskbar(slot)) yield break;
        double c = Cell, mid = slot.X + slot.Width / 2;
        Nearest(mid);
        Bot a = A, b = B;
        foreach (var _ in WalkBoth(InStage(mid - 7 * c), InStage(mid + 7 * c), 34)) yield return 0;

        foreach (var _ in Over(0.9, u => { a.Look(0.5, 0.3); b.Look(-0.5, 0.3); a.Say(Props.Question); })) yield return 0;
        foreach (var _ in Over(1.6, u =>
        {
            a.Tilt(12 * Math.Abs(Math.Sin(u * 9)));         // leans in for a closer look
            a.Look(0.5, 0.3);
            b.Look(-0.5, 0.3);
            b.Say(Props.Eyes);
        })) yield return 0;
        foreach (var _ in Over(0.9, u => { a.Look(0.5, (int)(u * 4) % 2 == 0 ? 0.2 : 0); b.Look(-0.5, 0); })) yield return 0;
    }

    // ---- Keeping an eye on the PC ----

    Taskbar.Scan? lastSurvey;
    bool surveying, online = true, lowWarned;
    double surveyIn = 6, listenIn, soundFor, danceIn, musicFor, partyCooldown, volumeCooldown;
    (float Level, bool Muted)? lastVolume;
    string? eventScene;                 // a scene some real event has asked for

    /// <summary>Called every frame: watches the taskbar's apps, the network, the battery and the speakers.</summary>
    void Watch(double dt)
    {
        // Music: while sound is playing, the free ones dance to it.
        listenIn -= dt;
        if (listenIn <= 0)
        {
            listenIn = 0.25;
            soundFor = Audio.Peak() > 0.02 ? Math.Min(6, soundFor + 0.25) : Math.Max(0, soundFor - 0.5);
            bool music = soundFor > 2;

            // The volume was turned up or down a lot, or muted: someone goes to listen at the speaker.
            volumeCooldown -= 0.25;
            var set = Audio.Volume();
            if (set is { } nowSet && lastVolume is { } was && volumeCooldown <= 0
                && (nowSet.Muted != was.Muted || Math.Abs(nowSet.Level - was.Level) > 0.2f))
            {
                volumeCooldown = 20;
                eventScene ??= "volume";
            }
            if (volumeCooldown <= 0 || lastVolume is null) lastVolume = set;

            // Music that keeps going turns into a party, now and then.
            musicFor = music ? musicFor + 0.25 : 0;
            partyCooldown -= 0.25;
            if (musicFor > 25 && partyCooldown <= 0)
            {
                partyCooldown = 600;
                eventScene ??= "party";
            }
            foreach (Bot b in cast) b.HearsMusic = music;
            danceIn -= 0.25;
            if (music && danceIn <= 0)
            {
                danceIn = 4 + rng.NextDouble() * 5;
                for (int i = 0; i < 2; i++) cast[rng.Next(cast.Length)].Queue(Move.Dance, rng.NextDouble());
            }
        }

        surveyIn -= dt;
        if (surveyIn > 0 || surveying || pendingScan || scene is not null || phase != Phase.None) return;
        surveyIn = 15;

        // Network: gone, they go and thump the icon; back, they cheer.
        bool connected = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        if (!connected && online) eventScene = "wifi";
        else if (connected && !online)
            foreach (Bot b in cast) b.Queue(Move.Jump, rng.NextDouble() * 1.5);
        online = connected;

        // Battery: the first time it gets low on this discharge, someone goes to look.
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        bool low = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline && power.BatteryLifePercent < 0.2f;
        if (low && !lowWarned) eventScene = "battery";
        lowWarned = low;

        // Apps: compare the taskbar's buttons with what was there a moment ago.
        surveying = true;
        Task.Run(Taskbar.Read).ContinueWith(task => Dispatcher.BeginInvoke(() =>
        {
            surveying = false;
            if (task.Result is not { } now) return;
            if (lastSurvey is { } before)
            {
                if (now.Apps.Count == before.Apps.Count + 1)
                {
                    // The newcomer is the last button; one opened in the middle shifts the rest along.
                    newAppButton = now.Apps[^1];
                    eventScene ??= "newapp";
                }
                else if (now.Apps.Count < before.Apps.Count)
                    cast[rng.Next(cast.Length)].Queue(Move.Wave, 0.4);       // goodbye to the one that closed
            }
            lastSurvey = now;
        }));
    }
}
