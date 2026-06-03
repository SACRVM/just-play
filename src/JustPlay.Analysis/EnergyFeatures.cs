namespace JustPlay.Analysis;

/// <summary>
/// Raw and normalised feature values extracted by <see cref="SpectralEnergyDetector.ExtractFeatures"/>.
/// Used by the <c>--dump-energy-features</c> console harness to write a per-track CSV row
/// so that calibration can be performed in Python against the DEAM arousal labels.
///
/// <para>Raw values are before normalisation (LUFS, dimensionless flux ratio, Hz, dimensionless RMS-SD).
/// Normalised values (N*) are clamped to [0,1] by the current <c>Norm(lo,hi)</c> constants.</para>
/// [energy-detection.md §Grounding the 1–10 scale, ml/calibrate_energy.py]
/// </summary>
public readonly record struct EnergyFeatures(
    double RawLufs,      // BS.1770 integrated gated loudness (LUFS, negative)
    double RawFlux,      // mean positive spectral flux / frame magnitude (dimensionless)
    double RawCentroid,  // mean spectral centroid (Hz)
    double RawRmsSd,     // standard deviation of frame RMS (dimensionless groove term)
    double NLoud,        // Norm(RawLufs,    LufsLo,     LufsHi)     → [0,1]
    double NFlux,        // Norm(RawFlux,    FluxLo,     FluxHi)     → [0,1]
    double NBright,      // Norm(RawCentroid, CentroidLo, CentroidHi) → [0,1]
    double NRmsSd);      // Norm(RawRmsSd,  RmsSdLo,    RmsSdHi)    → [0,1]
