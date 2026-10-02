using System;
using System.Windows;

namespace TaskbarBot;

/// <summary>
/// The few bits of mechanics the bots' world runs on. Lengths are in sprite cells where a constant
/// says so (multiply by Bot.Cell for DIPs), so the motion looks the same at every bot size.
/// </summary>
static class Physics
{
    /// <summary>Downward acceleration, cells per second squared. One value for everything that falls.</summary>
    public const double Gravity = 220;

    /// <summary>Coefficient of restitution: the share of its speed a thing keeps when it bounces.</summary>
    public const double BotBounce = 0.7, TrashBounce = 0.55;

    /// <summary>Friction coefficients: deceleration on the ground is coefficient * gravity.</summary>
    public const double Friction = 0.5, RollingFriction = 0.16;

    /// <summary>The bots' squashy bodies behave like a damped spring: a = -k x - c v.</summary>
    public const double SpringStiffness = 400, SpringDamping = 10;
}

/// <summary>
/// A projectile path between two points that peaks a given height above the higher one.
/// Screen y grows downward, so "up" is negative.
/// </summary>
readonly struct Throw
{
    readonly Point from;
    readonly double g;

    public Vector Velocity { get; }      // at launch
    public double Time { get; }          // seconds until it arrives

    Throw(Point from, Vector velocity, double g, double time)
    {
        this.from = from;
        this.g = g;
        Velocity = velocity;
        Time = time;
    }

    public static Throw Between(Point from, Point to, double apexAbove, double g)
    {
        double top = Math.Min(from.Y, to.Y) - apexAbove;
        double rise = Math.Sqrt(2 * (from.Y - top) / g);        // h = g t^2 / 2, solved for t
        double fall = Math.Sqrt(2 * (to.Y - top) / g);
        double time = rise + fall;
        return new Throw(from, new Vector((to.X - from.X) / time, -g * rise), g, time);
    }

    /// <summary>Position t seconds after launch: s = v t + g t^2 / 2.</summary>
    public Point At(double t) =>
        new(from.X + Velocity.X * t, from.Y + Velocity.Y * t + 0.5 * g * t * t);
}

/// <summary>A loose object: it falls, bounces off the ground losing energy, then rolls to rest.</summary>
sealed class Body
{
    public Point Pos { get; set; }
    public Vector Vel { get; set; }
    public double Radius { get; set; }
    public double Angle { get; private set; }       // degrees
    public double Spin { get; set; }                // degrees per second while in the air
    public bool Bounced { get; private set; }
    public bool Resting { get; private set; }

    public void Step(double dt, double g, double ground, double restitution, double friction)
    {
        if (Resting) return;

        bool onGround = Vel.Y == 0 && Pos.Y + Radius >= ground - 0.01;
        if (!onGround)
        {
            Vel = new Vector(Vel.X, Vel.Y + g * dt);
            Pos += Vel * dt;
            Angle += Spin * dt;
            if (Pos.Y + Radius >= ground && Vel.Y > 0)
            {
                // Bounce: the vertical speed is reversed and scaled by the restitution.
                // Too slow to leave the ground again and it stays down.
                double up = Vel.Y * restitution;
                Pos = new Point(Pos.X, ground - Radius);
                Vel = new Vector(Vel.X * 0.75, up < g * 0.05 ? 0 : -up);
                Bounced = true;
            }
        }
        else
        {
            // Friction takes speed off at a constant rate; rolling without slipping turns it.
            double slow = friction * g * dt;
            double vx = Math.Abs(Vel.X) <= slow ? 0 : Vel.X - Math.Sign(Vel.X) * slow;
            Vel = new Vector(vx, 0);
            Pos = new Point(Pos.X + vx * dt, ground - Radius);
            Angle += vx * dt / Radius * 180 / Math.PI;
            if (vx == 0) Resting = true;
        }
    }
}
