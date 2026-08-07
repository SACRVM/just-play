namespace JustPlay.Core.Audio;

/// <summary>
/// How the "capture a specific APP" source (per-process loopback) handles a target that renders
/// MORE than two channels - the classic case being DJ software driving a multi-out interface where
/// Master sits on one channel pair and the headphone Cue on another (e.g. Reloop Mixtour + Traktor:
/// Master = ch 1/2, Cue = ch 3/4 of one 4-channel device).
///
/// <para>Validated 2026-07-02: per-process loopback delivers those channels <b>separated</b> (a
/// 4-channel capture of Traktor showed independent audio on 1/2 vs 3/4, correlation ~ 0.18), so we
/// can isolate the Master pair instead of downmixing everything to stereo - which is what leaked the
/// Cue into the broadcast. See <c>roadmap-just-stream</c> memory + the just-route vision doc Sec.11b.</para>
/// </summary>
public enum AppCaptureChannels
{
    /// <summary>Downmix the app's entire output to stereo. Correct for normal apps with a single stereo
    /// output; on a multi-out DJ device it mixes Master + Cue together (cue bleed).</summary>
    FullMix = 0,

    /// <summary>THE DEFAULT. Capture 4 channels, broadcast only the FIRST pair (channels 1/2) -
    /// typically the Master on multi-out DJ gear, dropping the Cue on 3/4. Lossless for a plain stereo
    /// app too: Windows' 2->4 upmix preserves ch1/2 at unity (measured 2026-07-02), so it "just works"
    /// with no per-app config and no reliance on DJ detection. The UI labels it neutrally "Channels
    /// 1-2". If a 4-ch capture fails, <see cref="WasapiProcessLoopbackCapture"/> falls back to stereo.</summary>
    Master12 = 1,

    /// <summary>Capture 4 channels, broadcast only the SECOND pair (channels 3/4). On typical DJ gear
    /// 3/4 is the headphone Cue, NOT the Master - offered for setups whose Master is routed to that
    /// pair. The UI labels it neutrally "Channels 3-4" so it never claims a role it can't know.</summary>
    Master34 = 2,
}

/// <summary>
/// The low-level capture shape derived from an <see cref="AppCaptureChannels"/> choice: how many
/// channels to request from the source, and the interleaved offset of the Master pair to extract.
/// Keeps the capture provider "dumb" (channels + offset) while the enum->shape policy lives here.
/// </summary>
public readonly record struct AppCaptureFormat(int CaptureChannels, int MasterChannelOffset)
{
    public static AppCaptureFormat From(AppCaptureChannels selection) => selection switch
    {
        AppCaptureChannels.Master12 => new AppCaptureFormat(4, 0),
        AppCaptureChannels.Master34 => new AppCaptureFormat(4, 2),
        _ => new AppCaptureFormat(2, 0), // FullMix - stereo downmix, unchanged behaviour
    };
}
