namespace Orbit.Core.Media;

public sealed record AudioNormalizeOptions
{
    public string? OutputDirectory { get; init; }
    public double TargetLufs { get; init; } = -16;
    public double TruePeakDb { get; init; } = -1;
    public double LoudnessRange { get; init; } = 11;
}

public sealed record AudioLoudnessAnalysis(double IntegratedLufs, double TruePeakDb, double LoudnessRange,
    int SampleRate, int Channels, double DurationSeconds);
