using JustPlay.Analysis;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="SpectralTarget"/>.
///
/// Coverage:
///   ST1 — DbAt is monotonically non-increasing above 1 kHz.
///   ST2 — DbAt is flat (constant) below 120 Hz.
///   ST3 — All outputs are finite across 20 Hz – 20 kHz.
///   ST4 — Anchor at 1 kHz = 0 dB.
///   ST5 — Tolerance band helpers are offset by ±ToleranceDb.
/// </summary>
public class SpectralTargetTests
{
    [Fact]
    public void DbAt_MonotonicallyNonIncreasingAbove1kHz()
    {
        double prev = SpectralTarget.DbAt(1000.0);
        double[] testFreqs = [1100, 1250, 1500, 2000, 3000, 5000, 8000, 12000, 16000, 20000];
        foreach (double f in testFreqs)
        {
            double db = SpectralTarget.DbAt(f);
            Assert.True(db <= prev + 0.001,
                $"DbAt({f}) = {db:F2} should be ≤ DbAt({f/1.1:F0}) = {prev:F2} dB");
            prev = db;
        }
    }

    [Fact]
    public void DbAt_FlatBelowFlatBelowHz()
    {
        // Below 120 Hz the curve should be constant (equal sub-bass plateau).
        double expected = SpectralTarget.DbAt(SpectralTarget.FlatBelowHz);
        foreach (double f in new[] { 20.0, 30.0, 50.0, 80.0, 100.0, 119.0 })
        {
            double db = SpectralTarget.DbAt(f);
            Assert.True(Math.Abs(db - expected) < 0.01,
                $"DbAt({f}) = {db:F2} should equal DbAt({SpectralTarget.FlatBelowHz}) = {expected:F2}");
        }
    }

    [Fact]
    public void DbAt_AllFrequencies_AreFinite()
    {
        for (double f = 20.0; f <= 20000.0; f *= 1.1)
        {
            double db = SpectralTarget.DbAt(f);
            Assert.True(double.IsFinite(db), $"DbAt({f:F0}) returned non-finite: {db}");
        }
    }

    [Fact]
    public void DbAt_AnchorAt1kHz_IsZero()
    {
        double db = SpectralTarget.DbAt(SpectralTarget.AnchorHz);
        Assert.True(Math.Abs(db) < 0.001, $"Expected 0 dB at anchor {SpectralTarget.AnchorHz} Hz; got {db:F4}");
    }

    [Fact]
    public void DbAt_NonPositiveFrequency_ReturnsNaN()
    {
        Assert.True(double.IsNaN(SpectralTarget.DbAt(0.0)));
        Assert.True(double.IsNaN(SpectralTarget.DbAt(-100.0)));
    }

    [Fact]
    public void ToleranceBandHelpers_OffsetByToleranceDb()
    {
        foreach (double f in new[] { 50.0, 500.0, 2000.0, 10000.0 })
        {
            double mid   = SpectralTarget.DbAt(f);
            double upper = SpectralTarget.UpperDb(f);
            double lower = SpectralTarget.LowerDb(f);
            Assert.Equal(SpectralTarget.ToleranceDb, upper - mid, precision: 5);
            Assert.Equal(SpectralTarget.ToleranceDb, mid - lower, precision: 5);
        }
    }
}
