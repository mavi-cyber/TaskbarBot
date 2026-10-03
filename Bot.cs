using System;
using System.Windows;
using System.Windows.Media;

namespace TaskbarBot;

/// <summary>
/// Everything up to Sing is a move a bot picks for itself (the second row are the everyday,
/// human-like ones); Hide and Flee are reactions to the mouse.
/// </summary>
public enum Move
{
    Walk, Dash, Jump, LookAround, Wave, Dance, Sleep, Spin, Bounce, Peek,
    Yawn, Rest, Phone, Sneeze, Workout, Coffee, Read, Eat, Sweep, Call, Sing,
    Hide, Flee,
}

/// <summary>
/// One bot: its position, its pose and its moves. The sprite is a 12 x 8 grid of square cells taken
/// from the reference picture: an 8 x 6 body, two 2 x 2 arms, four 1 x 2 legs and two 1 x 1 eyes.
/// Left alone it idles and picks a move now and then; the stage can also take it over (External)
/// and pose it frame by frame, which is how the icon game is played.
/// </summary>
public sealed class Bot
{
    public const int Cols = 12, Rows = 8;

    /// <summary>How many of the Move values a bot may pick for itself.</summary>
    public const int OwnMoves = (int)Move.Sing + 1;

    static readonly Brush MouthBrush = Frozen(0x1F, 0x1E, 0x1D);
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

    /// <summary>Which mascot this is: its looks and its personality.</summary>
    public Skin Skin { get; }

    /// <summary>Full height from feet to the tip of the ears, in DIPs.</summary>
    public double Height => (6 + Skin.LegHeight + Skin.Top) * Cell;

    /// <summary>0 = wide awake, 1 = can hardly keep its eyes open (night, or nobody at the PC).</summary>
    public double Sleepy { get; set; }

    /// <summary>The PC is playing sound: dancing comes with a musical note.</summary>
    public bool HearsMusic { get; set; }

    // Pose, rebuilt every frame.
    double armL, armR;                      // in rows, negative = raised
    readonly bool[] legUp = new bool[4];
    double eyeDx, eyeDy, eyeOpen = 1;
    double scaleX = 1, scaleY = 1, angle, bob, sway;
    int ghosts;
    bool zzz, faceAway, tongue, mouth;
    string? prop, bubble;                   // something in the hand, something in a speech bubble
    double propDx, propUp, propAngle;

    // Mouse, as last seen by Update.
    double cursorX, hoverCooldown;
    bool cursorNear;

    // Move state.
    Move? current, last, queued;
    bool external;
    double span;                            // how long the current open-ended move lasts
    int stage, dir = 1, armSide = 1, showAll;
    double t, st, fromX, toX, idleLeft, blinkIn = 2, walkT, width;

    // Physics state: vertical speed while bouncing, and the body's squash spring.
    double fallSpeed, spring, springSpeed, lastLift;

    public Bot(Skin skin)
    {
        Skin = skin;
        idleLeft = 1 + rng.NextDouble() * 8;
    }

    /// <summary>Asks the bot to do this move next, after a short pause, if it is free.</summary>
    public void Queue(Move move, double delay)
    {
        if (external || current is not null) return;
        queued = move;
        idleLeft = Math.Min(idleLeft, delay);
    }

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
        if (lastLift > 0.5 && Lift <= 0.01 && Lift > -1 && impact > 0) springSpeed += Math.Min(7, 0.065 * impact);
        lastLift = Lift;

        springSpeed += (-Physics.SpringStiffness * spring - Physics.SpringDamping * springSpeed) * dt;
        spring = Math.Clamp(spring + springSpeed * dt, -0.3, 0.45);
        if (Math.Abs(spring) < 0.002 && Math.Abs(springSpeed) < 0.02) spring = springSpeed = 0;
    }

    /// <summary>Runs every move once, in order, then goes back to picking at random.</summary>
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
        if (showAll is > 0 and < OwnMoves) return (Move)showAll++;
        showAll = 0;
        if (queued is Move asked)
        {
            queued = null;
            return asked;
        }

        // The skin's personality, bent by how sleepy the bot is.
        var odds = new double[OwnMoves];
        double total = 0;
        for (int i = 0; i < OwnMoves; i++)
        {
            double w = i < Skin.Weights.Length ? Skin.Weights[i] : 5;       // moves a skin has no opinion on
            if ((Move)i is Move.Yawn or Move.Rest or Move.Sleep) w *= 1 + 4 * Sleepy;
            if ((Move)i is Move.Dash or Move.Dance or Move.Workout or Move.Spin or Move.Jump) w *= 1 - 0.8 * Sleepy;
            total += odds[i] = w;
        }
        while (true)
        {
            double roll = rng.NextDouble() * total;
            int i = 0;
            while (i < OwnMoves - 1 && roll >= odds[i]) roll -= odds[i++];
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
        span = 4.5 + rng.NextDouble() * 3;
        switch (move)
        {
            case Move.Walk: PickTarget(20 * Cell, 90 * Cell); break;
            case Move.Dash: PickTarget(0.3 * width, 0.7 * width); break;
            case Move.Peek: PickTarget(40 * Cell, 0.5 * width); break;
            case Move.Sweep: PickTarget(15 * Cell, 45 * Cell); break;
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
        zzz = faceAway = tongue = mouth = false;
        prop = bubble = null;
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
    public void MouthOpen() => mouth = true;

    /// <summary>Shows an emoji in a speech bubble spot above the head for this frame.</summary>
    public void Say(string emoji) => bubble = emoji;

    /// <summary>Shows an emoji held dx cells from the middle and up cells above the feet.</summary>
    public void Hold(string emoji, double dx, double up, double tilt = 0)
    {
        prop = emoji;
        propDx = dx;
        propUp = up;
        propAngle = tilt;
    }
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
        new(X, groundY - Lift - Height * scaleY - size / 2 - 1);

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
                if (HearsMusic) bubble = Props.Note;
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
            // ---- Everyday, human-like moves ----

            case Move.Yawn:
            {
                double u = Clamp01(t / 2.4), open = Math.Sin(Math.PI * u);
                mouth = open > 0.3;
                eyeOpen = 1 - 0.85 * Math.Min(1, open * 2);
                armL = armR = open > 0.5 ? -2 : open > 0.25 ? -1 : 0;
                Stretch(0.6 * open);
                return u >= 1;
            }

            case Move.Rest:
            {
                // Sits down and watches the world go by.
                Sit();
                double look = Math.Sin(t * 0.9);
                eyeDx = look > 0.4 ? 0.5 : look < -0.4 ? -0.5 : 0;
                if (t % 3.1 < 0.12) eyeOpen = 0.15;
                return t >= span;
            }

            case Move.Phone:
            {
                // Scrolls its phone, and something on it is funny.
                Hold(Props.Phone, armSide * 6.5, 4.2, armSide * 8);
                if (armSide < 0) armL = -1; else armR = -1;
                eyeDx = armSide * 0.5;
                eyeDy = 0.3;
                Lift = t > 3 && t < 3.8 ? 1.5 * c * Arc((t - 3) / 0.4 % 1) : 0;
                return t >= span;
            }

            case Move.Sneeze:
            {
                Lift = 0;
                if (t < 0.9)
                {
                    double u = t / 0.9;                     // aah...
                    angle = -armSide * 9 * u;
                    eyeOpen = 1 - 0.85 * u;
                    mouth = u > 0.5;
                    armL = armR = -1;
                }
                else if (t < 1.15)
                {
                    double u = (t - 0.9) / 0.25;            // ...choo
                    angle = armSide * 16 * (1 - u);
                    Squash(1 - u);
                    eyeOpen = 0.15;
                    Lift = 2 * c * Arc(u);
                }
                else if (t < 1.5) eyeOpen = 0.15;
                return t >= 1.9;
            }

            case Move.Workout:
            {
                // Jumping jacks.
                bool up = (int)(t * 4) % 2 == 0;
                armL = armR = up ? -2 : 1;
                Lift = 2 * c * Arc(t * 2 % 1);
                return t >= 4;
            }

            case Move.Coffee:
            {
                bool sipping = t % 1.8 > 1.2;
                Hold(Props.Coffee, armSide * (sipping ? 4 : 6.5), sipping ? 5.5 : 4);
                double arm = sipping ? -2 : -1;
                if (armSide < 0) armL = arm; else armR = arm;
                if (sipping)
                {
                    eyeOpen = 0.15;
                    angle = -armSide * 5;
                }
                return t >= span;
            }

            case Move.Read:
            {
                Sit();
                Hold(Props.Book, 0, 2.4);
                armL = armR = 1;
                bool lookUp = t % 4 > 3.4;                  // glances up from the page now and then
                eyeDy = lookUp ? -0.2 : 0.3;
                eyeDx = lookUp ? 0 : Math.Sin(t * 2.5) > 0 ? 0.3 : -0.3;
                return t >= span;
            }

            case Move.Eat:
            {
                Hold(Props.Burger, armSide * 4.5, 4.6);
                if (armSide < 0) armL = -1; else armR = -1;
                mouth = (int)(t * 5) % 2 == 0;
                eyeDy = 0.2;
                return t >= span;
            }

            case Move.Sweep:
            {
                // Sweeps its way along the taskbar.
                X += dir * 5 * c * dt;
                StepInPlace(dt);
                double stroke = Math.Sin(t * 7);
                Hold(Props.Broom, dir * (6 + stroke), 3.2, dir * (20 + 15 * stroke));
                eyeDx = dir * 0.5;
                eyeDy = 0.3;
                return dir > 0 ? X >= toX : X <= toX;
            }

            case Move.Call:
            {
                // On the phone, pacing up and down.
                int way = (int)(t / 2.2) % 2 == 0 ? dir : -dir;
                X = ClampX(X + way * 5 * c * dt);
                StepInPlace(dt);
                Hold(Props.Handset, armSide * 4.5, 6.2, armSide * -20);
                if (armSide < 0) armL = -2; else armR = -2;
                mouth = (int)(t * 4) % 3 == 0;
                eyeDx = way * 0.5;
                return t >= span + 2;
            }

            case Move.Sing:
            {
                Hold(Props.Mic, armSide * 4.2, 5);
                if (armSide < 0) armL = -1; else armR = -1;
                mouth = Math.Sin(t * 6) > -0.2;
                bubble = Props.Note;
                sway = Math.Sin(t * 3) * c;
                eyeOpen = Math.Sin(t * 1.3) > 0.6 ? 0.15 : 1;
                return t >= span;
            }

            case Move.Hide:
            {
                double hidden = -(Height + 6);
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
                double hidden = -(Height + 6);
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
        h.Add(mouth); h.Add(prop); h.Add(bubble); h.Add(propDx); h.Add(propUp);
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
        if (prop is not null)
            Props.Draw(dc, prop, new Point(X + sway + propDx * Cell, feetY - propUp * Cell), 4 * Cell, propAngle);
        if (bubble is not null)
            Props.Draw(dc, bubble, new Point(X + 5 * Cell, feetY - Height - 2.5 * Cell), 4.5 * Cell);
    }

    void DrawSprite(DrawingContext dc, double cx, double feetY, double opacity)
    {
        Skin skin = Skin;
        double c = Cell, w = Cols * c, h = Rows * c;
        int legs = skin.Legs.Length;
        bool sitting = true;
        for (int i = 0; i < legs; i++) sitting &= legUp[i];
        double tucked = skin.LegHeight >= 2 ? skin.LegHeight - 1 : skin.LegHeight * 0.4;

        dc.PushOpacity(opacity);
        dc.PushTransform(new TranslateTransform(Math.Round(cx), Math.Round(feetY - bob)));
        dc.PushTransform(new RotateTransform(angle, 0, -h / 2));
        dc.PushTransform(new ScaleTransform(scaleX * (1 + 0.6 * spring), scaleY * (1 - spring)));

        // Local space: origin at the feet, x in [-w/2, w/2]; the body's top edge is at oy.
        double ox = -w / 2, oy = -(6 + (sitting ? tucked : skin.LegHeight)) * c;
        void Block(Brush brush, double x, double y, double bw, double bh) =>
            dc.DrawRectangle(brush, null, new Rect(ox + x * c, oy + y * c, bw * c, bh * c));

        foreach (Skin.Part p in skin.Behind) Block(p.Brush, p.X, p.Y, p.W, p.H);
        if (skin.Round)
        {
            Block(skin.Body, 3, 0, 6, 1.1);
            Block(skin.Body, 2, 1, 8, 5);
        }
        else Block(skin.Body, 2, 0, 8, 6);
        dc.DrawRectangle(skin.Body, null, new Rect(ox, oy + (2 + armL) * c, 2 * c + 1, 2 * c));
        dc.DrawRectangle(skin.Body, null, new Rect(ox + 10 * c - 1, oy + (2 + armR) * c, 2 * c + 1, 2 * c));
        for (int i = 0; i < legs; i++)
            dc.DrawRectangle(skin.Leg ?? skin.Body, null, new Rect(ox + skin.Legs[i].X * c, oy + 6 * c - 1,
                skin.Legs[i].W * c, (legUp[i] ? tucked : skin.LegHeight) * c + 1));

        if (!faceAway)
        {
            foreach (Skin.Part p in skin.Face) Block(p.Brush, p.X, p.Y, p.W, p.H);
            double eh = skin.EyeHeight * eyeOpen;
            double ey = skin.EyeY + eyeDy * skin.EyeTravel + (skin.EyeHeight - eh) / 2;
            double ex = eyeDx * skin.EyeTravel;
            Block(skin.Eye, 3 + ex, ey, 1, eh);
            Block(skin.Eye, 8 + ex, ey, 1, eh);
            if (mouth) Block(MouthBrush, 5.3, 2.7, 1.4, 1.5);
            if (tongue)
            {
                Block(MouthBrush, 5, 2.4, 2, 0.5);
                Block(TongueBrush, 5.4, 2.9, 1.2, 1.5);
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
