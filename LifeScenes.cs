using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace TaskbarBot;

/// <summary>
/// The rowdier side of life on the taskbar: wrecking the pinned icons, fights, parties,
/// football, races, hide and seek, dinner.
/// </summary>
public sealed partial class StageView
{
    // ---- Making a mess of the pinned icons ----

    /// <summary>A taskbar icon the bots have pulled out: loose (a Body), or carried / in flight (At).</summary>
    sealed class LooseIcon
    {
        public required Shot Shot { get; init; }
        public Body? Body { get; set; }
        public bool Out { get; set; }
        public Point At { get; set; }
        public double Angle { get; set; }
    }

    IEnumerable<int> Mess(Taskbar.Scan scan)
    {
        double c = Cell, w = ActualWidth, g = Physics.Gravity * c;
        var apps = new List<Int32Rect>(scan.Apps);
        apps.Sort((p, q) => ToStage(p).X.CompareTo(ToStage(q).X));
        var icons = new List<LooseIcon>();
        int first = apps.Count > 5 ? rng.Next(apps.Count - 4) : 0;
        for (int i = first; i < apps.Count && icons.Count < 5; i++)
            if (Shoot(apps[i]) is { } s && OnTaskbar(s.Slot) && s.Centre.X > Edge && s.Centre.X < w - Edge)
                icons.Add(new LooseIcon { Shot = s, At = s.Centre });
        if (icons.Count < 3) yield break;

        Nearest(icons[0].Shot.Centre.X);
        Bot a = A, b = B;
        var cooldown = new double[2];

        sceneUnder = dc =>
        {
            foreach (LooseIcon icon in icons)
                if (icon.Out) DrawCover(dc, icon.Shot);
        };
        sceneDraw = dc =>
        {
            foreach (LooseIcon icon in icons)
                if (icon.Out) DrawIcon(dc, icon.Shot, icon.Body?.Pos ?? icon.At, icon.Body?.Angle ?? icon.Angle);
        };

        // The loose icons are ordinary bodies: they fall, bounce on the taskbar's edge and off the screen's sides.
        void Sim()
        {
            foreach (LooseIcon icon in icons)
            {
                if (icon.Body is not { } body) continue;
                body.Step(fdt, g, GroundY, 0.6, Physics.Friction);
                if (body.Pos.X < Edge || body.Pos.X > w - Edge)
                {
                    body.Pos = new Point(InStage(body.Pos.X), body.Pos.Y);
                    body.Vel = new Vector(-body.Vel.X * 0.6, body.Vel.Y);
                }
            }
        }

        // Runs after the nearest loose icon and boots it.
        void Chase(Bot bot, int who)
        {
            cooldown[who] -= fdt;
            Body? ball = null;
            foreach (LooseIcon icon in icons)
                if (icon.Body is { } body && (ball is null || Math.Abs(body.Pos.X - bot.X) < Math.Abs(ball.Pos.X - bot.X)))
                    ball = body;
            if (ball is null) return;

            bot.WalkToward(InStage(ball.Pos.X), fdt, 42, false);
            if (cooldown[who] <= 0 && Math.Abs(ball.Pos.X - bot.X) < 5 * c && ball.Pos.Y > GroundY - 7 * c)
            {
                cooldown[who] = 0.6;
                ball.Kick(new Vector((rng.Next(2) == 0 ? -1 : 1) * (25 + rng.NextDouble() * 45) * c, -(45 + rng.NextDouble() * 40) * c));
            }
            if (cooldown[who] > 0.35) bot.Lift = 2 * c * Arc((0.6 - cooldown[who]) / 0.25);
        }

        // A climbs down into the taskbar and hurls the icons out one by one; B is already at them.
        foreach (var _ in Walk(a, icons[0].Shot.Centre.X, 30)) yield return 0;
        double floor = -(BarDepth - 2);
        sceneOverTaskbar = true;
        BringToTop?.Invoke();
        lowBot = a;
        lowClip = null;
        foreach (var _ in Leap(a, 0, floor, 3 * c)) yield return 0;

        foreach (LooseIcon icon in icons)
        {
            while (!a.WalkToward(icon.Shot.Centre.X, fdt, 16, false))
            {
                a.Lift = floor;
                Sim();
                Chase(b, 1);
                yield return 0;
            }
            foreach (var _ in Over(0.25, u => { a.Lift = floor; a.Squash(u); Sim(); Chase(b, 1); })) yield return 0;

            double up = Math.Sqrt(2 * g * (BarDepth + (6 + rng.NextDouble() * 8) * c));
            icon.Out = true;
            icon.Body = new Body
            {
                Pos = icon.Shot.Centre, Radius = icon.Shot.Size / 2, Spin = (rng.NextDouble() - 0.5) * 900,
                Vel = new Vector((rng.NextDouble() - 0.5) * 60 * c, -up),
            };
        }
        foreach (var _ in Leap(a, floor, 0, 4 * c))
        {
            Sim();
            Chase(b, 1);
            yield return 0;
        }
        lowBot = null;

        // Mayhem.
        for (double t = 0; t < 6; t += fdt)
        {
            Sim();
            Chase(a, 0);
            Chase(b, 1);
            yield return 0;
        }

        // Somebody has to tidy up: each icon is fetched and thrown back into its slot.
        int turn = 0;
        foreach (LooseIcon icon in icons)
        {
            Bot bot = turn++ % 2 == 0 ? a : b;
            Body body = icon.Body!;
            while (!bot.WalkToward(InStage(body.Pos.X), fdt, 45, false))
            {
                Sim();
                yield return 0;
            }
            icon.Body = null;
            foreach (var _ in Over(0.3, u =>
            {
                bot.ArmsUp();
                icon.At = Lerp(body.Pos, bot.HoldPoint(GroundY, icon.Shot.Size), u);
                icon.Angle = body.Angle * (1 - u);
                Sim();
            })) yield return 0;

            var toss = Throw.Between(icon.At, icon.Shot.Centre, 6 * c, g);
            for (double t = 0; t < toss.Time; t += fdt)
            {
                icon.At = toss.At(t);
                Sim();
                yield return 0;
            }
            icon.Out = false;
        }
        sceneOverTaskbar = false;
        foreach (var _ in Over(0.9, u => { a.LookAt(Over(b, -4), GroundY); b.LookAt(Over(a, -4), GroundY); })) yield return 0;
    }

    // ---- A fight ----

    IEnumerable<int> Fight()
    {
        double c = Cell;
        sa = bots[0].X <= bots[1].X ? 0 : 1;                        // A is the left one
        Bot a = A, b = B;
        double mid = Math.Clamp((a.X + b.X) / 2, Edge + 24 * c, ActualWidth - Edge - 24 * c);
        foreach (var _ in WalkBoth(mid - 7 * c, mid + 7 * c, 30)) yield return 0;

        // Words first.
        for (int i = 0; i < 4; i++)
        {
            Bot talker = i % 2 == 0 ? a : b;
            int facing = talker == a ? 1 : -1;
            foreach (var _ in Over(0.55, u =>
            {
                talker.Say(Props.Anger);
                talker.Tilt(facing * 10 * Math.Sin(Math.PI * u));
                talker.StepInPlace(fdt);
                a.Look(0.5, 0);
                b.Look(-0.5, 0);
            })) yield return 0;
        }

        // Then shoving.
        double ax = a.X, bx = b.X;
        foreach (var _ in Over(0.35, u => { a.Tilt(14 * Math.Sin(Math.PI * u)); b.X = bx + 5 * c * Ease(u); b.Tilt(12 * u); }))
            yield return 0;
        bx = b.X;
        foreach (var _ in Over(0.35, u => { b.Tilt(-14 * Math.Sin(Math.PI * u)); a.X = ax - 5 * c * Ease(u); a.Tilt(-12 * u); }))
            yield return 0;

        // Then the cloud of blows, rolling along the taskbar.
        double centre = (a.X + b.X) / 2;
        int roll = rng.Next(2) == 0 ? -1 : 1;
        bool cloud = true;
        var blows = new Point[3];
        sceneDraw = dc =>
        {
            if (!cloud) return;
            foreach (Point blow in blows)
                Props.Draw(dc, Props.Boom, new Point(centre + blow.X, GroundY - 5 * c + blow.Y), 6 * c);
        };
        for (double t = 0; t < 3.2; t += fdt)
        {
            centre = Math.Clamp(centre + roll * 9 * c * fdt * Math.Sin(t * 2), Edge + 14 * c, ActualWidth - Edge - 14 * c);
            for (int k = 0; k < blows.Length; k++)
                blows[k] = new Point((rng.NextDouble() - 0.5) * 12 * c, (rng.NextDouble() - 0.5) * 8 * c);
            foreach (Bot bot in bots)
            {
                bot.X = centre + (rng.NextDouble() - 0.5) * 6 * c;
                bot.Tilt((rng.NextDouble() - 0.5) * 80);
                bot.Lift = rng.NextDouble() * 3 * c;
                bot.Arms(rng.Next(-2, 3), rng.Next(-2, 3));
                if (rng.Next(3) == 0) bot.EyesClosed();
            }
            yield return 0;
        }
        cloud = false;

        // Both come flying out of it.
        foreach (var _ in Over(0.6, u =>
        {
            a.X = centre - 16 * c * u;
            b.X = centre + 16 * c * u;
            a.Lift = b.Lift = 6 * c * Arc(u) + 2 * c * u;
            a.Tilt(-450 * u);
            b.Tilt(450 * u);
        })) yield return 0;
        foreach (var _ in Over(1.2, u => { Lie(a, -1); Lie(b, 1); })) yield return 0;
        foreach (var _ in Over(0.3, u =>
        {
            a.Tilt(-90 * (1 - u));
            b.Tilt(90 * (1 - u));
            a.Lift = b.Lift = 2 * c * (1 - u);
        })) yield return 0;

        if (rng.Next(3) > 0)
        {
            // Friends again.
            mid = (a.X + b.X) / 2;
            foreach (var _ in WalkBoth(mid - 5.5 * c, mid + 5.5 * c, 14)) yield return 0;
            foreach (var _ in Over(1.8, u =>
            {
                a.Arms(0, -1);
                b.Arms(-1, 0);
                a.Lift = 1.5 * c * Arc(u * 3 % 1);
                b.Lift = 1.5 * c * Arc((u * 3 + 0.5) % 1);
                a.Look(0.5, 0);
                b.Look(-0.5, 0);
            })) yield return 0;
        }
        else
            // Not speaking.
            foreach (var _ in Over(2.4, u =>
            {
                a.Look(-0.5, 0);
                b.Look(0.5, 0);
                a.Arms(1, 1);
                b.Arms(1, 1);
                if (u < 0.4) a.Say(Props.Anger); else if (u > 0.6) b.Say(Props.Anger);
            })) yield return 0;
    }

    // ---- A party, with everybody ----

    IEnumerable<int> Party()
    {
        double c = Cell, w = ActualWidth, g = Physics.Gravity * c;
        foreach (Bot bot in cast) bot.External = true;              // everyone is invited
        int n = cast.Length;
        double gap = Math.Min(13 * c, (w - 2 * Edge) / n);
        double centre = Math.Clamp(w / 2, Edge + gap * n / 2, Math.Max(Edge + gap * n / 2, w - Edge - gap * n / 2));
        while (true)
        {
            bool all = true;
            for (int i = 0; i < n; i++) all &= cast[i].WalkToward(centre + (i - (n - 1) / 2.0) * gap, fdt, 34, false);
            if (all) break;
            yield return 0;
        }

        var confetti = new List<(Body Body, Brush Brush)>();
        Brush[] colours = { Props.Pink, Props.Lime, Props.Gold, Props.Sky };
        void Burst(double x)
        {
            for (int k = 0; k < 26; k++)
                confetti.Add((new Body
                {
                    Pos = new Point(x, GroundY - 10 * c), Radius = 0.4 * c,
                    Vel = new Vector((rng.NextDouble() - 0.5) * 70 * c, -(40 + rng.NextDouble() * 50) * c),
                }, colours[rng.Next(colours.Length)]));
            if (confetti.Count > 160) confetti.RemoveRange(0, confetti.Count - 160);
        }
        void Rain()
        {
            foreach (var (body, _) in confetti) body.Step(fdt, g * 0.35, GroundY, 0.2, 2);      // paper falls slowly
        }
        sceneDraw = dc =>
        {
            dc.PushClip(AboveGround);
            foreach (var (body, brush) in confetti)
                dc.DrawRectangle(brush, null, new Rect(body.Pos.X - 0.4 * c, body.Pos.Y - 0.4 * c, 0.8 * c, 0.8 * c));
            dc.Pop();
        };

        double nextBurst = 0;
        for (double time = 0; time < 14; time += fdt)
        {
            if (time >= nextBurst)
            {
                Burst(centre + (rng.NextDouble() - 0.5) * gap * n);
                nextBurst = time + 1.8;
            }
            Rain();
            for (int i = 0; i < n; i++)
            {
                Bot bot = cast[i];
                double beat = time * 2.2 + i * 0.5;
                bool ph = (int)beat % 2 == 0;
                switch (i % 3)
                {
                    case 0:                                  // side to side
                        bot.Arms(ph ? -2 : 2, ph ? 2 : -2);
                        bot.Tilt(ph ? -6 : 6);
                        break;
                    case 1:                                  // pogo
                        bot.ArmsUp();
                        bot.Lift = 3 * c * Arc(beat % 1);
                        break;
                    default:                                 // hands in the air
                        bot.Arms(ph ? -2 : -1, ph ? -1 : -2);
                        bot.Shake(ph ? c : -c);
                        break;
                }
                if (i % 2 == 0) bot.Hold(Props.Balloon, i % 4 == 0 ? -6 : 6, 13 + Math.Sin(time * 2 + i) * 0.5);
                else if ((int)(time / 2 + i) % 2 == 0) bot.Say(Props.Note);
            }
            yield return 0;
        }

        // One big jump together.
        for (int k = 0; k < 3; k++) Burst(centre + (k - 1) * gap * 2);
        foreach (var _ in Over(0.9, u =>
        {
            Rain();
            foreach (Bot bot in cast) { bot.ArmsUp(); bot.Lift = 9 * c * Arc(u); }
        })) yield return 0;
        foreach (var _ in Over(1.2, u => Rain())) yield return 0;
    }

    // ---- Football ----

    /// <summary>
    /// A kick-about. Without a scan the ball is a football; with one, it is a real icon pulled out
    /// of the tray corner (the arrow, the language, the network, the speaker, the battery or the
    /// clock), which goes back in its place when the game is over.
    /// </summary>
    IEnumerable<int> Football(Taskbar.Scan? scan)
    {
        double c = Cell, w = ActualWidth, g = Physics.Gravity * c;

        Shot? prize = null;
        if (scan is not null)
        {
            var tray = new List<Int32Rect>(scan.Tray);
            while (prize is null && tray.Count > 0)
            {
                int i = rng.Next(tray.Count);
                if (Shoot(tray[i], whole: true) is { } taken && OnTaskbar(taken.Slot)) prize = taken;
                tray.RemoveAt(i);
            }
            if (prize is null) yield break;
            Nearest(prize.Centre.X);
        }
        else sa = bots[0].X <= bots[1].X ? 0 : 1;
        Bot a = A, b = B;

        double radius = prize is null ? 2 * c : prize.Size / 2;
        var ball = new Body { Radius = radius };
        bool pulled = false, walls = true;
        void Roll()
        {
            ball.Step(fdt, g, GroundY, 0.6, Physics.RollingFriction);
            if (walls && (ball.Pos.X < Edge || ball.Pos.X > w - Edge))
            {
                ball.Pos = new Point(InStage(ball.Pos.X), ball.Pos.Y);
                ball.Vel = new Vector(-ball.Vel.X * 0.6, ball.Vel.Y);
            }
        }
        sceneUnder = dc => { if (prize is not null && pulled) DrawCover(dc, prize); };
        sceneDraw = dc =>
        {
            if (prize is null) Props.Draw(dc, Props.Soccer, ball.Pos, 4 * c, ball.Angle);
            else if (pulled) DrawIcon(dc, prize, ball.Pos, ball.Angle);
        };

        if (prize is null)
        {
            double mid = Math.Clamp((a.X + b.X) / 2, Edge + 24 * c, w - Edge - 24 * c);
            foreach (var _ in WalkBoth(mid - 22 * c, mid + 22 * c, 30)) yield return 0;
            ball.Pos = new Point(a.X + 5 * c, GroundY - radius);
        }
        else
        {
            // A goes and yanks the icon out of the taskbar; B takes up position further in.
            int inward = prize.Centre.X > w / 2 ? -1 : 1;
            double spot = InStage(prize.Centre.X);
            foreach (var _ in WalkBoth(spot, InStage(spot + inward * 44 * c), 40)) yield return 0;
            sceneOverTaskbar = true;
            BringToTop?.Invoke();
            pulled = true;
            Point from = prize.Centre, to = new(a.X + inward * 5 * c, GroundY - radius);
            foreach (var _ in Over(0.45, u =>
            {
                a.Squash(Math.Sin(Math.PI * u));
                Point p = Lerp(from, to, Ease(u));
                ball.Pos = new Point(p.X, p.Y - 4 * c * Arc(u));
            })) yield return 0;
            ball.Pos = to;
        }

        Bot kicker = a, keeper = b;
        int kicks = 5 + rng.Next(4), way = 1;
        for (int k = 0; k <= kicks; k++)
        {
            way = keeper.X > kicker.X ? 1 : -1;
            while (!kicker.WalkToward(ball.Pos.X - way * 4 * c, fdt, 36, false))
            {
                Roll();
                keeper.LookAt(ball.Pos, GroundY);
                yield return 0;
            }
            bool screamer = k == kicks && prize is null;        // a football's last kick goes off the screen
            double reach = Math.Abs(keeper.X - ball.Pos.X);
            walls = !screamer;
            ball.Kick(screamer
                ? new Vector(way * 120 * c, -65 * c)
                : new Vector(way * reach / 0.7, -(25 + rng.NextDouble() * 35) * c));
            foreach (var _ in Over(0.25, u => { kicker.Tilt(way * 14 * (1 - u)); Roll(); })) yield return 0;
            if (screamer) break;

            for (double t = 0; t < 3 && Math.Abs(ball.Pos.X - keeper.X) > 6 * c && !ball.Resting; t += fdt)
            {
                Roll();
                keeper.LookAt(ball.Pos, GroundY);
                kicker.LookAt(ball.Pos, GroundY);
                yield return 0;
            }
            (kicker, keeper) = (keeper, kicker);
        }

        if (prize is not null)
        {
            // Full time: whoever has it carries the icon over and throws it back into its place.
            while (!kicker.WalkToward(InStage(ball.Pos.X), fdt, 40, false))
            {
                Roll();
                yield return 0;
            }
            Point lying = ball.Pos;
            foreach (var _ in Over(0.3, u => { kicker.ArmsUp(); ball.Pos = Lerp(lying, kicker.HoldPoint(GroundY, prize.Size), u); }))
                yield return 0;
            var home = Throw.Between(ball.Pos, prize.Centre, 6 * c, g);
            for (double t = 0; t < home.Time; t += fdt)
            {
                ball.Pos = home.At(t);
                kicker.ArmsUp();
                yield return 0;
            }
            pulled = false;
            sceneOverTaskbar = false;
            foreach (var _ in Over(1.4, u =>
            {
                kicker.Lift = 2 * c * Arc(u * 3 % 1);
                keeper.Lift = 2 * c * Arc((u * 3 + 0.5) % 1);
            })) yield return 0;
            yield break;
        }

        foreach (var _ in Over(2.2, u =>
        {
            Roll();
            kicker.Say(Props.Trophy);
            kicker.ArmsUp();
            kicker.Lift = 3 * c * Arc(u * 4 % 1);
            keeper.Sit();
            keeper.EyesClosed();
        })) yield return 0;
    }

    // ---- A race ----

    IEnumerable<int> Race()
    {
        double c = Cell, w = ActualWidth;
        sa = rng.Next(2);
        Bot a = A, b = B;
        int d = (a.X + b.X) / 2 < w / 2 ? 1 : -1;                  // run towards the far side
        double track = Math.Min(w - 2 * Edge - 16 * c, 150 * c);
        double start = d > 0 ? Edge + 2 * c : w - Edge - 2 * c;
        foreach (var _ in WalkBoth(start, start + d * 13 * c, 34)) yield return 0;

        // On your marks...
        foreach (var _ in Over(1.3, u => { a.Squash(0.7); b.Squash(0.7); a.Look(d * 0.5, 0); b.Look(d * 0.5, 0); })) yield return 0;

        double finishA = start + d * track, finishB = finishA + d * 13 * c;
        double speedA = 60, speedB = 60, reroll = 0;
        bool doneA = false, doneB = false;
        Bot? winner = null;
        while (!doneA || !doneB)
        {
            reroll -= fdt;
            if (reroll <= 0)
            {
                reroll = 0.5;
                speedA = 52 + rng.NextDouble() * 26;
                speedB = 52 + rng.NextDouble() * 26;
            }
            if (!doneA && (doneA = a.WalkToward(finishA, fdt, speedA, false))) winner ??= a;
            if (!doneB && (doneB = b.WalkToward(finishB, fdt, speedB, false))) winner ??= b;
            if (!doneA) { a.Ghosts(1, d); a.Tilt(d * 10); }
            if (!doneB) { b.Ghosts(1, d); b.Tilt(d * 10); }
            yield return 0;
        }

        Bot first = winner ?? a, second = first == a ? b : a;
        foreach (var _ in Over(2.4, u =>
        {
            first.Say(Props.Trophy);
            first.ArmsUp();
            first.Lift = 3 * c * Arc(u * 4 % 1);
            second.Sit();
            second.Squash(0.25 + 0.2 * Math.Sin(u * 40));
        })) yield return 0;
    }

    // ---- Hide and seek ----

    IEnumerable<int> HideSeek()
    {
        double c = Cell;
        sa = rng.Next(2);
        Bot seeker = A, hider = B;
        int away = hider.X >= seeker.X ? 1 : -1;
        double spot = InStage(hider.X + away * (25 + rng.NextDouble() * 30) * c);
        if (Math.Abs(spot - seeker.X) < 22 * c) spot = InStage(seeker.X - away * 40 * c);
        double hidden = -(hider.Height + 6), peeking = hidden + hider.Skin.Top * c + 2.4 * c + 6;

        // Counting, no peeking.
        foreach (var _ in Over(3.4, u => { seeker.EyesClosed(); seeker.ArmsUp(); hider.WalkToward(spot, fdt, 13, false); }))
            yield return 0;
        foreach (var _ in Over(0.4, u => { seeker.EyesClosed(); seeker.ArmsUp(); hider.Lift = hidden * Ease(u); })) yield return 0;
        stanceB = () => hider.Lift = hidden;

        // Ready or not.
        foreach (var _ in Over(1.3, u => { seeker.Say(Props.Question); seeker.Look(u < 0.5 ? -0.5 : 0.5, 0); })) yield return 0;
        int wrong = spot > seeker.X ? -1 : 1;
        foreach (var _ in Walk(seeker, InStage(seeker.X + wrong * 14 * c), 16)) yield return 0;
        foreach (var _ in Over(0.9, u => seeker.Look(wrong * 0.5, 0.2))) yield return 0;
        int side = seeker.X < spot ? -1 : 1;
        foreach (var _ in Walk(seeker, spot + side * 13 * c, 18)) yield return 0;

        // The hider cannot resist a look.
        stanceB = () => { hider.Lift = peeking; hider.Look(side * 0.5, 0); };
        foreach (var _ in Over(0.9, u => seeker.Look(side * 0.5, 0))) yield return 0;
        foreach (var _ in Over(0.5, u => { seeker.Say(Props.Bang); seeker.Lift = 5 * c * Arc(u); seeker.Look(-side * 0.5, 0); }))
            yield return 0;
        stanceB = null;
        foreach (var _ in Over(0.4, u => hider.Lift = peeking * (1 - Ease(u)))) yield return 0;
        foreach (var _ in Over(1.5, u =>
        {
            seeker.Say(Props.Laugh);
            seeker.Lift = 2 * c * Arc(u * 4 % 1);
            hider.Lift = 2 * c * Arc((u * 4 + 0.5) % 1);
            seeker.Look(-side * 0.5, 0);
            hider.Look(side * 0.5, 0);
        })) yield return 0;
    }

    // ---- Dinner for two ----

    IEnumerable<int> Dinner()
    {
        double c = Cell;
        sa = bots[0].X <= bots[1].X ? 0 : 1;
        Bot a = A, b = B;
        double mid = Math.Clamp((a.X + b.X) / 2, Edge + 9 * c, ActualWidth - Edge - 9 * c);
        foreach (var _ in WalkBoth(mid - 8 * c, mid + 8 * c)) yield return 0;

        string[] menu = { Props.Pizza, Props.Burger, Props.Cake };
        string food = menu[rng.Next(menu.Length)];
        double left = 1;
        sceneDraw = dc => Props.Draw(dc, food, new Point(mid, GroundY - 2.5 * c * left), 5 * c * left);

        foreach (var _ in Over(6, u =>
        {
            Bot eater = (int)(u * 8) % 2 == 0 ? a : b;
            a.Sit();
            b.Sit();
            a.Look(0.5, 0.3);
            b.Look(-0.5, 0.3);
            eater.Tilt((eater == a ? 1 : -1) * 12);
            if ((int)(u * 36) % 2 == 0) eater.MouthOpen();
            left = 1 - 0.7 * u;
        })) yield return 0;
        left = 0;
        foreach (var _ in Over(1.4, u => { a.Sit(); b.Sit(); a.EyesClosed(); b.EyesClosed(); })) yield return 0;
    }
}
