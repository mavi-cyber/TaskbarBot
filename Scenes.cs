using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace TaskbarBot;

/// <summary>
/// Scripted two-bot scenes and the director that decides when one starts. A scene is an iterator:
/// every "yield return" is one frame. While a scene runs both bots are External, so the script
/// (and the two stance callbacks, for poses that should simply persist) poses them each frame.
/// </summary>
public sealed partial class StageView
{
    public static readonly (string Name, string Title)[] Scenes =
    {
        ("catch", "Play catch with a taskbar icon"), ("start", "Steal the Start button"),
        ("fish", "Fish for trash"), ("laser", "Chase the mouse"), ("push", "Lift the window"),
        ("graffiti", "Graffiti on the clock"), ("bowl", "Icon bowling"), ("moon", "Taunt"),
        ("shove", "Shove an icon"), ("scare", "Sneak-up scare"), ("tower", "Piggyback tower"),
        ("tag", "Tag"), ("faces", "Pull faces"), ("chat", "Have a chat"),
        ("search", "Dive into the search box"), ("stroll", "Stroll inside the taskbar"), ("clock", "Check the time"),
        ("battery", "Check the battery"), ("wifi", "Fix the network"),
        ("mess", "Wreck the pinned icons"), ("fight", "Fight"), ("party", "Party"), ("football", "Football"),
        ("race", "Race"), ("hide", "Hide and seek"), ("dinner", "Dinner for two"),
        ("trayhop", "Hop along the tray icons"), ("trampoline", "Bounce on the tray arrow"), ("volume", "Listen at the speaker"),
        ("language", "Try the language button"), ("date", "What day is it?"),
        ("traycatch", "Play catch with a tray icon"), ("trayball", "Football with a tray icon"),
    };

    IEnumerator<int>? scene;
    Action<DrawingContext>? sceneUnder, sceneDraw;      // drawn below / above the bots
    Action? stanceA, stanceB;
    bool sceneOverTaskbar;                              // something of the scene covers the taskbar
    int sa, sceneFrames;

    Bot A => bots[sa];
    Bot B => bots[1 - sa];

    // Director state.
    string? wanted, lastAmbient;
    bool pendingScan, wasDown, stoleThisIdle, forced;     // forced = asked for from the tray menu
    double now, nextAmbient, laserHeat, laserCooldown, shoveCooldown, hoverDwell;
    int graffitiHour = -1;
    double dwellX, moonX;                               // along the taskbar, stage coordinates
    readonly List<double> clicks = new();

    /// <summary>Plays the named scene as soon as it can (taskbar scenes wait for the mouse to move away).</summary>
    public void PlayScene(string name)
    {
        foreach (Bot b in cast) b.CancelMove();
        wanted = name;
    }

    void WatchMouse(double dt)
    {
        now += dt;
        laserCooldown -= dt;
        shoveCooldown -= dt;

        // Fast sideways movement in the lower half of the screen heats up the "laser pointer".
        double depth = StageAngle % 180 == 0 ? SystemParameters.PrimaryScreenHeight : SystemParameters.PrimaryScreenWidth;
        bool low = GroundY - cursor.Y < depth / 2 && !CursorOnTaskbar;
        laserHeat = low && Math.Abs(cursorVx) > 1300 ? laserHeat + dt : Math.Max(0, laserHeat - 2 * dt);

        bool down = Taskbar.LeftButtonDown();
        if (down && !wasDown)
        {
            hoverDwell = 0;
            if (CursorOnTaskbar)
            {
                clicks.Add(now);
                moonX = cursor.X;
            }
        }
        wasDown = down;
        clicks.RemoveAll(t => now - t > 1.2);

        if (CursorOnTaskbar && Math.Abs(cursor.X - dwellX) <= 3 && !down) hoverDwell += dt;
        else
        {
            hoverDwell = 0;
            dwellX = cursor.X;
        }
    }

    /// <summary>Runs when nothing is playing: decides whether a scene should start now.</summary>
    void Direct(double dt)
    {
        if (pendingScan) return;

        double idle = Taskbar.IdleSeconds();
        if (idle < 5) stoleThisIdle = false;
        DateTime clock = DateTime.Now;

        string? pick = wanted;
        if (pick is null)
        {
            if (clicks.Count >= 3) { clicks.Clear(); pick = "moon"; }
            else if (hoverDwell >= 4 && shoveCooldown <= 0) pick = "shove";
            else if (laserHeat > 0.45 && laserCooldown <= 0) pick = "laser";
            else if (eventScene is not null) { pick = eventScene; eventScene = null; }
            else if (idle >= 120 && !stoleThisIdle) pick = "start";
            else if (clock.Minute == 0 && clock.Hour != graffitiHour) pick = "graffiti";
            else
            {
                nextAmbient -= dt;
                if (nextAmbient <= 0 && Array.FindAll(cast, b => b.IsIdle).Length >= 2) pick = PickAmbient();
            }
        }
        if (pick is null) return;

        // Scenes that cover part of the taskbar never start with the mouse on its way there.
        if (pick is "catch" or "start" or "graffiti" or "bowl" or "search" or "stroll" or "mess" or "trayhop" or "trampoline" or "traycatch" or "trayball" && CursorThreatens()) return;

        forced = wanted is not null;
        wanted = null;
        nextAmbient = 35 + rng.NextDouble() * 55;
        switch (pick)
        {
            case "start": stoleThisIdle = true; break;
            case "graffiti": if (clock.Minute == 0) graffitiHour = clock.Hour; break;
            case "laser": laserCooldown = 120; laserHeat = 0; break;
            case "shove": shoveCooldown = 60; hoverDwell = 0; break;
        }

        if (pick is "catch" or "traycatch") BeginCatch(tray: pick == "traycatch");
        else if (pick is "start" or "graffiti" or "bowl" or "moon" or "shove" or "search" or "clock" or "battery" or "wifi" or "mess"
                 or "trayhop" or "trampoline" or "volume" or "language" or "date" or "trayball")
        {
            pendingScan = true;
            string name = pick;
            Task.Run(Taskbar.Read).ContinueWith(task => Dispatcher.BeginInvoke(() =>
            {
                pendingScan = false;
                if (task.Result is { } scan && scene is null && phase == Phase.None) Launch(name, scan);
            }));
        }
        else Launch(pick, null);
    }

    string PickAmbient()
    {
        var pool = new List<(string Name, int Weight)>
        {
            ("catch", 3), ("fish", 2), ("graffiti", 1), ("bowl", 2),
            ("scare", 2), ("tower", 2), ("tag", 2), ("faces", 2), ("chat", 4),
        };
        if (WindowOnTaskbar() is not null) pool.Add(("push", 2));
        pool.Add(("search", 3));
        pool.Add(("stroll", 3));
        pool.Add(("clock", 2));
        pool.Add(("mess", 3));
        pool.Add(("fight", 2));
        pool.Add(("football", 2));
        pool.Add(("race", 2));
        pool.Add(("hide", 2));
        pool.Add(("dinner", 2));
        pool.Add(("party", 1));
        pool.Add(("trayhop", 3));
        pool.Add(("traycatch", 3));
        pool.Add(("trayball", 3));
        pool.Add(("trampoline", 2));
        pool.Add(("volume", 2));
        pool.Add(("language", 1));
        pool.Add(("date", 1));
        if (lastSurvey?.Battery is not null) pool.Add(("battery", 1));
        pool.RemoveAll(p => p.Name == lastAmbient);

        int total = 0;
        foreach (var p in pool) total += p.Weight;
        int roll = rng.Next(total), i = 0;
        while (roll >= pool[i].Weight) roll -= pool[i++].Weight;
        return lastAmbient = pool[i].Name;
    }

    /// <summary>Stage x of the bottom centre of the active window, if it rests on the taskbar.</summary>
    double? WindowOnTaskbar()
    {
        if (Taskbar.ForegroundBounds() is not { } r) return null;
        Rect window = ToStage(r);
        return Math.Abs(window.Bottom - GroundY) <= 2 ? window.X + window.Width / 2 : null;
    }

    void Launch(string name, Taskbar.Scan? scan)
    {
        IEnumerable<int>? script = name switch
        {
            "start" when scan is not null => StartSteal(scan),
            "search" when scan is not null => SearchDive(scan),
            "clock" when scan is not null => ClockCheck(scan),
            "battery" when scan is not null => BatteryCare(scan),
            "wifi" when scan is not null => WifiFix(scan),
            "stroll" => Stroll(),
            "newapp" => NewApp(),
            "mess" when scan is not null => Mess(scan),
            "trayhop" when scan is not null => TrayHop(scan),
            "trampoline" when scan is not null => Trampoline(scan),
            "volume" when scan is not null => VolumeCheck(scan),
            "language" when scan is not null => LanguageTry(scan),
            "date" when scan is not null => DateCheck(scan),
            "fight" => Fight(),
            "party" => Party(),
            "football" => Football(null),
            "trayball" when scan is not null => Football(scan),
            "race" => Race(),
            "hide" => HideSeek(),
            "dinner" => Dinner(),
            "graffiti" when scan is not null => Graffiti(scan),
            "bowl" when scan is not null => Bowling(scan),
            "moon" when scan is not null => Mooning(scan),
            "shove" when scan is not null => Shove(scan),
            "fish" => Fishing(),
            "laser" => Laser(),
            "push" => PushWindow(),
            "scare" => Scare(),
            "tower" => Tower(),
            "tag" => TagGame(),
            "faces" => Faces(),
            "chat" => Chat(),
            _ => null,
        };
        if (script is null) return;

        PickDuo(name == "laser" ? "Mochi" : null);      // the cat is the one who chases the dot
        foreach (Bot b in bots) b.External = true;
        sa = 0;
        sceneFrames = 0;
        sceneOverTaskbar = false;
        scene = script.GetEnumerator();
    }

    void StepScene()
    {
        sceneFrames++;
        stanceA?.Invoke();
        stanceB?.Invoke();
        if (sceneOverTaskbar && (CursorOnTaskbar || CursorThreatens())) { EndScene(); return; }
        if (!scene!.MoveNext()) EndScene();
    }

    void EndScene()
    {
        scene?.Dispose();
        scene = null;
        sceneUnder = sceneDraw = null;
        stanceA = stanceB = null;
        sceneOverTaskbar = false;
        lowBot = null;
        lowClip = null;
        foreach (Bot b in cast) b.External = false;      // a party borrows the whole cast
        if (sceneFrames < 3) nextAmbient = 8;       // it could not run; try something else soon
        InvalidateVisual();
    }

    // ---- Script building blocks ----

    /// <summary>Runs pose(0..1) every frame for the given time.</summary>
    IEnumerable<int> Over(double seconds, Action<double> pose)
    {
        for (double t = 0; t < seconds; t += fdt)
        {
            pose(t / seconds);
            yield return 0;
        }
    }

    IEnumerable<int> Walk(Bot bot, double x, double speed = 22)
    {
        while (!bot.WalkToward(x, fdt, speed, false)) yield return 0;
    }

    /// <summary>Goes to x: a walk if it is close, a run if it is a long way off.</summary>
    IEnumerable<int> Hurry(Bot bot, double x)
    {
        while (true)
        {
            int way = Math.Sign(x - bot.X);
            bool far = Math.Abs(x - bot.X) > 50 * Cell;
            if (bot.WalkToward(x, fdt, far ? 80 : 30, false)) yield break;
            if (far)
            {
                bot.Ghosts(1, way);
                bot.Tilt(way * 10);
            }
            yield return 0;
        }
    }

    IEnumerable<int> WalkBoth(double ax, double bx, double speed = 22)
    {
        while (true)
        {
            bool p = A.WalkToward(ax, fdt, speed, false), q = B.WalkToward(bx, fdt, speed, false);
            if (p && q) yield break;
            yield return 0;
        }
    }

    /// <summary>
    /// Makes A whoever is closest to x. A free bystander who is closer than both actors takes
    /// the lead role over, so the one who reacts to something is the one standing next to it.
    /// </summary>
    void Nearest(double x)
    {
        Bot best = bots[0];
        foreach (Bot b in cast)
            if ((b.IsIdle || b == bots[1]) && Math.Abs(b.X - x) < Math.Abs(best.X - x)) best = b;
        if (best == bots[1]) { sa = 1; return; }
        if (best != bots[0])
        {
            bots[0].External = false;
            bots[0] = best;
            best.External = true;
        }
        sa = 0;
    }

    double InStage(double x) => Math.Clamp(x, Edge, Math.Max(Edge, ActualWidth - Edge));

    Point Over(Bot bot, double cellsAboveHead) =>
        new(bot.X, GroundY - bot.Lift - bot.Height - cellsAboveHead * Cell);

    Geometry AboveGround => new RectangleGeometry(new Rect(0, 0, ActualWidth, GroundY));

    /// <summary>Flat on its side, out cold.</summary>
    void Lie(Bot bot, int side)
    {
        bot.Tilt(side * 90);
        bot.Lift = 2 * Cell;
        bot.EyesClosed();
    }

    IEnumerable<int> GetUp(Bot bot, int side) =>
        Over(0.3, u => { bot.Tilt(side * 90 * (1 - u)); bot.Lift = 2 * Cell * (1 - u); });

    // ---- 1. Stealing the Start button ----

    IEnumerable<int> StartSteal(Taskbar.Scan scan)
    {
        if (scan.Start is not { } button || Shoot(button) is not { } start || !OnTaskbar(start.Slot)) yield break;
        double c = Cell;
        int sd = start.Slot.Right + 40 * c < ActualWidth ? 1 : -1;      // the side with room to stand on
        double edge = sd > 0 ? start.Slot.Right : start.Slot.Left;
        Nearest(edge);
        Bot a = A, b = B;
        double standA = edge + sd * 8 * c, standB = standA + sd * 20 * c, watch = 0;

        // B keeps watch, eyes darting left and right.
        stanceB = () => { watch += fdt; b.Look((int)(watch * 5) % 2 == 0 ? -0.5 : 0.5, 0); };
        foreach (var _ in WalkBoth(standA + sd * 25 * c, standB + sd * 25 * c)) yield return 0;
        foreach (var _ in Walk(a, standA, 6)) yield return 0;

        double bar = -35;
        bool crowbar = true, pried = false;
        Point icon = start.Centre, pivot = new(edge - sd * 2, GroundY);
        sceneOverTaskbar = true;
        BringToTop?.Invoke();
        sceneUnder = dc => { if (pried) DrawCover(dc, start); };
        sceneDraw = dc =>
        {
            if (crowbar) Props.Crowbar(dc, pivot, bar, c, sd);
            if (pried) DrawIcon(dc, start, icon);
        };

        for (int heave = 0; heave < 3; heave++)
            foreach (var _ in Over(0.6, u =>
            {
                double p = Math.Sin(Math.PI * u);
                bar = -35 + 24 * p;
                a.Tilt(-sd * 10 * p);
                a.Squash(0.4 * p);
                a.Arms(sd > 0 ? 1 : 0, sd > 0 ? 0 : 1);
                a.Look(-sd * 0.5, 0.25);
            })) yield return 0;

        // It comes loose.
        pried = true;
        crowbar = false;
        foreach (var _ in Over(0.3, u => { icon = new(start.Centre.X, start.Centre.Y - 10 * Arc(u)); a.ArmsUp(); }))
            yield return 0;

        double drag = 50 / dpiScale;
        foreach (var _ in Over(1.8, u =>
        {
            icon = new(start.Centre.X + sd * drag * u, start.Centre.Y);
            a.X = standA + sd * drag * u;
            a.Tilt(sd * 14);
            a.StepInPlace(fdt);
            a.Arms(sd > 0 ? 1 : 0, sd > 0 ? 0 : 1);
            a.Look(sd * 0.5, 0);
        })) yield return 0;

        // Trips; the button snaps back.
        stanceB = null;
        foreach (var _ in Over(0.3, u =>
        {
            a.Tilt(sd * (14 + 76 * u));
            a.Lift = 2 * c * u;
            icon = new(start.Centre.X + sd * drag * (1 - Ease(u)), start.Centre.Y);
            b.LookAt(Over(a, 0), GroundY);
        })) yield return 0;
        pried = false;
        sceneOverTaskbar = false;

        foreach (var _ in Over(1.3, u => { Lie(a, sd); b.LookAt(Over(a, 0), GroundY); })) yield return 0;
        foreach (var _ in GetUp(a, sd)) yield return 0;
    }

    // ---- 2. Fishing for trash behind the taskbar ----

    IEnumerable<int> Fishing()
    {
        double c = Cell, w = ActualWidth, g = Physics.Gravity * c, size = 4.6 * c;
        sa = rng.Next(2);
        Bot a = A, b = B;
        int side = a.X < w / 2 ? 1 : -1;                 // cast towards the roomier side
        double tipX = a.X + side * 9 * c, tipY = GroundY - 11 * c, kick = 0;
        string[] junk = { Props.Can, Props.Bone, Props.Fish, Props.Boot, Props.Paper, Props.Banana };
        int casts = Taskbar.RecycleBinItems() > 0 ? 3 + rng.Next(3) : 1;

        string? hooked = null;                           // on the line
        Point hookAt = default;
        Body? loose = null;                              // thrown: in the air or bouncing
        string looseEmoji = "";
        bool bite = false, bag = false;
        var floor = new List<(string Emoji, double X, double Angle)>();
        var rod = new Pen(Props.Wood, 0.6 * c);
        var line = new Pen(Props.Line, 1);

        sceneDraw = dc =>
        {
            dc.PushClip(AboveGround);
            var tip = new Point(tipX, tipY + kick);
            dc.DrawLine(rod, new Point(a.X + side * 5 * c, GroundY - 5 * c), tip);
            dc.DrawLine(line, tip, hooked is not null ? hookAt : new Point(tip.X, GroundY));
            foreach (var (emoji, x, angle) in floor) Props.Draw(dc, emoji, new Point(x, GroundY - size / 2), size, angle);
            if (hooked is not null) Props.Draw(dc, hooked, hookAt, size);
            if (loose is not null) Props.Draw(dc, looseEmoji, loose.Pos, size, loose.Angle);
            if (bite) Props.Draw(dc, Props.Bang, Over(a, 3), 3 * c);
            if (bag) Props.Draw(dc, Props.Bin, b.HoldPoint(GroundY, 6 * c), 6 * c);
            dc.Pop();
        };
        stanceA = () => { a.Sit(); a.Arms(side < 0 ? -1 : 0, side > 0 ? -1 : 0); a.Look(side * 0.5, 0.2); };
        if (Math.Abs(b.X - a.X) < 20 * c)
            foreach (var _ in Walk(b, InStage(a.X - side * 24 * c))) yield return 0;
        stanceB = () => { if (bag) b.ArmsUp(); b.LookAt(new Point(tipX, tipY), GroundY); };

        for (int cast = 0; cast < casts; cast++)
        {
            foreach (var _ in Over(1.2 + rng.NextDouble() * 1.6, u => kick = Math.Sin(u * 20) * 0.3 * c)) yield return 0;
            bite = true;
            foreach (var _ in Over(0.35, u => kick = 1.5 * c * Math.Sin(u * 25))) yield return 0;
            bite = false;
            kick = 0;

            // Reel it up from behind the taskbar. The catch hangs on the line as a pendulum:
            // angular acceleration = -(g / length) * sin(angle), minus a little air drag,
            // so it swings faster as the line gets shorter.
            hooked = junk[rng.Next(junk.Length)];
            double length = GroundY + 3 * c - tipY, swing = 0, swingSpeed = side * 2.4;
            while (length > 2.5 * c)
            {
                length -= 9 * c * fdt;
                swingSpeed += (-(g / length) * Math.Sin(swing) - 0.8 * swingSpeed) * fdt;
                swing += swingSpeed * fdt;
                hookAt = new(tipX + length * Math.Sin(swing), tipY + length * Math.Cos(swing));
                a.ArmsUp();
                yield return 0;
            }

            // Toss it over the shoulder. From here it is a free body: gravity, bounces, friction.
            // B scrambles to get the bin under it before it lands.
            double landX = InStage(a.X - side * (18 + rng.NextDouble() * 30) * c);
            var toss = Throw.Between(hookAt, new Point(landX, GroundY - size / 2), 9 * c, g);
            loose = new Body { Pos = hookAt, Vel = toss.Velocity, Radius = size / 2, Spin = -side * 500 };
            looseEmoji = hooked;
            hooked = null;
            bag = true;
            bool caught = false;
            for (double air = 0; air < 5 && !caught && !loose.Resting; air += fdt)
            {
                loose.Step(fdt, g, GroundY, Physics.TrashBounce, Physics.Friction);
                if (loose.Pos.X < Edge || loose.Pos.X > w - Edge)       // the screen edge is a wall
                {
                    loose.Pos = new Point(InStage(loose.Pos.X), loose.Pos.Y);
                    loose.Vel = new Vector(-loose.Vel.X * 0.5, loose.Vel.Y);
                }
                b.WalkToward(loose.Bounced ? loose.Pos.X : landX, fdt, 38, false);
                Point bin = b.HoldPoint(GroundY, 6 * c);
                caught = loose.Vel.Y > 0 && Math.Abs(loose.Pos.X - bin.X) < 3.5 * c && Math.Abs(loose.Pos.Y - bin.Y) < 3 * c;
                yield return 0;
            }

            if (caught)
            {
                loose = null;
                foreach (var _ in Over(0.4, u => b.Squash(1 - u))) yield return 0;
            }
            else
            {
                floor.Add((looseEmoji, loose.Pos.X, loose.Angle));
                loose = null;
                foreach (var _ in Over(0.6, u => b.Look(0, 0.3))) yield return 0;
            }
        }

        // Tidy up what hit the floor.
        stanceA = null;
        while (floor.Count > 0)
        {
            foreach (var _ in Walk(b, floor[0].X, 30)) yield return 0;
            floor.RemoveAt(0);
            foreach (var _ in Over(0.25, u => b.Squash(1 - u))) yield return 0;
        }
        foreach (var _ in Over(0.5, u => { })) yield return 0;
    }

    // ---- 3. The mouse is a laser pointer ----

    IEnumerable<int> Laser()
    {
        double c = Cell;
        if (bots[0].Skin.Name == "Mochi") sa = 0;
        else Nearest(cursor.X);
        Bot a = A, b = B;
        double total = 0, still = 0, pounce = 0, tumble = -1;
        int side = Math.Sign(a.X - b.X), knock = 1;

        while (total < 9 && still < 1.5)
        {
            total += fdt;
            still = Math.Abs(cursorVx) + Math.Abs(cursorVy) < 40 ? still + fdt : 0;

            double target = InStage(cursor.X), gap = target - a.X;
            if (Math.Abs(gap) > 4 * c)
            {
                a.WalkToward(target, fdt, 85);
                a.Ghosts(2, Math.Sign(gap));
                a.Tilt(Math.Sign(gap) * 12);
                pounce = 0;
            }
            else
            {
                pounce += fdt;
                a.Lift = 8 * c * Arc(pounce / 0.45 % 1);
                a.ArmsUp();
                a.Look(0, -0.3);
            }

            // A zooming past bowls B over.
            int sideNow = Math.Sign(a.X - b.X);
            if (sideNow != 0 && sideNow != side)
            {
                if (tumble < 0) { tumble = 0; knock = sideNow; }
                side = sideNow;
            }
            if (tumble >= 0)
            {
                tumble += fdt;
                if (tumble < 0.8)
                {
                    double u = tumble / 0.8;
                    b.X = InStage(b.X + knock * 30 * c * (1 - u) * fdt);
                    b.Tilt(knock * 810 * u);
                    b.Lift = 2 * c * Math.Min(1, u * 5) + 5 * c * Arc(u);
                }
                else if (tumble < 2) Lie(b, knock);
                else if (tumble < 2.3)
                {
                    double u = (tumble - 2) / 0.3;
                    b.Tilt(knock * 90 * (1 - u));
                    b.Lift = 2 * c * (1 - u);
                }
                else tumble = -1;
            }
            else b.LookAt(Over(a, -4), GroundY);
            yield return 0;
        }

        // Worn out.
        foreach (var _ in Over(1.6, u => { a.Sit(); a.Squash(0.25 + 0.2 * Math.Sin(u * 30)); })) yield return 0;
    }

    // ---- 4. Lifting the window ----

    IEnumerable<int> PushWindow()
    {
        if ((WindowOnTaskbar() ?? (forced ? ActualWidth / 2 : null)) is not { } centre) yield break;
        double c = Cell, x = Math.Clamp(centre, Edge + 8 * c, ActualWidth - Edge - 8 * c);
        Nearest(x);
        Bot a = A, b = B;

        foreach (var _ in Walk(a, x - 7 * c)) yield return 0;
        double heave = 0;
        stanceA = () =>
        {
            heave += fdt;
            a.ArmsUp();
            a.Stretch(0.6 * Math.Abs(Math.Sin(heave * 5)));
            a.Look(0, -0.35);
        };
        stanceB = () => b.LookAt(Over(a, 0), GroundY);
        foreach (var _ in Over(1.6, u => { })) yield return 0;

        // B lends a hand.
        stanceB = null;
        foreach (var _ in Walk(b, x + 7 * c, 30)) yield return 0;
        stanceA = null;
        foreach (var _ in Over(1.8, u =>
        {
            double shake = frame % 2 == 0 ? 1.2 : -1.2;
            foreach (Bot bot in bots) { bot.ArmsUp(); bot.Shake(shake); bot.Look(0, -0.35); }
        })) yield return 0;

        // Too heavy: pancakes.
        foreach (var _ in Over(0.2, u => { foreach (Bot bot in bots) bot.Flatten(u); })) yield return 0;
        foreach (var _ in Over(1.7, u => { foreach (Bot bot in bots) { bot.Flatten(1); bot.EyesClosed(); } })) yield return 0;
        foreach (var _ in Over(0.35, u => { foreach (Bot bot in bots) { bot.Flatten(1 - u); bot.Lift = 3 * c * Arc(u); } }))
            yield return 0;
        foreach (var _ in Over(0.9, u => { foreach (Bot bot in bots) bot.Look(0, -0.35); })) yield return 0;
    }

    // ---- 5. Graffiti on the clock ----

    IEnumerable<int> Graffiti(Taskbar.Scan scan)
    {
        if (scan.Clock is not { } button) yield break;
        Rect clock = ToStage(button);
        if (!OnTaskbar(clock)) yield break;
        double c = Cell, mid = clock.X + clock.Width / 2;
        int sd = mid > ActualWidth / 2 ? 1 : -1;                    // the bots come at the clock from this side
        double ax = mid - sd * (clock.Width / 2 + 3 * c);
        Nearest(ax);
        Bot a = A, b = B;

        // The paint is drawn upright on the screen, like the clock it covers.
        var wall = new Rect(mid - button.Width / dpiScale / 2, clock.Y + clock.Height / 2 - button.Height / dpiScale / 2,
            button.Width / dpiScale, button.Height / dpiScale);
        double paint = 0, reach = 0, canY = 0;
        bool can = false, spraying = false, sponge = false;
        Point spongeAt = default;
        double Along(double u) => sd > 0 ? clock.X + clock.Width * u : clock.Right - clock.Width * u;
        sceneUnder = dc =>
        {
            dc.PushTransform(new RotateTransform(-StageAngle, mid, clock.Y + clock.Height / 2));
            Props.Graffiti(dc, wall, paint);
            dc.Pop();
        };
        sceneDraw = dc =>
        {
            var canAt = new Point(a.X + sd * 7 * c, GroundY - 4 * c + canY);
            if (can) Props.SprayCan(dc, canAt, spraying ? sd * 120 : 0, 0.8 * c);
            if (spraying)
                for (int i = 0; i < 7; i++)
                {
                    double x = Along(reach) + (rng.NextDouble() - 0.5) * 3 * c;
                    double y = GroundY - rng.NextDouble() * 3 * c + clock.Height * 0.4 * rng.NextDouble();
                    dc.DrawRectangle(i % 2 == 0 ? Props.Pink : Props.Lime, null, new Rect(x, y, 0.5 * c, 0.5 * c));
                }
            if (sponge) Props.Draw(dc, Props.Sponge, spongeAt, 3.5 * c);
        };

        foreach (var _ in WalkBoth(ax, ax - sd * 22 * c)) yield return 0;
        stanceB = () => b.LookAt(Over(a, 0), GroundY);

        // Shake the can: clack-clack.
        can = true;
        foreach (var _ in Over(1.1, u =>
        {
            bool up = (int)(u * 10) % 2 == 0;
            canY = up ? -1.2 * c : 0;
            a.Arms(sd < 0 && up ? -1 : 0, sd > 0 && up ? -1 : 0);
            a.Look(sd * 0.5, 0);
        })) yield return 0;
        canY = 0;

        sceneOverTaskbar = true;
        BringToTop?.Invoke();
        spraying = true;
        foreach (var _ in Over(2.2, u =>
        {
            paint = reach = u;
            a.Tilt(sd * 14);
            a.X = ax + sd * clock.Width * 0.6 * u;
            a.Look(sd * 0.5, 0.3);
        })) yield return 0;
        spraying = false;
        can = false;
        paint = 1;

        // A admires the work and slinks off; B rushes in with a sponge.
        foreach (var _ in Over(0.7, u => { a.Lift = 2 * c * Arc(u * 2 % 1); a.ArmsUp(); })) yield return 0;
        stanceB = null;
        double away = mid - sd * 45 * c, bx = mid - sd * 2 * c;
        while (!b.WalkToward(bx, fdt, 45, false))
        {
            a.WalkToward(away, fdt, 30, false);
            yield return 0;
        }
        sponge = true;
        foreach (var _ in Over(2.4, u =>
        {
            a.WalkToward(away, fdt, 30, false);
            paint = 1 - u;
            spongeAt = new(Along(u), clock.Y + clock.Height * (0.5 + 0.3 * Math.Sin(u * 40)));
            b.Tilt(sd * 10);
            b.Squash(0.4 * Math.Abs(Math.Sin(u * 40)));
            b.Look(sd * 0.5, 0.3);
        })) yield return 0;
        sponge = false;
        paint = 0;
        sceneOverTaskbar = false;
        foreach (var _ in Over(0.8, u => { b.Look(-sd * 0.5, 0); a.Look(sd * 0.5, 0); a.Tongue(); })) yield return 0;
    }

    // ---- 6. Icon bowling ----

    IEnumerable<int> Bowling(Taskbar.Scan scan)
    {
        double c = Cell, w = ActualWidth;
        if (scan.Apps.Count < 4) yield break;

        // Three neighbouring icons are the pins. Neighbours along the taskbar, whichever way it runs.
        var apps = new List<Int32Rect>(scan.Apps);
        apps.Sort((p, q) => ToStage(p).X.CompareTo(ToStage(q).X));
        Shot[]? pins = null;
        for (int attempt = 0; attempt < 6 && pins is null; attempt++)
        {
            int first = rng.Next(apps.Count - 2);
            Shot? p0 = Shoot(apps[first]), p1 = Shoot(apps[first + 1]), p2 = Shoot(apps[first + 2]);
            if (p0 is not null && p1 is not null && p2 is not null && OnTaskbar(p0.Slot)) pins = new[] { p0, p1, p2 };
        }
        if (pins is null) yield break;

        double left = pins[0].Centre.X, right = pins[2].Centre.X;
        int d = left - 55 * c > Edge ? 1 : -1;                    // which way the ball rolls
        double near = d > 0 ? left : right, far = d > 0 ? right : left;
        double lane = InStage(near - d * 55 * c), pinSpot = far + d * 10 * c;
        Nearest(lane);
        Bot a = A, b = B;

        double g = Physics.Gravity * c;
        double ballX = 0, ballAngle = 0, ballOpacity = 0;
        bool sign = false;
        var hit = new double[3];                                    // seconds since each pin was hit, -1 = not yet
        var pinUp = new double[3];                                  // upward speed each pin was knocked to
        Array.Fill(hit, -1.0);
        // Height of a knocked pin: h = v t - g t^2 / 2, until it comes back down.
        double Hop(int i) => hit[i] < 0 ? -1 : pinUp[i] * hit[i] - 0.5 * g * hit[i] * hit[i];
        sceneUnder = dc =>
        {
            for (int i = 0; i < 3; i++)
                if (Hop(i) >= 0) DrawCover(dc, pins[i]);
        };
        sceneDraw = dc =>
        {
            for (int i = 0; i < 3; i++)
                if (Hop(i) >= 0) DrawIcon(dc, pins[i], new Point(pins[i].Centre.X, pins[i].Centre.Y - Hop(i)));
            Props.Draw(dc, Props.Ball, new Point(ballX, GroundY - 2.75 * c), 5.5 * c, ballAngle, ballOpacity);
            if (sign) Props.Sign(dc, Over(b, 0), 0.8 * c);
        };

        foreach (var _ in WalkBoth(lane, pinSpot)) yield return 0;
        stanceB = () => b.LookAt(new Point(ballX, GroundY - 2 * c), GroundY);

        // Wind up and let go.
        ballOpacity = 1;
        foreach (var _ in Over(0.7, u =>
        {
            ballX = a.X + d * (7 - 5 * Math.Sin(Math.PI * u)) * c;
            a.Squash(0.7 * Math.Sin(Math.PI * u));
            a.Look(d * 0.5, 0.25);
        })) yield return 0;

        // The roll. Rolling friction slows the ball at mu * g. Each pin is an elastic collision with
        // something 0.15 of the ball's mass: momentum and energy are conserved, so the ball keeps
        // (1 - r) / (1 + r) of its speed and the pin leaves at 2 / (1 + r) of it. Later pins hop lower.
        const double massRatio = 0.15;
        double speed = 85 * c, stop = pinSpot - d * 5 * c;
        while (speed > 2 * c && d * (stop - ballX) > 0)
        {
            speed = Math.Max(0, speed - Physics.RollingFriction * g * fdt);
            ballX += d * speed * fdt;
            ballAngle += d * speed * fdt / (2.75 * c) * 180 / Math.PI;
            a.Look(d * 0.5, 0);
            for (int i = 0; i < 3; i++)
            {
                if (hit[i] < 0 && d * (ballX - pins[i].Centre.X) >= 0)
                {
                    pinUp[i] = 0.35 * 2 * speed / (1 + massRatio);      // a third of the kick goes upward
                    speed *= (1 - massRatio) / (1 + massRatio);
                    hit[i] = 0;
                    sceneOverTaskbar = true;
                    BringToTop?.Invoke();
                }
                else if (hit[i] >= 0) hit[i] += fdt;
            }
            yield return 0;
        }

        // Strike!
        sign = true;
        stanceB = null;
        foreach (var _ in Over(2.2, u =>
        {
            for (int i = 0; i < 3; i++) if (hit[i] >= 0) hit[i] += fdt;
            if (u > 0.3) sceneOverTaskbar = false;
            ballOpacity = Math.Max(0, 1 - u * 3);
            b.Lift = 4 * c * Arc(u * 5 % 1);
            b.ArmsUp();
            a.Lift = 3 * c * Arc(u * 4 % 1);
            a.ArmsUp();
        })) yield return 0;
    }

    // ---- 7. Taunting the user ----

    IEnumerable<int> Mooning(Taskbar.Scan scan)
    {
        if (!forced)
            foreach (Int32Rect r in scan.All)
            {
                Rect taken = ToStage(r);
                if (moonX >= taken.Left && moonX < taken.Right) yield break;       // not empty taskbar
            }

        double c = Cell, w = ActualWidth;
        double x = forced ? w / 2 : InStage(moonX);
        Nearest(x);
        Bot a = A, b = B;
        double bang = 0;
        sceneDraw = dc => Props.Draw(dc, Props.Bang, Over(b, 3), 3 * c, 0, bang);

        while (!a.WalkToward(x, fdt, 70))
        {
            b.LookAt(Over(a, 0), GroundY);
            yield return 0;
        }

        // Back turned, wiggling and slapping its rear; B gasps, then cannot watch.
        foreach (var _ in Over(2.2, u =>
        {
            bool ph = (int)(u * 14) % 2 == 0;
            a.FaceAway();
            a.Shake(ph ? 0.6 * c : -0.6 * c);
            a.Tilt(ph ? 4 : -4);
            a.Arms(0, ph ? 2 : 1);
            b.ArmsUp();
            if (u < 0.25) { b.Lift = 4 * c * Arc(u / 0.25); bang = 1; }
            else { bang = 0; b.EyesClosed(); }
        })) yield return 0;

        foreach (var _ in Over(1.3, u =>
        {
            bool ph = (int)(u * 8) % 2 == 0;
            a.Tongue();
            a.Arms(ph ? -2 : -1, ph ? -1 : -2);
            b.ArmsUp();
            b.EyesClosed();
        })) yield return 0;

        // B marches over and hauls A off the screen.
        int s = b.X >= a.X ? 1 : -1;
        stanceA = () => a.Tongue();
        foreach (var _ in Walk(b, a.X + s * 11 * c, 45)) yield return 0;
        stanceA = null;
        double gap = a.X - b.X, exit = s > 0 ? w + 25 * c : -25 * c;
        while (!b.WalkToward(exit, fdt, 55, false))
        {
            a.X = b.X + gap;
            a.Tilt(-s * 25);
            a.Tongue();
            a.Arms(s < 0 ? -1 : 0, s > 0 ? -1 : 0);
            yield return 0;
        }
        a.X = b.X + gap;
        foreach (var _ in Over(1.2, u => { })) yield return 0;
    }

    // ---- 8. Shoving the hovered icon ----

    IEnumerable<int> Shove(Taskbar.Scan scan)
    {
        Int32Rect? hovered = null;
        foreach (Int32Rect r in scan.Apps)
        {
            Rect under = ToStage(r);
            if (cursor.X >= under.Left && cursor.X < under.Right) hovered = r;
        }
        if (forced && scan.Apps.Count > 0) hovered = scan.Apps[rng.Next(scan.Apps.Count)];
        else if (!CursorOnTaskbar) hovered = null;
        if (hovered is not { } button) yield break;

        double c = Cell;
        Rect slot = ToStage(button);
        if (!OnTaskbar(slot)) yield break;
        double ix = slot.X + slot.Width / 2;
        int d = ix - 20 * c > Edge ? 1 : -1;                      // direction A pushes in
        double ax = ix - d * 8 * c, bx = ax - d * 11 * c, anger = 0;
        Nearest(ax);
        Bot a = A, b = B;
        sceneDraw = dc =>
        {
            Props.Draw(dc, Props.Anger, Over(a, 3), 3 * c, 0, anger);
        };

        foreach (var _ in Walk(a, ax, 30)) yield return 0;
        stanceA = () => { a.Tilt(d * 16); a.StepInPlace(fdt); a.Look(d * 0.5, 0.25); };
        anger = 1;
        foreach (var _ in Walk(b, bx, 40)) yield return 0;

        // B hangs on and pulls the other way.
        foreach (var _ in Over(4.5, u =>
        {
            double tug = 2 * c * Math.Sin(u * 14);
            a.X = ax + tug;
            b.X = bx + tug;
            b.Tilt(-d * 18);
            b.StepInPlace(fdt);
            b.Look(d * 0.5, 0);
            b.Arms(d > 0 ? 0 : -1, d > 0 ? -1 : 0);
        })) yield return 0;

        // Something gives and both go over backwards.
        stanceA = null;
        anger = 0;
        foreach (var _ in Over(0.35, u =>
        {
            a.Tilt(d * 16 - d * 106 * u);
            b.Tilt(-d * 18 - d * 72 * u);
            a.Lift = b.Lift = 2 * c * u;
            a.X = ax - d * 3 * c * u;
            b.X = bx - d * 3 * c * u;
        })) yield return 0;
        foreach (var _ in Over(1.3, u => { Lie(a, -d); Lie(b, -d); })) yield return 0;
        foreach (var _ in Over(0.3, u =>
        {
            foreach (Bot bot in bots) { bot.Tilt(-d * 90 * (1 - u)); bot.Lift = 2 * c * (1 - u); }
        })) yield return 0;
    }

    // ---- Bored-kid mischief ----

    /// <summary>A creeps up behind B and makes it jump out of its skin.</summary>
    IEnumerable<int> Scare()
    {
        double c = Cell;
        sa = rng.Next(2);
        Bot a = A, b = B;
        int away = a.X < b.X ? 1 : -1;                              // the way B is looking: away from A
        double bang = 0, anger = 0;
        sceneDraw = dc =>
        {
            Props.Draw(dc, Props.Bang, Over(b, 3), 3 * c, 0, bang);
            Props.Draw(dc, Props.Anger, Over(b, 3), 3 * c, 0, anger);
        };
        stanceB = () => b.Look(away * 0.5, 0);

        foreach (var _ in Walk(a, b.X - away * 32 * c)) yield return 0;
        foreach (var _ in Walk(a, b.X - away * 11 * c, 6)) yield return 0;

        stanceB = null;
        bang = 1;
        foreach (var _ in Over(0.55, u =>
        {
            a.ArmsUp();
            a.Lift = 5 * c * Arc(u);
            b.Lift = 12 * c * Arc(u);
            b.ArmsUp();
            b.Tilt(away * 20 * Math.Sin(Math.PI * u));
            b.Look(0, -0.35);
        })) yield return 0;
        bang = 0;

        double flee = InStage(b.X + away * 45 * c), laugh = 0;
        while (!b.WalkToward(flee, fdt, 80))
        {
            b.Ghosts(2, away);
            laugh += fdt;
            a.Lift = 2.5 * c * Arc(laugh * 3 % 1);
            a.Look(away * 0.5, 0);
            yield return 0;
        }
        anger = 1;
        foreach (var _ in Over(1.8, u =>
        {
            a.Lift = 2.5 * c * Arc(u * 5 % 1);
            a.Look(away * 0.5, 0);
            b.Look(-away * 0.5, 0);
            b.StepInPlace(fdt);
        })) yield return 0;
    }

    /// <summary>A climbs onto B's head, B carries it, it ends the way it always does.</summary>
    IEnumerable<int> Tower()
    {
        double c = Cell;
        sa = rng.Next(2);
        Bot a = A, b = B;
        int s = a.X >= b.X ? 1 : -1;
        double top = b.Height;

        foreach (var _ in Walk(a, b.X + s * 12 * c)) yield return 0;
        foreach (var _ in Over(0.3, u => b.Squash(u))) yield return 0;
        double from = a.X;
        foreach (var _ in Over(0.55, u =>
        {
            b.Squash(1 - u);
            a.X = from + (b.X - from) * u;
            a.Lift = top * u + 6 * c * Arc(u);
            a.ArmsUp();
        })) yield return 0;

        // Off they go, wobbling more with every step.
        int go = b.X < ActualWidth / 2 ? 1 : -1;
        double target = InStage(b.X + go * 35 * c), time = 0;
        while (!b.WalkToward(target, fdt, 10))
        {
            time += fdt;
            bool ph = (int)(time * 6) % 2 == 0;
            a.X = b.X;
            a.Lift = top;
            a.Tilt(Math.Min(18, time * 5) * Math.Sin(time * 6));
            a.Arms(ph ? -2 : 1, ph ? 1 : -2);
            yield return 0;
        }

        double ax = a.X;
        foreach (var _ in Over(0.6, u =>
        {
            a.X = ax + go * 14 * c * u;
            a.Lift = top * (1 - u * u) + 2 * c * u;
            a.Tilt(go * 450 * u);
            b.LookAt(Over(a, -4), GroundY);
        })) yield return 0;
        foreach (var _ in Over(1.4, u =>
        {
            Lie(a, go);
            b.Lift = 2.5 * c * Arc(u * 4 % 1);
            b.Look(go * 0.5, 0);
        })) yield return 0;
        foreach (var _ in GetUp(a, go)) yield return 0;
    }

    /// <summary>You're it.</summary>
    IEnumerable<int> TagGame()
    {
        double c = Cell, w = ActualWidth;
        sa = rng.Next(2);
        Bot it = A, runner = B;

        int rounds = 3 + rng.Next(2);
        for (int round = 0; round < rounds; round++)
        {
            while (!it.WalkToward(runner.X + Math.Sign(it.X - runner.X + 0.01) * 11 * c, fdt, 50))
            {
                runner.LookAt(Over(it, -4), GroundY);
                yield return 0;
            }
            Bot tagger = it, tagged = runner;
            int toward = tagged.X > tagger.X ? 1 : -1;
            foreach (var _ in Over(0.3, u =>
            {
                tagger.Tilt(toward * 12 * Math.Sin(Math.PI * u));
                tagger.Arms(toward < 0 ? -1 : 0, toward > 0 ? -1 : 0);
                tagged.Lift = 3 * c * Arc(u);
            })) yield return 0;
            (it, runner) = (runner, it);

            // The tagger legs it; whoever is "it" gives a short head start.
            double flee = runner.X - toward * (40 + rng.NextDouble() * 40) * c;
            if (flee < Edge || flee > w - Edge) flee = runner.X + toward * 60 * c;
            flee = InStage(flee);
            double lead = 0;
            while (!runner.WalkToward(flee, fdt, 48))
            {
                lead += fdt;
                runner.Ghosts(1, Math.Sign(flee - runner.X));
                if (lead > 0.5) it.WalkToward(runner.X, fdt, 36);
                else it.LookAt(Over(runner, -4), GroundY);
                yield return 0;
            }
        }

        foreach (var _ in Over(1.8, u =>
        {
            foreach (Bot bot in bots) { bot.Sit(); bot.Squash(0.25 + 0.2 * Math.Sin(u * 34)); }
        })) yield return 0;
    }

    /// <summary>Tongues out at whoever is watching.</summary>
    IEnumerable<int> Faces()
    {
        double c = Cell;
        sa = bots[0].X <= bots[1].X ? 0 : 1;                        // A is the left one
        Bot a = A, b = B;
        double mid = Math.Clamp((a.X + b.X) / 2, Edge + 9 * c, ActualWidth - Edge - 9 * c);
        foreach (var _ in WalkBoth(mid - 9 * c, mid + 9 * c)) yield return 0;

        foreach (var _ in Over(2.8, u =>
        {
            bool ph = (int)(u * 16) % 2 == 0;
            foreach (Bot bot in bots)
            {
                bot.Tongue();
                bot.Arms(ph ? -2 : -1, ph ? -1 : -2);
                bot.Shake(ph ? 0.4 * c : -0.4 * c);
            }
        })) yield return 0;
        foreach (var _ in Over(1.3, u =>
        {
            a.Look(0.5, 0);
            b.Look(-0.5, 0);
            a.Lift = 2.5 * c * Arc(u * 4 % 1);
            b.Lift = 2.5 * c * Arc((u * 4 + 0.5) % 1);
        })) yield return 0;
    }

    /// <summary>Two of them stop for a chat: one talks, the other listens, then they swap.</summary>
    IEnumerable<int> Chat()
    {
        double c = Cell;
        sa = bots[0].X <= bots[1].X ? 0 : 1;                        // A is the left one
        Bot a = A, b = B;
        double mid = Math.Clamp((a.X + b.X) / 2, Edge + 9 * c, ActualWidth - Edge - 9 * c);
        foreach (var _ in WalkBoth(mid - 9 * c, mid + 9 * c)) yield return 0;

        string[] lines = { Props.Speech, Props.Question, Props.Bang, Props.Laugh, Props.Idea, Props.Speech };
        int turns = 4 + rng.Next(4);
        for (int turn = 0; turn < turns; turn++)
        {
            Bot talker = turn % 2 == 0 ? a : b, listener = turn % 2 == 0 ? b : a;
            int facing = talker == a ? 1 : -1;
            string line = lines[rng.Next(lines.Length)];
            foreach (var _ in Over(1.1 + rng.NextDouble() * 0.9, u =>
            {
                bool beat = (int)(u * 6) % 2 == 0;
                talker.Say(line);
                talker.Look(facing * 0.5, 0);
                talker.Arms(facing < 0 && beat ? -1 : 0, facing > 0 && beat ? -1 : 0);      // talks with its hands
                listener.Look(-facing * 0.5, beat ? 0.2 : 0);                               // nods along
                if (line == Props.Laugh) listener.Lift = 1.5 * c * Arc(u * 3 % 1);
            })) yield return 0;
        }

        // A wave goodbye.
        foreach (var _ in Over(1.2, u =>
        {
            bool up = (int)(u * 6) % 2 == 0;
            a.Arms(0, up ? -2 : -1);
            b.Arms(up ? -2 : -1, 0);
            a.Look(0.5, 0);
            b.Look(-0.5, 0);
        })) yield return 0;
    }

    // ---- Everyday life: the whole cast reacts to what is going on at the PC ----

    bool wasAway, plugged = true, powerSeen;
    double moodIn, glanceCooldown;
    IntPtr lastWindow;

    void Live(double dt)
    {
        // Somebody came back to the PC after a while: they wave hello.
        double idle = Taskbar.IdleSeconds();
        if (idle > 90) wasAway = true;
        else if (wasAway && idle < 2)
        {
            wasAway = false;
            foreach (Bot b in cast) b.Queue(Move.Wave, rng.NextDouble() * 1.5);
        }

        // A new window came to the front: a couple of them look up to see what it is.
        glanceCooldown -= dt;
        IntPtr window = Taskbar.ForegroundHandle();
        if (window != lastWindow)
        {
            lastWindow = window;
            if (glanceCooldown <= 0)
            {
                glanceCooldown = 25;
                for (int i = 0; i < 2; i++) cast[rng.Next(cast.Length)].Queue(Move.LookAround, 0.3 + rng.NextDouble());
            }
        }

        moodIn -= dt;
        if (moodIn > 0) return;
        moodIn = 5;

        // Charger plugged in: a little jump of joy. Pulled out: they look around, worried.
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        bool online = power.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Offline;
        if (powerSeen && online != plugged)
            foreach (Bot b in cast) b.Queue(online ? Move.Jump : Move.LookAround, rng.NextDouble() * 1.2);
        plugged = online;
        powerSeen = true;

        // Late at night, with nobody around, or on a nearly flat battery they get drowsy.
        int hour = DateTime.Now.Hour;
        bool night = hour >= 23 || hour < 6;
        bool flat = !online && power.BatteryLifePercent < 0.2f;
        double sleepy = night ? 1 : idle > 90 ? 0.7 : flat ? 0.6 : 0;
        foreach (Bot b in cast) b.Sleepy = sleepy;
    }
}