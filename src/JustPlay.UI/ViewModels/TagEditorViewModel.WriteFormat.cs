using System.Collections.Generic;
using System.Linq;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// The write-format half of the shared tag editor: the notice that says, BEFORE the click, that
/// pressing Save would also change what is open from one ID3v2 version to another.
///
/// <para>Why it sits above Save and not in the settings that cause it: the setting is a standing
/// choice ("convert to ID3v2.3"), and the consequence is per file. Only the editor knows whether THIS
/// file is one of the ones it lands on. The settings card says what the option does; this says whether
/// it is about to happen to you.</para>
///
/// <para><b>Layering.</b> The version table lives in <c>JustPlay.Metadata.Id3VersionProbe</c>, which
/// this library must never reference. So the host hands the target over as the same short LABEL the
/// probe already put into <c>TrackMetadata.Id3Version</c> - one string produced by one probe on both
/// sides of the comparison, which is exact by construction and leaves no second version table to drift.
/// The precedent is <see cref="RawTagsViewModel"/>, which takes its target the same way.</para>
///
/// <para>Only JUST TAG ever sets <see cref="ConvertsToId3Version"/>: it owns the only WRITE FORMAT
/// setting in the suite, and the three converting modes are the only thing that sets TagLib#'s
/// <c>ForceDefaultVersion</c>. JUST PLAY, the PRE CUE FINDER and the CLI never convert, so the notice
/// can never appear there - it stays null and nothing is rendered.</para>
/// </summary>
public sealed partial class TagEditorViewModel
{
    /// <summary>
    /// The ID3v2 version every MP3 saved from this editor would be converted TO, as
    /// <c>Id3VersionProbe.ShortName(major)</c> ("2.3") - the SAME shape
    /// <c>TrackMetadata.Id3Version</c> carries. Null when the configured write format keeps each
    /// file's own version, which means no conversion can happen and no notice applies.
    /// <para>Assigning re-evaluates immediately, so switching the setting with the editor open cannot
    /// leave a stale notice standing - a stale warning is worse than none.</para>
    /// </summary>
    public string? ConvertsToId3Version
    {
        get => _convertsToId3Version;
        set
        {
            var clean = string.IsNullOrWhiteSpace(value) ? null : value;
            if (clean == _convertsToId3Version) return;
            _convertsToId3Version = clean;
            RefreshId3Notice();
        }
    }

    private string? _convertsToId3Version;

    /// <summary>The loaded file's ID3v2 version, or one entry per file across a selection. Null entries
    /// are files with no leading ID3v2 tag at all - a FLAC, an MP4, a never-tagged MP3 - which the write
    /// format cannot convert and which therefore never raise the notice.</summary>
    private IReadOnlyList<string?> _id3Versions = [];

    /// <summary>
    /// Set when saving would ALSO change the ID3v2 version of what is open. Null the rest of the time -
    /// the notice only appears on the files it actually applies to.
    /// </summary>
    public string? Id3Notice { get; private set; }

    /// <summary>The way out of <see cref="Id3Notice"/>. Non-null exactly when the notice is.</summary>
    public string? Id3NoticeFix { get; private set; }

    public bool HasId3Notice => Id3Notice is not null;

    /// <summary>Record what the open file (or the selection) currently is. Called from every path that
    /// changes the target, so there is one place the notice reads its facts from.</summary>
    private void SetId3Versions(IReadOnlyList<string?> versions)
    {
        _id3Versions = versions;
        RefreshId3Notice();
    }

    /// <summary>
    /// A save has just written these files, so they now carry whatever the write format produces.
    /// Recorded rather than re-read: the conversion IS what the notice was about, and re-opening every
    /// file to learn what we just asked TagLib# to do would cost a read per file to confirm it.
    /// </summary>
    private void MarkId3Converted(IReadOnlyList<bool> written)
    {
        if (_convertsToId3Version is null || _id3Versions.Count == 0) return;

        var next = _id3Versions.ToArray();
        for (var i = 0; i < next.Length && i < written.Count; i++)
        {
            // A file with no ID3v2 tag stays "none" here: it got a fresh tag, not a conversion, and it
            // was never what the notice was about.
            if (written[i] && next[i] is not null) next[i] = _convertsToId3Version;
        }

        SetId3Versions(next);
    }

    /// <summary>Re-evaluate the notice against the current write format. Cheap and idempotent - it
    /// compares strings that are already in memory and touches no file.</summary>
    private void RefreshId3Notice()
    {
        var before = Id3Notice;

        if (_convertsToId3Version is not { } target || _id3Versions.Count == 0)
        {
            Id3Notice = null;
            Id3NoticeFix = null;
        }
        else
        {
            var affected = _id3Versions.Count(v => v is not null && v != target);

            if (affected == 0)
            {
                Id3Notice = null;
                Id3NoticeFix = null;
            }
            else if (_id3Versions.Count == 1)
            {
                Id3Notice = Id3ConversionNotice.Headline(_id3Versions[0]!, target);
                Id3NoticeFix = Id3ConversionNotice.Fix(many: false);
            }
            else
            {
                Id3Notice = Id3ConversionNotice.Headline(affected, _id3Versions.Count, target);
                // Plural follows what is AFFECTED, not what is selected: one file inside a selection
                // of twelve is still "keep it as it is".
                Id3NoticeFix = Id3ConversionNotice.Fix(many: affected > 1);
            }
        }

        if (before == Id3Notice) return;

        Raise(nameof(Id3Notice));
        Raise(nameof(Id3NoticeFix));
        Raise(nameof(HasId3Notice));
    }
}
