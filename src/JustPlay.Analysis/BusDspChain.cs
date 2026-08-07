namespace JustPlay.Analysis;

/// <summary>
/// Offline reproduction of the JustPlay live output bus chain.
/// Applies the same DSP processors in the same priority order as <c>BassAudioEngine</c>,
/// so the <c>spectrum</c> command accurately represents what listeners hear.
///
/// <para><b>Bus order (matches BassAudioEngine DSP priorities - higher priority runs first):</b>
/// <list type="number">
///   <item><see cref="ThreeBandEqualizer"/>   - priority 200</item>
///   <item><see cref="AdaptiveTilt"/>          - priority 180</item>
///   <item><see cref="TransientDesigner"/>     - priority 140</item>
///   <item><see cref="MasteringLimiter"/>      - priority   0 (optional: disabled when <c>limiterEnabled</c> = false)</item>
/// </list>
/// </para>
///
/// <para>At neutral/default values each processor's own bypass short-circuit fires
/// (e.g. <c>EQ all-unity</c>, <c>Tilt strength 0</c>, <c>Transient punch 0</c>) so the
/// chain is bit-transparent through all inactive stages. The Limiter is gated by its
/// enabled flag - when disabled it is never instantiated and never consumes CPU.</para>
///
/// <para>Platform-agnostic, reflection-free, allocation-free on the audio path, trim/AOT-safe.</para>
/// </summary>
public sealed class BusDspChain
{
    private readonly ThreeBandEqualizer   _eq;
    private readonly AdaptiveTilt         _tilt;
    private readonly TransientDesigner    _transient;
    private readonly MasteringLimiter?    _limiter;  // null = disabled

    private readonly bool _limiterEnabled;

    /// <summary>
    /// Construct the offline bus chain with explicit per-processor parameters.
    /// Pass neutral/default values to bypass individual stages transparently.
    /// </summary>
    /// <param name="eqLow">EQ low-shelf linear gain (1.0 = unity).</param>
    /// <param name="eqMid">EQ mid-peaking linear gain (1.0 = unity).</param>
    /// <param name="eqHigh">EQ high-shelf linear gain (1.0 = unity).</param>
    /// <param name="tiltStrength">Adaptive tilt strength 0..1 (0 = bit-transparent bypass).</param>
    /// <param name="transientPunch">Transient designer punch 0..1 (0 = bit-transparent bypass).</param>
    /// <param name="limiterEnabled">Whether the mastering limiter is active.</param>
    /// <param name="limiterDriveDb">Limiter maximizer drive in dB (0 = safety only).</param>
    /// <param name="limiterCeilingDbTp">Limiter true-peak ceiling in dBTP (e.g. -1).</param>
    /// <param name="sampleRate">Bus sample rate in Hz (44 100 for the JustPlay engine).</param>
    public BusDspChain(
        double eqLow                = 1.0,
        double eqMid                = 1.0,
        double eqHigh               = 1.0,
        double tiltStrength         = 0.0,
        double transientPunch       = 0.0,
        bool   limiterEnabled       = false,
        double limiterDriveDb       = 0.0,
        double limiterCeilingDbTp   = -1.0,
        int    sampleRate           = 44100)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        _eq        = new ThreeBandEqualizer(sampleRate, eqLow, eqMid, eqHigh);
        _tilt      = new AdaptiveTilt(sampleRate, tiltStrength);
        _transient = new TransientDesigner(sampleRate, transientPunch);

        _limiterEnabled = limiterEnabled;
        if (limiterEnabled)
            _limiter = new MasteringLimiter(sampleRate, limiterCeilingDbTp, limiterDriveDb);
    }

    /// <summary>
    /// Reset all processor states.  Call between analysis passes or when switching tracks.
    /// </summary>
    public void Reset()
    {
        _eq.Reset();
        _tilt.Reset();
        _transient.Reset();
        _limiter?.Reset();
    }

    /// <summary>
    /// Process one block of interleaved-stereo float samples (L,R,L,R,...) in place.
    /// Applies processors in bus order: EQ -> Tilt -> Transient -> Limiter.
    /// State carries across calls - process the whole signal in sequential chunks.
    /// </summary>
    public void ProcessInterleavedStereo(Span<float> buf)
    {
        _eq.ProcessInterleavedStereo(buf);
        _tilt.ProcessInterleavedStereo(buf);
        _transient.ProcessInterleavedStereo(buf);
        if (_limiterEnabled) _limiter!.ProcessInterleavedStereo(buf);
    }

    /// <summary>
    /// How hard the limiter is working right now, in dB of gain reduction (0 when it is not limiting,
    /// or when it is disabled). Read it after a <see cref="ProcessInterleavedStereo"/> block.
    ///
    /// <para><b>Why this exists.</b> A drum sound's real question is not "how does it sound" but "how
    /// does it sound <i>through a master chain, next to a bassline</i>" - and the honest answer has two
    /// halves: how hard it HITS, and how much LIMITER it eats to do so. A kick that only gets loud by
    /// pushing everything else down is not a good kick, it is a greedy one. You cannot see that without
    /// the gain reduction, so the chain has to be willing to say it.</para>
    ///
    /// <para>Read-only telemetry - it changes no audio and costs nothing when unread.</para>
    /// </summary>
    public double LimiterGainReductionDb => _limiterEnabled ? _limiter!.LastGainReductionDb : 0.0;

    /// <summary>Whether the limiter stage is active at all. When false,
    /// <see cref="LimiterGainReductionDb"/> is always 0 - and any bench reading it should say
    /// "no limiter" rather than "no limiting", because those are very different sentences.</summary>
    public bool HasLimiter => _limiterEnabled;
}
