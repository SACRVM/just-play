using JustPlay.Core.Audio;
using Xunit;

namespace JustPlay.Core.Tests;

public class ChannelExtractorTests
{
    [Fact]
    public void FourChannel_Offset0_TakesMasterPair_DropsCue()
    {
        // 2 frames, 4 ch interleaved: [M-L M-R C-L C-R] — Master on 0/1, Cue on 2/3.
        var src = new float[] { 1f, 2f, 90f, 91f,   3f, 4f, 92f, 93f };
        var dst = new float[1];
        int n = ChannelExtractor.ToStereoPair(src, src.Length, channels: 4, masterOffset: 0, ref dst);

        Assert.Equal(4, n); // 2 frames × 2
        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, dst[..n]); // only Master survives; Cue dropped
    }

    [Fact]
    public void FourChannel_Offset2_TakesSecondPair()
    {
        var src = new float[] { 1f, 2f, 90f, 91f,   3f, 4f, 92f, 93f };
        var dst = new float[8];
        int n = ChannelExtractor.ToStereoPair(src, src.Length, channels: 4, masterOffset: 2, ref dst);

        Assert.Equal(4, n);
        Assert.Equal(new[] { 90f, 91f, 92f, 93f }, dst[..n]);
    }

    [Fact]
    public void Stereo_PassesThroughUnchanged()
    {
        var src = new float[] { 1f, 2f, 3f, 4f };
        var dst = new float[4];
        int n = ChannelExtractor.ToStereoPair(src, src.Length, channels: 2, masterOffset: 0, ref dst);

        Assert.Equal(4, n);
        Assert.Equal(src, dst[..n]);
    }

    [Fact]
    public void OffsetPastEnd_IsClampedIntoRange()
    {
        // offset 4 on a 4-ch buffer would read out of bounds → clamp to channels-2 (=2).
        var src = new float[] { 1f, 2f, 5f, 6f };
        var dst = new float[4];
        int n = ChannelExtractor.ToStereoPair(src, src.Length, channels: 4, masterOffset: 4, ref dst);

        Assert.Equal(2, n); // 1 frame × 2
        Assert.Equal(new[] { 5f, 6f }, dst[..n]);
    }

    [Fact]
    public void GrowsDestinationWhenTooSmall()
    {
        var src = new float[] { 1f, 2f, 0f, 0f,   3f, 4f, 0f, 0f };
        var dst = new float[1]; // undersized on purpose
        int n = ChannelExtractor.ToStereoPair(src, src.Length, channels: 4, masterOffset: 0, ref dst);

        Assert.Equal(4, n);
        Assert.True(dst.Length >= 4);
        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, dst[..n]);
    }

    [Fact]
    public void PartialFrame_IgnoresTrailingFloats()
    {
        // 5 valid floats on a 4-ch layout = 1 whole frame; the extra float is ignored.
        var src = new float[] { 1f, 2f, 3f, 4f, 99f };
        var dst = new float[4];
        int n = ChannelExtractor.ToStereoPair(src, validFloats: 5, channels: 4, masterOffset: 0, ref dst);

        Assert.Equal(2, n);
        Assert.Equal(new[] { 1f, 2f }, dst[..n]);
    }

    [Theory]
    [InlineData(AppCaptureChannels.FullMix, 2, 0)]
    [InlineData(AppCaptureChannels.Master12, 4, 0)] // the default — capture 4ch, broadcast pair 1/2
    [InlineData(AppCaptureChannels.Master34, 4, 2)]
    public void AppCaptureFormat_MapsSelectionToChannelsAndOffset(AppCaptureChannels sel, int channels, int offset)
    {
        var f = AppCaptureFormat.From(sel);
        Assert.Equal(channels, f.CaptureChannels);
        Assert.Equal(offset, f.MasterChannelOffset);
    }
}
