using System.Windows.Media;

namespace TaskbarBot;

/// <summary>
/// What one mascot looks like and how it tends to behave. Every mascot is built on the same
/// moving parts (an 8 x 6 body, two arms, legs, two eyes), so all moves and scenes work for all
/// of them; a skin only adds colours, extra blocks such as ears, and a personality.
/// Positions are in sprite cells: x 0..12 across, y 0 at the top of the body, negative above it.
/// </summary>
public sealed class Skin
{
    public sealed record Part(double X, double Y, double W, double H, Brush Brush);

    public required string Name { get; init; }
    public required Brush Body { get; init; }
    public Brush? Leg { get; init; }                        // null = same as the body
    public Brush Eye { get; init; } = Hex(0x1F1E1D);

    /// <summary>Left edge and width of each leg (two or four of them).</summary>
    public (double X, double W)[] Legs { get; init; } = { (2, 1), (4, 1), (7, 1), (9, 1) };
    public double LegHeight { get; init; } = 2;

    /// <summary>Blocks drawn behind the body (ears, antenna) and on the face (nose, visor, eye whites).</summary>
    public Part[] Behind { get; init; } = { };
    public Part[] Face { get; init; } = { };

    public double EyeY { get; init; } = 1;
    public double EyeHeight { get; init; } = 1;
    public double EyeTravel { get; init; } = 1;             // how far the pupils may wander
    public double Top { get; init; }                        // cells of ears or antenna above the body
    public bool Round { get; init; }                        // top corners cut off

    /// <summary>
    /// Relative odds of each move, in Move order up to Read. This is the personality:
    /// the cat naps, the frog jumps, the ghost hides.
    /// </summary>
    public required int[] Weights { get; init; }

    //                        Walk Dash Jump Look Wave Dance Sleep Spin Bounce Peek Yawn Rest Phone Sneeze Workout Coffee Read
    static int[] W(params int[] w) => w;

    public static readonly Skin[] All =
    {
        new()
        {
            Name = "Bolt", Body = Hex(0x4FA3D9), Eye = Hex(0x9FF3FF), Top = 3,
            Legs = new[] { (3.0, 2.0), (7.0, 2.0) },
            Behind = new Part[] { new(5.5, -2, 1, 2, Hex(0x2B3A55)), new(5, -3, 2, 1, Hex(0xFFD23F)) },
            Face = new Part[] { new(3, 0.6, 6, 1.8, Hex(0x2B3A55)) },
            Weights = W(30, 10, 6, 12, 5, 4, 1, 10, 5, 6, 3, 5, 6, 2, 9, 4, 5),
        },
        new()
        {
            Name = "Mochi", Body = Hex(0x8E8EA6), Top = 2,
            Behind = new Part[]
            {
                new(2, -2, 2, 2.1, Hex(0x8E8EA6)), new(8, -2, 2, 2.1, Hex(0x8E8EA6)),
                new(2.6, -1.4, 0.9, 1.4, Hex(0xF5A3B5)), new(8.5, -1.4, 0.9, 1.4, Hex(0xF5A3B5)),
            },
            Face = new Part[] { new(5.5, 2.3, 1, 0.7, Hex(0xF5A3B5)) },
            Weights = W(26, 3, 5, 12, 4, 3, 12, 3, 4, 6, 11, 13, 5, 3, 2, 5, 6),
        },
        new()
        {
            Name = "Pip", Body = Hex(0x6CC24A), Top = 1.6, EyeY = -0.6, EyeTravel = 0.5,
            Legs = new[] { (2.0, 2.0), (8.0, 2.0) },
            Behind = new Part[] { new(2.4, -1.6, 2.4, 2, Hex(0x6CC24A)), new(7.2, -1.6, 2.4, 2, Hex(0x6CC24A)) },
            Face = new Part[]
            {
                new(2.8, -1.1, 1.6, 1.6, Hex(0xFFFFFF)), new(7.6, -1.1, 1.6, 1.6, Hex(0xFFFFFF)),
                new(4, 2.6, 4, 0.5, Hex(0x2E6B1F)), new(4, 4, 4, 2, Hex(0xB7E59A)),
            },
            Weights = W(26, 6, 15, 12, 5, 4, 4, 5, 13, 6, 5, 7, 5, 3, 5, 5, 5),
        },
        new()
        {
            Name = "Boo", Body = Hex(0x9B6BE0), Round = true, LegHeight = 1, EyeHeight = 1.4, EyeTravel = 0.6,
            Face = new Part[] { new(2.6, 0.7, 1.8, 2.2, Hex(0xFFFFFF)), new(7.6, 0.7, 1.8, 2.2, Hex(0xFFFFFF)) },
            Weights = W(28, 5, 5, 17, 4, 2, 5, 4, 4, 17, 6, 8, 5, 4, 2, 5, 7),
        },
        new()
        {
            Name = "Bun", Body = Hex(0xF4F1EA), Top = 3.4, LegHeight = 1,
            Legs = new[] { (2.0, 3.0), (7.0, 3.0) },
            Behind = new Part[]
            {
                new(3, -3.4, 1.6, 3.6, Hex(0xF4F1EA)), new(7.4, -3.4, 1.6, 3.6, Hex(0xF4F1EA)),
                new(3.5, -2.8, 0.6, 2.6, Hex(0xF5A3B5)), new(7.9, -2.8, 0.6, 2.6, Hex(0xF5A3B5)),
            },
            Face = new Part[] { new(5.5, 2.3, 1, 0.7, Hex(0xF5A3B5)) },
            Weights = W(28, 12, 8, 12, 5, 4, 4, 4, 11, 6, 5, 6, 5, 7, 5, 5, 5),
        },
        new()
        {
            Name = "Chip", Body = Hex(0xFFD23F), Leg = Hex(0xF28C28), Top = 1.8,
            Legs = new[] { (3.5, 1.0), (7.5, 1.0) },
            Behind = new Part[] { new(5, -1, 1, 1.1, Hex(0xFFD23F)), new(6, -1.8, 1, 1.9, Hex(0xFFD23F)) },
            Face = new Part[] { new(5, 2.2, 2, 1, Hex(0xF28C28)) },
            Weights = W(28, 6, 6, 12, 11, 11, 4, 5, 5, 6, 5, 6, 10, 3, 5, 5, 4),
        },
    };

    static Brush Hex(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
