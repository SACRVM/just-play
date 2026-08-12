namespace JustPlay.Tag.ViewModels;

/// <summary>Why the file pane has nothing in it. Each one gets its own words - and its own way out.</summary>
public enum EmptyReason
{
    /// <summary>Not empty, or too early to say (still listing / still reading). Say nothing.</summary>
    None,

    /// <summary>The folder holds no audio files, but it does hold folders that might.</summary>
    NoAudioButFoldersBelow,

    /// <summary>The folder holds no audio files and nothing below it either.</summary>
    NoAudioAtAll,

    /// <summary>There ARE files here; the search is hiding all of them.</summary>
    FilteredOut,

    /// <summary>A PLAYLIST that resolves to no tracks. Deliberately not the same state as an empty
    /// folder: a set names tracks that live somewhere else, so "no audio files in this folder"
    /// would answer a question nobody asked. A leaf FOLDER shown as a list is still a folder and
    /// gets the folder wording.</summary>
    EmptyPlaylist,
}

/// <summary>What the empty pane says: one calm line, and a hint under it when there is somewhere to go.</summary>
/// <param name="Reason">Which of the empty states this is.</param>
/// <param name="Line">The sentence. Empty for <see cref="EmptyReason.None"/>.</param>
/// <param name="Hint">The way out, or null when there is nothing useful to add.</param>
/// <param name="OffersClear">The search is what is hiding the files, so offer to clear it.</param>
public readonly record struct EmptyState(EmptyReason Reason, string Line, string? Hint, bool OffersClear)
{
    public static readonly EmptyState Silent = new(EmptyReason.None, "", null, false);

    public bool IsEmpty => Reason != EmptyReason.None;
}

/// <summary>
/// The words the file pane shows when it has no rows.
///
/// <para><b>Why this exists.</b> Opening a folder that holds only SUB-folders produced a blank pane
/// and no explanation. The pane was CORRECT and said nothing, which from where the user sits is the
/// same thing as broken - and it was read as broken. An empty list has to say WHY it is empty and,
/// where there is one, what to do about it: the answer to "GENRES looks empty" is "open one of the
/// folders on the left", not silence.</para>
///
/// <para>The shape is the PRE CUE FINDER's, which already solved this for its own pane: a centred
/// muted line per distinct reason, plus a button on the one state that has an action ("No tracks
/// match the filter." / "Clear filters"). Matched rather than re-invented.</para>
///
/// <para>Pure and static so the copy is pinned by tests instead of being read off a screenshot.</para>
/// </summary>
public static class FilePaneEmptyState
{
    /// <param name="hasFolder">A folder has been picked at all. Before that the pane runs its own
    /// "Drop a folder here" offer and this must stay quiet.</param>
    /// <param name="busy">Still listing the folder or still reading its tags - the header's progress
    /// line is already saying so, and "no audio files" during a load would simply be wrong.</param>
    /// <param name="hasProblem">The folder could not be read. That message is the folder's own
    /// (permission denied, share gone) and it wins: it is the one empty state with a CAUSE.</param>
    /// <param name="shown">Rows currently on screen.</param>
    /// <param name="total">Rows before the search narrowed them.</param>
    /// <param name="filtering">A search condition is active.</param>
    /// <param name="foldersBelow">What the folder pane offers besides "..": sub-folders and
    /// playlists. (When a LEAF folder is shown as a list those are its siblings, not its children -
    /// which is why the hint says "on the left" and never "below".)</param>
    /// <param name="isPlaylist">The pane is showing a PLAYLIST. A leaf folder shown as a list is
    /// still a folder and takes the folder wording.</param>
    public static EmptyState Describe(
        bool hasFolder, bool busy, bool hasProblem,
        int shown, int total, bool filtering, int foldersBelow, bool isPlaylist)
    {
        if (!hasFolder || busy || hasProblem || shown > 0) return EmptyState.Silent;

        // The search is hiding everything. Distinct from "there is nothing here" on purpose: one is
        // a state you created and can undo in a click, the other is a fact about the folder.
        if (filtering && total > 0)
            return new EmptyState(
                EmptyReason.FilteredOut,
                "No files match the search.",
                null,
                OffersClear: true);

        if (isPlaylist)
            return new EmptyState(
                EmptyReason.EmptyPlaylist,
                "This set has no tracks.",
                "Nothing it lists could be found on this machine.",
                OffersClear: false);

        if (foldersBelow > 0)
            return new EmptyState(
                EmptyReason.NoAudioButFoldersBelow,
                "No audio files in this folder.",
                foldersBelow == 1
                    ? "Open the folder on the left."
                    : $"Open one of the {foldersBelow} folders on the left.",
                OffersClear: false);

        return new EmptyState(
            EmptyReason.NoAudioAtAll,
            "No audio files in this folder.",
            "Nothing below it either - drop files here, or pick another folder.",
            OffersClear: false);
    }
}
