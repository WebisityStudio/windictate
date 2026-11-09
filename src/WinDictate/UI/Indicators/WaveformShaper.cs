namespace WinDictate.UI.Indicators;

/// <summary>
/// Turns the microphone peak level into bar heights that move like a voice
/// rather than like a meter: quick to rise, slower to fall, with the bars
/// leaning on each other instead of jumping in unison. Pure and deterministic,
/// so it can be unit-tested on any OS.
/// </summary>
internal sealed class WaveformShaper
{
    public const int BarCount = 7;

    /// <summary>Height of a bar when nothing is being said (0..1 of full height).</summary>
    public const double Floor = 0.16;

    /// <summary>
    /// Microphones differ a lot in how loud they are, so the level is read in
    /// decibels, the way loudness is heard: a quiet microphone still moves the
    /// bars clearly, room hiss does not, and a loud one does not pin them.
    /// At or below <see cref="QuietDb"/> the bars rest; at <see cref="LoudDb"/> they are full.
    /// </summary>
    private const double QuietDb = -52;
    private const double LoudDb = -8;
    private const double AttackPerSecond = 18.0;
    private const double ReleasePerSecond = 6.0;

    private readonly double[] _heights = new double[BarCount];
    private double _time;

    public WaveformShaper() => Reset();

    /// <summary>Smoothed input level, 0..1.</summary>
    public double Level { get; private set; }

    /// <summary>Current bar heights, each between <see cref="Floor"/> and 1.</summary>
    public IReadOnlyList<double> Heights => _heights;

    public void Reset()
    {
        Level = 0;
        _time = 0;
        Array.Fill(_heights, Floor);
    }

    /// <summary>Advance by <paramref name="dtSeconds"/> towards the latest microphone peak (0..1).</summary>
    public void Advance(double peak, double dtSeconds)
    {
        if (double.IsNaN(peak) || peak < 0) peak = 0;
        double dt = Math.Clamp(dtSeconds, 0, 0.1);
        _time += dt;

        double target = peak <= 0
            ? 0
            : Math.Clamp((20 * Math.Log10(peak) - QuietDb) / (LoudDb - QuietDb), 0, 1);
        double rate = target > Level ? AttackPerSecond : ReleasePerSecond;
        Level += (target - Level) * (1 - Math.Exp(-rate * dt));

        for (int i = 0; i < BarCount; i++)
        {
            // Each bar wobbles on its own phase; the middle bars reach a little higher.
            double wobble = 0.55 + 0.45 * Math.Abs(Math.Sin(_time * 7.3 + i * 1.3));
            double centre = 1 - Math.Abs(i - (BarCount - 1) / 2.0) / (BarCount - 1);
            double height = Floor + (1 - Floor) * Level * wobble * (0.7 + 0.3 * centre);
            _heights[i] = Math.Clamp(height, Floor, 1);
        }
    }
}
