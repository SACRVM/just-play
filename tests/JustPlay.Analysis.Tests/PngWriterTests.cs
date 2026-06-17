using JustPlay.Analysis;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="PngWriter"/>.
///
/// Coverage:
///   PW1 — Encoded bytes begin with the PNG signature (89 50 4E 47 0D 0A 1A 0A).
///   PW2 — Encoded output contains the IHDR, IDAT, and IEND chunk type markers.
///   PW3 — A small non-trivial image encodes to a non-empty buffer whose length is
///          deterministic (same canvas, same operations → same byte sequence).
///   PW4 — Clear(), SetPixel(), and DrawText() produce non-trivially different pixel data
///          (sanity check that drawing operations actually modify the buffer).
///   PW5 — Save() writes a valid file that starts with the PNG signature.
/// </summary>
public class PngWriterTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IhdrMarker   = [0x49, 0x48, 0x44, 0x52]; // "IHDR"
    private static readonly byte[] IdatMarker   = [0x49, 0x44, 0x41, 0x54]; // "IDAT"
    private static readonly byte[] IendMarker   = [0x49, 0x45, 0x4E, 0x44]; // "IEND"

    // ── PW1: PNG signature ────────────────────────────────────────────────────

    [Fact]
    public void EncodePng_StartsWithPngSignature()
    {
        var p   = new PngWriter(8, 8);
        p.Clear(0x336699FF);
        byte[] png = p.EncodePng();

        Assert.True(png.Length > 8, "PNG must be longer than 8 bytes.");
        for (int i = 0; i < PngSignature.Length; i++)
            Assert.Equal(PngSignature[i], png[i]);
    }

    // ── PW2: Contains IHDR, IDAT, IEND ───────────────────────────────────────

    [Fact]
    public void EncodePng_ContainsRequiredChunkTypes()
    {
        var p   = new PngWriter(16, 16);
        p.FillRect(2, 2, 10, 10, 0xFF8800FF);
        byte[] png = p.EncodePng();

        Assert.True(ContainsSequence(png, IhdrMarker), "PNG must contain IHDR chunk type.");
        Assert.True(ContainsSequence(png, IdatMarker), "PNG must contain IDAT chunk type.");
        Assert.True(ContainsSequence(png, IendMarker), "PNG must contain IEND chunk type.");
    }

    // ── PW3: Deterministic non-trivial output ─────────────────────────────────

    [Fact]
    public void EncodePng_IsDeterministic()
    {
        byte[] png1 = BuildTestPng();
        byte[] png2 = BuildTestPng();

        Assert.Equal(png1.Length, png2.Length);
        for (int i = 0; i < png1.Length; i++)
            Assert.Equal(png1[i], png2[i]);
        Assert.True(png1.Length > 100, "Encoded PNG should be non-trivially large.");
    }

    // ── PW4: Drawing operations modify the buffer ─────────────────────────────

    [Fact]
    public void DrawOperations_ModifyCanvas()
    {
        var p1 = new PngWriter(32, 32);
        p1.Clear(0x000000FF);
        byte[] black = p1.EncodePng();

        var p2 = new PngWriter(32, 32);
        p2.Clear(0x000000FF);
        p2.SetPixel(10, 10, 0xFFFFFFFF);
        p2.DrawLine(0, 0, 31, 31, 0xFF4400FF);
        p2.DrawText(2, 2, "8k", 0x00FF00FF);
        byte[] drawn = p2.EncodePng();

        Assert.NotEqual(black, drawn);
    }

    // ── PW5: Save() writes a valid PNG file ───────────────────────────────────

    [Fact]
    public void Save_WritesValidPngFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"justplay-pngtest-{Guid.NewGuid():N}.png");
        try
        {
            var p = new PngWriter(20, 10);
            p.Clear(0x102030FF);
            p.DrawHLine(0, 19, 5, 0xFF0000FF);
            p.Save(path);

            Assert.True(File.Exists(path), "PNG file should exist after Save().");
            byte[] bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 20, "Saved PNG file should not be trivially small.");

            for (int i = 0; i < PngSignature.Length; i++)
                Assert.Equal(PngSignature[i], bytes[i]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] BuildTestPng()
    {
        var p = new PngWriter(40, 20);
        p.Clear(0x1A1A2EFF);
        p.FillRect(5, 5, 30, 10, 0x4488CCFF);
        p.DrawLine(0, 0, 39, 19, 0xFF8844FF);
        p.DrawText(2, 6, "dB Hz", 0xFFFFFFFF);
        return p.EncodePng();
    }

    private static bool ContainsSequence(byte[] data, byte[] seq)
    {
        int limit = data.Length - seq.Length;
        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < seq.Length; j++)
                if (data[i + j] != seq[j]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }
}
