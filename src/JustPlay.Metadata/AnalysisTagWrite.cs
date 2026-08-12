using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Metadata;

/// <summary>What a write does to ONE analysis field.</summary>
public enum TagFieldAction
{
    /// <summary>Leave the standard tag alone. The stored decision carries over unchanged.</summary>
    None,

    /// <summary>Put our detected value in the standard tag - the file is ours for this field.</summary>
    Write,

    /// <summary>Record that the claimed tag value was reviewed and stands, so it stops flagging.</summary>
    Keep,
}

/// <summary>
/// The ONE place that turns "we measured this track" into a <see cref="TagWrite"/> - the JUSTPLAY blob
/// plus the standard tags, with the per-field decisions and the reversible stash of whatever foreign
/// value we overwrote.
///
/// <para><b>Why it exists.</b> This composition used to be written out by hand in every app that
/// writes analysis: <c>MainWindowViewModel.Persist</c> in JUST PLAY, <c>PromoteCommand</c> in the CLI,
/// and JUST TAG was about to get a third copy. Three copies of "what a fresh analysis writes" is three
/// chances for one of them to forget the ReplayGain fields, the <see cref="FieldDecision.Kept"/> rule
/// or the <see cref="TrackAnalysisState.Original"/> stash - a divergence to kill at the root rather
/// than patch per app.</para>
///
/// <para>It composes, it does not write: the caller still hands the result to
/// <see cref="IMetadataWriter.Write"/> through whatever seam that app uses to let go of an open file
/// (JUST PLAY defers the playing track, JUST TAG releases the preview). Pure and side-effect free, so
/// the rules are testable without touching a file.</para>
///
/// <para>It lives in JustPlay.Metadata because the DJ-compatible comment is built by
/// <see cref="DjCommentBuilder"/>, which lives here - and every app that writes tags already
/// references this project.</para>
/// </summary>
public static class AnalysisTagWrite
{
    /// <summary>
    /// The "we just analysed this file" write: our detected value into every field that has one,
    /// EXCEPT a field the user has explicitly kept.
    ///
    /// <para>(!) <see cref="FieldDecision.Kept"/> means "the user reviewed this and the tag stands".
    /// Overwriting it here would silently undo a hand correction on the next analysis - you fix a
    /// wrong key, the track gets re-analysed, and your fix is gone. An explicit Write / Fill-missing
    /// still overwrites, because that is the user asking for it.</para>
    /// </summary>
    /// <param name="detected">What the DSP just measured.</param>
    /// <param name="current">The file's metadata as it is right now (tags + any stored blob). Null
    /// when the caller has not read it - then nothing is treated as kept and nothing is stashed.</param>
    /// <param name="analysedAtUtc">When the DSP actually ran. Null falls back to whatever the stored
    /// blob already claimed - never "now", which is merely when we are writing.</param>
    /// <param name="djComment">Rebuild the file's comment in the DJ-software-compatible shape
    /// ("8A - Energy 7 | whatever the user wrote"). Opt-in, and off by default in every app.</param>
    /// <returns>The write, or null when there is nothing to write at all.</returns>
    public static TagWrite? ForDetected(
        AnalysisResult detected,
        TrackMetadata? current,
        DateTime? analysedAtUtc = null,
        bool djComment = false)
    {
        ArgumentNullException.ThrowIfNull(detected);

        var prev = current?.StoredAnalysis;

        return ForFields(
            detected,
            current,
            detected.Bpm is > 0 && prev?.BpmDecision != FieldDecision.Kept
                ? TagFieldAction.Write : TagFieldAction.None,
            detected.Key is not null && prev?.KeyDecision != FieldDecision.Kept
                ? TagFieldAction.Write : TagFieldAction.None,
            detected.Energy is not null && prev?.EnergyDecision != FieldDecision.Kept
                ? TagFieldAction.Write : TagFieldAction.None,
            analysedAtUtc,
            djComment);
    }

    /// <summary>
    /// The general form: decide each field separately. This is what a per-cell "write BPM" / "keep
    /// key" acts through, and what <see cref="ForDetected"/> is expressed in.
    /// </summary>
    /// <returns>The write, or null when all three fields are <see cref="TagFieldAction.None"/> -
    /// there is then nothing to say and the file is not opened at all.</returns>
    public static TagWrite? ForFields(
        AnalysisResult detected,
        TrackMetadata? current,
        TagFieldAction bpm,
        TagFieldAction key,
        TagFieldAction energy,
        DateTime? analysedAtUtc = null,
        bool djComment = false)
    {
        ArgumentNullException.ThrowIfNull(detected);

        if (bpm == TagFieldAction.None && key == TagFieldAction.None && energy == TagFieldAction.None)
            return null;

        var prev = current?.StoredAnalysis;

        // Stash the pre-overwrite foreign value the FIRST time we overwrite a field, and keep any
        // already-stored original so a second write never loses the true origin.
        var origBpm = prev?.Original?.Bpm
                      ?? (bpm == TagFieldAction.Write ? current?.TaggedBpm : null);
        var origKey = prev?.Original?.Key
                      ?? (key == TagFieldAction.Write ? MusicalKey.TryParse(current?.TaggedKey) : null);
        var origEnergy = prev?.Original?.Energy
                         ?? (energy == TagFieldAction.Write ? current?.TaggedEnergy : null);

        var original = origBpm is null && origKey is null && origEnergy is null
            ? null
            : new AnalysisResult { Bpm = origBpm, Key = origKey, Energy = origEnergy };

        var state = new TrackAnalysisState
        {
            Version  = TrackAnalysisState.CurrentVersion,
            Detected = detected,
            // WHEN the values in Detected were measured. Falls back to whatever the file already
            // claimed and stays null when nothing knows: a null reads as "unknown", which the
            // staleness rules treat as stale rather than clean. Guessing a date here would
            // re-create exactly the blindness that fix was about.
            AnalysedAtUtc  = analysedAtUtc ?? prev?.AnalysedAtUtc,
            Original       = original,
            BpmDecision    = Decide(bpm,    prev?.BpmDecision),
            KeyDecision    = Decide(key,    prev?.KeyDecision),
            EnergyDecision = Decide(energy, prev?.EnergyDecision),
        };

        // The comment is built from the values as they WILL BE in the tag after this write:
        //   Write -> the detected value; Keep / None -> whatever the file already claims.
        string? comment = null;
        if (djComment)
        {
            var effectiveKey = key == TagFieldAction.Write
                ? detected.Key : MusicalKey.TryParse(current?.TaggedKey);
            var effectiveEnergy = energy == TagFieldAction.Write
                ? detected.Energy : current?.TaggedEnergy;
            comment = DjCommentBuilder.Build(effectiveKey, effectiveEnergy, current?.Comment);
        }

        return new TagWrite
        {
            Bpm    = bpm    == TagFieldAction.Write ? detected.Bpm    : null,
            Key    = key    == TagFieldAction.Write ? detected.Key    : null,
            Energy = energy == TagFieldAction.Write ? detected.Energy : null,
            // ReplayGain rides along with every write (it is the mp3gain / MIK replacement): the
            // REPLAYGAIN_TRACK_GAIN / _PEAK fields are non-destructive standards that do not collide
            // with BPM / key / energy, so they need no per-field decision - whenever there is a
            // loudness measurement, stamp it. Null (un-analysed) simply writes nothing.
            ReplayGainDb = detected.ReplayGainDb,
            Peak         = detected.Peak,
            State        = state,
            Comment      = comment,
        };
    }

    private static FieldDecision Decide(TagFieldAction action, FieldDecision? prior) => action switch
    {
        TagFieldAction.Write => FieldDecision.Applied,
        TagFieldAction.Keep  => FieldDecision.Kept,
        _                    => prior ?? FieldDecision.Pending,
    };
}
