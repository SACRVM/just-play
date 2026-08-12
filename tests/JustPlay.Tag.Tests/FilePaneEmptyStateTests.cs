namespace JustPlay.Tag.Tests;

/// <summary>
/// The words an empty file pane says. Pinned here rather than eyeballed, because the bug this copy
/// fixes was invisible by construction: a folder holding only sub-folders listed CORRECTLY, showed
/// nothing, and said nothing - and was read as the app being broken.
/// </summary>
public sealed class FilePaneEmptyStateTests
{
    /// <summary>The ordinary "here is a folder with 12 songs in it" case - say nothing at all.</summary>
    [Fact]
    public void SilentWhenRowsAreShowing()
    {
        var s = Describe(shown: 12, total: 12);

        Assert.False(s.IsEmpty);
        Assert.Equal("", s.Line);
    }

    /// <summary>Before a folder is picked the pane runs its own "Drop a folder here" offer.</summary>
    [Fact]
    public void SilentBeforeAFolderIsPicked()
    {
        Assert.False(Describe(hasFolder: false).IsEmpty);
    }

    /// <summary>While the folder is being listed / read, the header's progress line is the answer.
    /// "No audio files" during a load would simply be a wrong statement.</summary>
    [Fact]
    public void SilentWhileStillLoading()
    {
        Assert.False(Describe(busy: true).IsEmpty);
    }

    /// <summary>A folder that could not be read has its own message, with a CAUSE in it. That one
    /// wins - two centred texts would land on top of each other.</summary>
    [Fact]
    public void SilentWhenTheFolderReportedAProblem()
    {
        Assert.False(Describe(hasProblem: true).IsEmpty);
    }

    /// <summary>THE bug: only sub-folders. The answer is "look one level down", not "there is
    /// nothing" - and the pane has to say so.</summary>
    [Fact]
    public void FoldersBelowPointsDownwards()
    {
        var s = Describe(foldersBelow: 7);

        Assert.Equal(EmptyReason.NoAudioButFoldersBelow, s.Reason);
        Assert.Equal("No audio files in this folder.", s.Line);
        Assert.Equal("Open one of the 7 folders on the left.", s.Hint);
        Assert.False(s.OffersClear);
    }

    /// <summary>One folder is not "one of the 1 folders".</summary>
    [Fact]
    public void OneFolderBelowIsSingular()
    {
        Assert.Equal("Open the folder on the left.", Describe(foldersBelow: 1).Hint);
    }

    /// <summary>Nothing here and nothing below: a different fact, and a different hint.</summary>
    [Fact]
    public void EmptyFolderSaysThereIsNothingBelowEither()
    {
        var s = Describe(foldersBelow: 0);

        Assert.Equal(EmptyReason.NoAudioAtAll, s.Reason);
        Assert.Equal("No audio files in this folder.", s.Line);
        Assert.Contains("Nothing below it either", s.Hint);
    }

    /// <summary>A search hiding every row is a state you created and can undo in one click - so it
    /// is worded differently AND carries the offer.</summary>
    [Fact]
    public void FilteredOutOffersToClearTheSearch()
    {
        var s = Describe(shown: 0, total: 120, filtering: true, foldersBelow: 3);

        Assert.Equal(EmptyReason.FilteredOut, s.Reason);
        Assert.Equal("No files match the search.", s.Line);
        Assert.True(s.OffersClear);
    }

    /// <summary>(!) A filter over an EMPTY folder is not "the filter is hiding them" - there was
    /// nothing to hide. Saying "clear the search" there would send her chasing a filter that is not
    /// the reason.</summary>
    [Fact]
    public void FilteringAnEmptyFolderStillReportsTheFolder()
    {
        var s = Describe(shown: 0, total: 0, filtering: true, foldersBelow: 2);

        Assert.Equal(EmptyReason.NoAudioButFoldersBelow, s.Reason);
        Assert.False(s.OffersClear);
    }

    /// <summary>A set names tracks that live elsewhere, so "no audio files in this folder" would be
    /// answering a question nobody asked.</summary>
    [Fact]
    public void AnEmptySetSaysSo()
    {
        var s = Describe(isPlaylist: true, foldersBelow: 4);

        Assert.Equal(EmptyReason.EmptyPlaylist, s.Reason);
        Assert.Equal("This set has no tracks.", s.Line);
    }

    /// <summary>
    /// (!) A LEAF folder is shown in the file pane exactly the way a playlist is - and it is still a
    /// FOLDER. Telling her "this set has no tracks" over a folder she opened by clicking it would
    /// send her looking for a set she never opened. That is 06 EMPTY LANDING in the test library.
    /// </summary>
    [Fact]
    public void AnEmptyLeafFolderKeepsTheFolderWording()
    {
        var s = Describe(isPlaylist: false, foldersBelow: 0);

        Assert.Equal(EmptyReason.NoAudioAtAll, s.Reason);
        Assert.Equal("No audio files in this folder.", s.Line);
    }

    /// <summary>Every state that is shown says something - an empty pane with an empty line is the
    /// bug all over again.</summary>
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(3, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, true)]
    public void EveryVisibleStateHasWords(int foldersBelow, bool filtering, bool isPlaylist)
    {
        var s = Describe(shown: 0, total: filtering ? 9 : 0,
                         filtering: filtering, foldersBelow: foldersBelow, isPlaylist: isPlaylist);

        Assert.True(s.IsEmpty);
        Assert.NotEqual("", s.Line);
    }

    private static EmptyState Describe(
        bool hasFolder = true, bool busy = false, bool hasProblem = false,
        int shown = 0, int total = 0, bool filtering = false, int foldersBelow = 0,
        bool isPlaylist = false)
        => FilePaneEmptyState.Describe(hasFolder, busy, hasProblem, shown, total, filtering,
                                       foldersBelow, isPlaylist);
}
