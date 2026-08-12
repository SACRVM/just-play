using System.Collections.Generic;
using System.Linq;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.Tests;

/// <summary>
/// Where the RAW tab states OWNERSHIP: in the row's tooltip, and nowhere else.
///
/// <para>Lives in this project because it is the only test project that can see JustPlay.UI - the
/// same reason <c>TagTransformTests</c> does.</para>
///
/// <para>The case that made this file necessary: a file carrying our analysis frames AND the
/// ReplayGain fields reported "2 frames from JUST PLAY" above four TXXX rows. Both numbers were
/// right and the pair was confusing. Two things came out of it, and both are pinned below - the
/// summary line is gone (the row says it, closer to the frame it is about), and ReplayGain became
/// recognisable WITHOUT becoming attributable.</para>
/// </summary>
public class RawTagOwnershipTests
{
    private sealed class FakeReader(RawTagReadResult result) : IRawTagReader
    {
        public RawTagReadResult Read(string filePath) => result;
    }

    private static RawTagEntry Txxx(string descriptor, string summary, RawTagVendor vendor) =>
        new() { Id = "TXXX", Descriptor = descriptor, SizeBytes = 20, Summary = summary, Vendor = vendor };

    private static RawTagsViewModel Open(params RawTagEntry[] entries)
    {
        var result = new RawTagReadResult
        {
            FilePath = @"C:\music\track.mp3",
            Containers =
            [
                new RawTagContainer
                {
                    Kind = RawTagContainerKind.Id3v2,
                    Label = "ID3v2.3",
                    Entries = entries,
                },
            ],
        };

        return new RawTagsViewModel(new FakeReader(result), [@"C:\music\track.mp3"]);
    }

    /// <summary>
    /// Nothing above the table counts frames by owner. That summary existed twice - as chips, then as
    /// a sentence - and both times it said what the rows already say, one step further from the frame
    /// in question. What is left up there is the container/entry count, which the rows do NOT say.
    ///
    /// <para>Pinned as a test because a summary line is the natural thing to add back: it looks like
    /// service and it is a claim the reader cannot always support.</para>
    /// </summary>
    [Fact]
    public void NoOwnerSummaryAboveTheTable_OnlyTheEntryCount()
    {
        var vm = Open(
            Txxx("ENERGY", "7", RawTagVendor.JustPlay),
            Txxx("JUSTPLAY", "v9,bpm=148.7", RawTagVendor.JustPlay),
            Txxx("REPLAYGAIN_TRACK_GAIN", "-8.40 dB", RawTagVendor.ReplayGain),
            Txxx("REPLAYGAIN_TRACK_PEAK", "0.988312", RawTagVendor.ReplayGain));

        Assert.Equal("1 container, 4 entries", vm.CountLine);

        // The copied listing is the screen in text form, so it carries no summary either - the
        // per-row labels travel with the rows.
        Assert.DoesNotContain("from JUST PLAY", vm.ListingText);
        Assert.Contains("[JUST PLAY]", vm.ListingText);
    }

    /// <summary>The row label is about the FRAME, and a ReplayGain field is identifiable even though
    /// its author is not - so the row is labelled while the sentence stays silent. This is the whole
    /// point of the split.</summary>
    [Fact]
    public void Row_LabelsReplayGain_ButNeverAsOurs()
    {
        var vm = Open(Txxx("REPLAYGAIN_TRACK_GAIN", "-8.40 dB", RawTagVendor.ReplayGain));

        var row = Assert.Single(vm.Sections.Single().Rows);
        Assert.Equal("REPLAYGAIN", row.Vendor);
        Assert.DoesNotContain("JUST PLAY", row.Tip);
    }

    /// <summary>The vendor has no column on screen any more - it reads as the first word of the value
    /// there - so the tooltip is the ONLY place it is shown. If it ever falls out of the tip it is
    /// gone from the UI entirely, silently.</summary>
    [Fact]
    public void Tip_CarriesHandleVendorAndValue()
    {
        var vm = Open(new RawTagEntry
        {
            Id = "GEOB",
            Descriptor = "Serato Markers2",
            SizeBytes = 470,
            Summary = "470 bytes",
            Vendor = RawTagVendor.Serato,
        });

        var row = Assert.Single(vm.Sections.Single().Rows);
        var lines = row.Tip.Split('\n');

        Assert.Equal("GEOB:Serato Markers2", lines[0]);
        Assert.Equal("SERATO", lines[1]);
        Assert.Equal("470 bytes", lines[2]);
        Assert.Contains("Click to copy", row.Tip);
    }

    /// <summary>An ordinary frame has no owner to name, so its tip skips the line rather than showing
    /// an empty one.</summary>
    [Fact]
    public void Tip_OmitsTheVendorLineWhenThereIsNoVendor()
    {
        var vm = Open(new RawTagEntry
        {
            Id = "TIT2",
            SizeBytes = 30,
            Summary = "Some Title",
            Vendor = RawTagVendor.Unknown,
        });

        var row = Assert.Single(vm.Sections.Single().Rows);
        var lines = row.Tip.Split('\n');

        Assert.Equal("TIT2", lines[0]);
        Assert.Equal("Some Title", lines[1]);
    }

    /// <summary>A copied line has no pointer to hover, so the bracketed name is the only way the
    /// owner survives a paste - it stays inline in the text form on purpose.</summary>
    [Fact]
    public void CopiedLine_KeepsTheVendorInline()
    {
        var vm = Open(Txxx("REPLAYGAIN_TRACK_GAIN", "-8.40 dB", RawTagVendor.ReplayGain));

        var row = Assert.Single(vm.Sections.Single().Rows);
        Assert.Contains("[REPLAYGAIN]", row.Line);
        Assert.Contains("TXXX:REPLAYGAIN_TRACK_GAIN", row.Line);
    }
}
