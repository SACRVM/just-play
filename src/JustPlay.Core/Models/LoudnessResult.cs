namespace JustPlay.Core.Models;

/// <summary>
/// The output of <c>ILoudnessDetector.Detect</c>: BS.1770 integrated loudness and the
/// linear sample peak of the decoded buffer.
/// </summary>
/// <param name="IntegratedLufs">
/// K-weighted gated integrated loudness in LUFS (ITU-R BS.1770-4/-5 / EBU R128).
/// Negative; typical music is −20 to −6 LUFS. Silent tracks return null (detector returns null).
/// </param>
/// <param name="Peak">
/// Maximum absolute sample value over the decoded buffer, linear scale (0..~1).
/// Note: this is a sample-domain peak, not a true-peak / inter-sample peak measurement.
/// </param>
public readonly record struct LoudnessResult(double IntegratedLufs, double Peak);
