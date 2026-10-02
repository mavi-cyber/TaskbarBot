using System;
using System.Windows;
using System.Windows.Media;

namespace TaskbarBot;

/// <summary>The first ten are the moves a bot picks from; Hide and Flee are reactions to the mouse.</summary>
public enum Move { Walk, Dash, Jump, LookAround, Wave, Dance, Sleep, Spin, Bounce, Peek, Hide, Flee }

/// <summary>
/// One bot: its position, its pose and its moves. The sprite is a 12 x 8 grid of square cells taken
/// from the reference picture: an 8 x 6 body, two 2 x 2 arms, four 1 x 2 legs and two 1 x 1 eyes.
/// Left alone it idles and picks a move now and then; the stage can also take it over (External)
/// and pose it frame by frame, which is how the icon game is played.
/// </summary>
public sealed class Bot
{
    public const int Cols = 12, Rows = 8;
    static readonly int[] LegCols = { 2, 4, 7, 9 };

    // Relative odds of each move, in enum order. Calm ones are common, showy ones are rare.
    static readonly int[] Weights = { 34, 8, 8, 16, 6, 5, 4, 5, 6, 8 };

    static readonly Brush BodyBrush = Frozen(0xD9, 0x77, 0x57);
    static readonly Brush EyeBrush = Frozen(0x1F, 0x1E, 0x1D);
    static readonly Brush ZBrush = Frozen(0xF4, 0xF1, 0xEA);
    static readonly Brush TongueBrush = Frozen(0xF0, 0x5C, 0x8A);

    readonly Random rng = new();

    /// <summary>Size of one sprite cell in DIPs.</summary>
    public double Cell { get; set; } = 5;

    /// <summary>Centre x of the bot.</summary>
    public double X { get; set; } = double.NaN;

    /// <summary>Height of the feet above the ground (negative = sunk behind the taskbar).</summary>
    public double Lift { get; set; }

    /// <summary>Where the other bot stands, so walks do not end on top of it.</summary>
    public Func<double>? OtherX { get; set; }

    public bool IsIdle => current is null && !external;

    // Pose, rebuilt every frame.
    double armL, armR;                      // in rows, negative = raised
    readonly bool[] legUp = new bool[4];
    double eyeDx, eyeDy, eyeOpen = 1;
    double scaleX = 1, scaleY = 1, angle, bob, sway;
    int ghosts;
    bool zzz, faceAway, tongue;

    // Mouse, as last seen by Update.
    double cursorX, hoverCooldown;
    bool cursorNear;

    // Move state.
    Move? current, last;
    bool external;
    int stage, dir = 1, armSide = 1, showAll;
    double t, st, fromX, toX, idleLeft, blinkIn = 2, walkT, width;

    // Physics state: vertical speed while bouncing, and the body's squash spring.
    double fallSpeed, spring, springSpeed, lastLift;

    public Bot() => idleLeft = 1 + rng.NextDouble() * 6;

    double Margin => Cols * Cell / 2 + 8;

    bool EnsurePlaced(double stageWidth)
    {
        if (stageWidth <= 0) return false;
        width = stageWidth;
        if (double.IsNaN(X)) X = Margin + rng.NextDouble() * (width - 2 * Margin);
        return true;
    }

    double ClampX(double v) => Math.Clamp(v, Margin, Math.Max(Margin, width - Margin));

    public void Play(Move move)
    {
        if (external || width <= 0) return;
        Lift = 0;
        Start(move);
    }

    /// <summary>
    /// Call once per frame after the bot has been posed. A landing kicks the body's spring in
    /// proportion to the speed it hit the ground at; the spring then rings and dies away
    /// (a = -k x - c v), which is what makes every landing squash and wobble.
    /// </summary>
    public void EndFrame(double dt)
    {
        if (dt <= 0) return;
        double impact = (lastLift - Lift) / dt / Cell;              // cells per second, downward
        if (lastLift > 0.5 && Lift <= 0.01 && impact > 0) springSpeed += Math.Min(7, 0.065 * impact);
        lastLift = Lift;

        springSpeed += (-Physics.SpringStiffness * spring - Physics.SpringDamping * springSpeed) * dt;
        spring = Math.Clamp(spring + springSpeed * dt, -0.3, 0.45);
        if (Math.Abs(spring) < 0.002 && Math.Abs(springSpeed) < 0.02) spring = springSpeed = 0;
    }

    /// <summary>Runs all ten moves once, in order, then goes back to picking at random.</summary>
    public void PlayAll()
    {
        showAll = 1;
        Play(Move.Walk);
    }

    public void CancelMove()
    {
        current = null;
        Lift = 0;
        showAll = 0;
    }

    public void Update(double dt, double stageWidth, Point cursor, double groundY)
    {
        if (!EnsurePlaced(stageWidth)) return;

        // The mouse touching a bot makes it duck behind the taskbar or bolt.
        double c = Cell;
        cursorX = cursor.X;
        cursorNear = Math.Abs(cursor.X - X) < 11 * c && cursor.Y > groundY - 16 * c;
        hoverCooldown -= dt;
        bool touched = Lift >= 0 && Math.Abs(cursor.X - X) < 7 * c
            && cursor.Y > groundY - Lift - 9 * c && cursor.Y < groundY;
        if (touched && hoverCooldown <= 0)
        {
            hoverCooldown = 2;
            showAll = 0;
            Lift = 0;
            Start(rng.Next(2) == 0 ? Move.Hide : Move.Flee);
        }

        ResetPose();
        if (current is null && (X < Margin - 1 || X > width - Margin + 1))
        {
            WalkToward(ClampX(X), dt);      // left outside the stage by a scene: stroll back in
            return;
        }
        if (current is Move m)
        {
            t += dt;
            st += dt;
            if (Step(m, dt))
            {
                current = null;
                Lift = 0;
                ResetPose();
                // Stand around for a while between moves instead of chaining them.
                idleLeft = showAll > 0 ? 0.4 : 2.5 + rng.NextDouble() * 7;
            }
        }
        else
        {
            Blink(dt);
            idleLeft -= dt;
            if (idleLeft <= 0) Start(PickMove());
        }
    }

    void Blink(double dt)
    {
        blinkIn -= dt;
        if (blinkIn < 0) eyeOpen = 0.15;
        if (blinkIn < -0.12) blinkIn = 1.5 + rng.NextDouble() * 3.5;
    }

    Move PickMove()
    {
        if (showAll is > 0 and < 10) return (Move)showAll++;
        showAll = 0;

        int total = 0;
        foreach (int w in Weights) total += w;
        while (true)
        {
            int roll = rng.Next(total), i = 0;
            while (roll >= Weights[i]) roll -= Weights[i++];
            if ((Move)i != last || i == (int)Move.Walk) return (Move)i;
        }
    }

    void Start(Move move)
    {
        current = last = move;
        t = st = 0;
        stage = 0;
        fromX = X;
        armSide = rng.Next(2) == 0 ? -1 : 1;
        switch (move)
        {
            case Move.Walk: PickTarget(20 * Cell, 90 * Cell); break;
            case Move.Dash: PickTarget(0.3 * width, 0.7 * width); break;
            case Move.Peek: PickTarget(40 * Cell, 0.5 * width); break;
            case Move.Flee:
                // Run from the mouse; it is a dash without the wind-up.
                dir = cursorX <= X ? 1 : -1;
                if (dir > 0 ? width - Margin - X < 25 * Cell : X - Margin < 25 * Cell) dir = -dir;
                toX = ClampX(X + dir * (45 + rng.NextDouble() * 40) * Cell);
                current = last = Move.Dash;
                stage = 1;
                break;
            default: dir = rng.Next(2) == 0 ? -1 : 1; break;
        }
    }

    void PickTarget(double min, double max)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            double d = min + rng.NextDouble() * Math.Max(0, max - min);
            dir = rng.Next(2) == 0 ? -1 : 1;
            if (X + dir * d < Margin || X + dir * d > width - Margin) dir = -dir;
            toX = ClampX(X + dir * d);
            if (OtherX is null || Math.Abs(toX - OtherX()) > 16 * Cell) break;
        }
        if (toX == X) dir = 1;
    }

    void ResetPose()
    {
        armL = armR = 0;
        SetLegs(false, false, false, false);
        eyeDx = eyeDy = 0;
        eyeOpen = 1;
        scaleX = scaleY = 1;
        angle = bob = sway = 0;
        ghosts = 0;
        zzz = faceAway = tongue = false;
    }

    void SetLegs(bool a, bool b, bool c, bool d)
    {
        legUp[0] = a; legUp[1] = b; legUp[2] = c; legUp[3] = d;
    }

    void Next() { stage++; st = 0; }

    static double Clamp01(double u) => Math.Clamp(u, 0, 1);
    static double Ease(double u) { u = Clamp01(u); return u * u * (3 - 2 * u); }
    static double Lerp(double a, double b, double u) => a + (b - a) * u;
    static double Arc(double u) => 4 * u * (1 - u);

    // ---- Posing by the stage (External mode) ----

    /// <summary>While true the bot does nothing on its own; the stage poses it every frame.</summary>
    public bool External
    {
        get => external;
        set
        {
            external = value;
            current = null;
            Lift = 0;
            ResetPose();
            if (!value) idleLeft = 1.5 + rng.NextDouble() * 4;
        }
    }

    /// <summary>Call first each frame in External mode: back to the neutral standing pose.</summary>
    public void BeginPose(double dt, double stageWidth)
    {
        EnsurePlaced(stageWidth);
        ResetPose();
        Lift = 0;
        Blink(dt);
    }

    /// <summary>Takes one walking step towards target. Returns true once the bot is there.</summary>
    public bool WalkToward(double target, double dt, double speed = 22, bool clamp = true)
    {
        if (clamp) target = ClampX(target);
        double step = speed * Cell * dt;
        if (Math.Abs(target - X) <= step)
        {
            X = target;
            return true;
        }
        dir = target > X ? 1 : -1;
        X += dir * step;
        StepInPlace(dt);
        eyeDx = dir * 0.5;
        return false;
    }

    /// <summary>Legs going without the bot getting anywhere.</summary>
    public void StepInPlace(double dt)
    {
        walkT += dt;
        bool ph = (int)(walkT * 10) % 2 == 0;
        SetLegs(ph, !ph, ph, !ph);
        bob = ph ? 0 : 1;
    }

    /// <summary>Sets the body wobbling, as after a landing.</summary>
    public void Kick(double amount) => springSpeed += amount;

    public void ArmsUp() => armL = armR = -2;
    public void Arms(double left, double right) { armL = left; armR = right; }
    public void Look(double dx, double dy) { eyeDx = dx; eyeDy = dy; }
    public void EyesClosed() => eyeOpen = 0.15;
    public void Sit() => SetLegs(true, true, true, true);
    public void Tilt(double degrees) => angle = degrees;
    public void Shake(double dips) => sway = dips;
    public void FaceAway() => faceAway = true;
    public void Tongue() => tongue = true;
    public void Ghosts(int count, int direction) { ghosts = count; dir = direction; }

    /// <summary>0 = normal, 1 = pressed flat like a pancake.</summary>
    public void Flatten(double amount)
    {
        scaleY = 1 - 0.82 * amount;
        scaleX = 1 + 0.45 * amount;
    }

    public void Squash(double amount)
    {
        scaleY = 1 - 0.25 * amount;
        scaleX = 1 + 0.15 * amount;
    }

    public void Stretch(double amount)
    {
        scaleY = 1 + 0.14 * amount;
        scaleX = 1 - 0.08 * amount;
    }

    public void LookAt(Point p, double groundY)
    {
        eyeDx = p.X > X + Cell ? 0.5 : p.X < X - Cell ? -0.5 : 0;
        eyeDy = p.Y < groundY - Rows * Cell ? -0.3 : 0.25;
    }

    /// <summary>Centre of something of the given size carried just above the bot's head.</summary>
    public Point HoldPoint(double groundY, double size) =>
        new(X, groundY - Lift - Rows * Cell * scaleY - size / 2 - 1);

    // ---- The ten moves ----

    /// <summary>Advances the current move one frame. Returns true when the move has finished.</summary>
    bool Step(Move move, double dt)
    {
        double c = Cell;
        switch (move)
        {
            case Move.Walk:
            {
                X += dir * 9 * c * dt;
                bool ph = (int)(t * 7) % 2 == 0;
                SetLegs(ph, !ph, ph, !ph);
                bob = ph ? 0 : 1;
                eyeDx = dir * 0.5;
                return dir > 0 ? X >= toX : X <= toX;
            }

            case Move.Dash:
            {
                eyeDx = dir * 0.5;
                if (stage == 0)
                {
                    double u = Clamp01(st / 0.3);
                    angle = -dir * 10 * u;
                    Squash(0.5 * u);
                    if (u >= 1) Next();
                }
                else if (stage == 1)
                {
                    X += dir * 90 * c * dt;
                    angle = dir * 12;
                    bool ph = (int)(t * 18) % 2 == 0;
                    SetLegs(ph, !ph, ph, !ph);
                    ghosts = 2;
                    if (dir > 0 ? X >= toX : X <= toX) Next();
                }
                else
                {
                    double u = Clamp01(st / 0.3);
                    X += dir * 25 * c * (1 - u) * dt;
                    angle = -dir * 14 * (1 - u);
                    return u >= 1;
                }
                return false;
            }

            case Move.Jump:
            {
                // Crouch, then a ballistic jump: take-off speed v = sqrt(2 g h), height = v t - g t^2 / 2.
                if (stage == 0)
                {
                    double u = Clamp01(st / 0.18);
                    Squash(u);
                    if (u >= 1) Next();
                    return false;
                }
                double g = Physics.Gravity * c, v = Math.Sqrt(2 * g * 14 * c);
                Lift = Math.Max(0, v * st - 0.5 * g * st * st);
                ArmsUp();
                Stretch(Math.Max(0, 1 - st * 4));
                eyeDy = st < v / g ? -0.3 : 0.3;
                return st > 0.05 && Lift <= 0;
            }
            case Move.LookAround:
            {
                if (t < 0.9) eyeDx = -0.5;
                else if (t < 1.8) eyeDx = 0.5;
                else if (t < 2.4) eyeDy = -0.35;
                if ((t > 2.6 && t < 2.72) || (t > 2.95 && t < 3.07)) eyeOpen = 0.15;
                return t >= 3.4;
            }

            case Move.Wave:
            {
                double arm = (int)(t * 6) % 2 == 0 ? -2 : -1;
                if (armSide < 0) armL = arm; else armR = arm;
                angle = armSide * 3 * Math.Sin(t * 12);
                eyeDy = -0.2;
                return t >= 2.4;
            }

            case Move.Dance:
            {
                bool ph = (int)(t * 4.4) % 2 == 0;
                armL = ph ? -2 : 2;
                armR = ph ? 2 : -2;
                SetLegs(ph, ph, !ph, !ph);
                sway = (ph ? -1 : 1) * c;
                angle = ph ? -6 : 6;
                bob = 2 * Math.Abs(Math.Sin(t * 4.4 * Math.PI));
                eyeDx = ph ? -0.5 : 0.5;
                return t >= 4.5;
            }

            case Move.Sleep:
            {
                armL = armR = 1;
                if (stage == 0)
                {
                    double u = Clamp01(st / 0.5);
                    eyeOpen = 1 - 0.85 * u;
                    if (u > 0.5) SetLegs(true, true, true, true);
                    if (u >= 1) Next();
                }
                else if (stage == 1)
                {
                    SetLegs(true, true, true, true);
                    eyeOpen = 0.15;
                    double breath = Math.Sin(st * 2.2);
                    scaleY = 0.96 + 0.04 * breath;
                    scaleX = 1.02 - 0.02 * breath;
                    zzz = true;
                    if (st >= 5.5) Next();
                }
                else
                {
                    double u = Clamp01(st / 0.45);
                    armL = armR = 0;
                    Lift = 3 * c * Arc(u);
                    return u >= 1;
                }
                return false;
            }

            case Move.Spin:
            {
                // A somersault timed to the jump: the time in the air is 2 v / g.
                if (stage == 0)
                {
                    double u = Clamp01(st / 0.15);
                    Squash(u);
                    if (u >= 1) Next();
                    return false;
                }
                double g = Physics.Gravity * c, v = Math.Sqrt(2 * g * 10 * c), air = 2 * v / g;
                Lift = Math.Max(0, v * st - 0.5 * g * st * st);
                angle = dir * 360 * Clamp01(st / air);
                return st >= air;
            }

            case Move.Bounce:
            {
                // Like a dropped ball: every landing keeps only part of the speed (restitution),
                // so each bounce is lower and shorter than the one before.
                double g = Physics.Gravity * c;
                if (stage == 0)
                {
                    double u = Clamp01(st / 0.15);
                    Squash(u);
                    if (u >= 1)
                    {
                        fallSpeed = Math.Sqrt(2 * g * 13 * c);
                        Next();
                    }
                    return false;
                }
                fallSpeed -= g * dt;
                Lift += fallSpeed * dt;
                Stretch(Math.Min(1, Math.Abs(fallSpeed) / (80 * c)));
                if (Lift <= 0 && fallSpeed < 0)
                {
                    Lift = 0;
                    fallSpeed = -fallSpeed * Physics.BotBounce;
                    if (fallSpeed < 25 * c) return true;
                }
                return false;
            }
            case Move.Hide:
            {
                double hidden = -(Rows * c + 6);
                double peeking = -(Rows - 2.4) * c;
                eyeDx = cursorX > X ? 0.5 : -0.5;
                if (stage == 0)
                {
                    double u = Clamp01(st / 0.18);
                    Lift = hidden * Ease(u);
                    if (u >= 1) Next();
                }
                else if (stage == 1)
                {
                    Lift = hidden;
                    if (st > 1.2 && !cursorNear) Next();
                }
                else if (stage == 2)
                {
                    double u = Clamp01(st / 0.35);
                    Lift = Lerp(hidden, peeking, Ease(u));
                    if (u >= 1) Next();
                }
                else if (stage == 3)
                {
                    // Eyes over the edge: is the coast clear?
                    Lift = peeking;
                    if (cursorNear) { stage = 1; st = 0; }
                    else if (st > 1) Next();
                }
                else
                {
                    double u = Clamp01(st / 0.3);
                    Lift = Lerp(peeking, 0, Ease(u));
                    return u >= 1;
                }
                return false;
            }

            case Move.Peek:
            {
                double hidden = -(Rows * c + 6);
                double peeking = -(Rows - 2.4) * c;
                if (stage == 0)
                {
                    double u = Clamp01(st / 0.5);
                    Lift = hidden * Ease(u);
                    eyeDy = 0.3;
                    if (u >= 1) Next();
                }
                else if (stage == 1)
                {
                    double u = Clamp01(st / 0.7);
                    Lift = hidden;
                    X = Lerp(fromX, toX, Ease(u));
                    if (u >= 1) Next();
                }
                else if (stage == 2)
                {
                    double u = Clamp01(st / 0.45);
                    Lift = Lerp(hidden, peeking, Ease(u));
                    if (u >= 1) Next();
                }
                else if (stage == 3)
                {
                    double u = Clamp01(st / 1.8);
                    Lift = peeking;
                    eyeDx = u < 0.4 ? -0.5 : u < 0.8 ? 0.5 : 0;
                    if (u > 0.86 && u < 0.93) eyeOpen = 0.15;
                    if (u >= 1) Next();
                }
                else
                {
                    double u = Clamp01(st / 0.4);
                    Lift = Lerp(peeking, 0, Ease(u)) + 5 * c * Math.Sin(Math.PI * u);
                    ArmsUp();
                    return u >= 1;
                }
                return false;
            }
        }
        return true;
    }

    public int PoseHash()
    {
        var h = new HashCode();
        h.Add(X); h.Add(Lift); h.Add(armL); h.Add(armR);
        foreach (bool up in legUp) h.Add(up);
        h.Add(eyeDx); h.Add(eyeDy); h.Add(eyeOpen);
        h.Add(scaleX); h.Add(scaleY); h.Add(angle); h.Add(bob); h.Add(sway);
        h.Add(ghosts); h.Add(Cell); h.Add(faceAway); h.Add(tongue); h.Add(spring);
        if (zzz) h.Add(st);
        return h.ToHashCode();
    }

    // ---- Drawing ----

    public void Draw(DrawingContext dc, double groundY)
    {
        if (double.IsNaN(X)) return;
        double feetY = groundY - Lift;
        for (int g = ghosts; g >= 1; g--)
            DrawSprite(dc, X + sway - dir * g * 4 * Cell, feetY, 0.36 / g);
        DrawSprite(dc, X + sway, feetY, 1);
        if (zzz) DrawZs(dc, groundY);
    }

    void DrawSprite(DrawingContext dc, double cx, double feetY, double opacity)
    {
        double c = Cell, w = Cols * c, h = Rows * c;
        bool sitting = legUp[0] && legUp[1] && legUp[2] && legUp[3];

        dc.PushOpacity(opacity);
        dc.PushTransform(new TranslateTransform(Math.Round(cx), Math.Round(feetY - bob)));
        dc.PushTransform(new RotateTransform(angle, 0, -h / 2));
        dc.PushTransform(new ScaleTransform(scaleX * (1 + 0.6 * spring), scaleY * (1 - spring)));

        // Local space: origin at the feet, sprite spans x in [-w/2, w/2] and y in [-h, 0].
        double ox = -w / 2, oy = -h + (sitting ? c : 0);
        dc.DrawRectangle(BodyBrush, null, new Rect(ox + 2 * c, oy, 8 * c, 6 * c));
        dc.DrawRectangle(BodyBrush, null, new Rect(ox, oy + (2 + armL) * c, 2 * c + 1, 2 * c));
        dc.DrawRectangle(BodyBrush, null, new Rect(ox + 10 * c - 1, oy + (2 + armR) * c, 2 * c + 1, 2 * c));
        for (int i = 0; i < 4; i++)
            dc.DrawRectangle(BodyBrush, null,
                new Rect(ox + LegCols[i] * c, oy + 6 * c - 1, c, (legUp[i] ? c : 2 * c) + 1));

        if (!faceAway)
        {
            double eh = c * eyeOpen;
            double ey = oy + (1 + eyeDy) * c + (c - eh) / 2;
            dc.DrawRectangle(EyeBrush, null, new Rect(ox + (3 + eyeDx) * c, ey, c, eh));
            dc.DrawRectangle(EyeBrush, null, new Rect(ox + (8 + eyeDx) * c, ey, c, eh));
            if (tongue)
            {
                dc.DrawRectangle(EyeBrush, null, new Rect(ox + 5 * c, oy + 2.4 * c, 2 * c, 0.5 * c));
                dc.DrawRectangle(TongueBrush, null, new Rect(ox + 5.4 * c, oy + 2.9 * c, 1.2 * c, 1.5 * c));
            }
        }

        dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
    }

    void DrawZs(DrawingContext dc, double groundY)
    {
        double c = Cell;
        double headX = X + 3 * c, headY = groundY - Rows * c;
        for (int k = 0; k < 3; k++)
        {
            double p = (st * 0.4 + k / 3.0) % 1;
            double zc = Math.Max(1.5, c * (0.3 + 0.4 * p));
            double zx = Math.Round(headX + p * 6 * c + Math.Sin(p * 6) * c * 0.6);
            double zy = Math.Round(headY - c - p * 8 * c);
            dc.PushOpacity(Math.Sin(Math.PI * p));
            dc.DrawRectangle(ZBrush, null, new Rect(zx, zy, 3 * zc, zc));
            dc.DrawRectangle(ZBrush, null, new Rect(zx + zc, zy + zc, zc, zc));
            dc.DrawRectangle(ZBrush, null, new Rect(zx, zy + 2 * zc, 3 * zc, zc));
            dc.Pop();
        }
    }

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
