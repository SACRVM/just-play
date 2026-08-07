using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace JustPlay.Analysis;

/// <summary>
/// Minimal zero-dependency RGBA raster canvas with a built-in PNG encoder and a 5x7 bitmap font.
/// Intended for offline spectrum plots - no NuGet, no native dependencies.
///
/// <para><b>PNG encoding (RFC 2083 / ISO 15948) using only BCL primitives:</b></para>
/// <list type="number">
///   <item>8-byte PNG signature.</item>
///   <item>IHDR - 8-bit RGBA (colour type 6), no interlace.</item>
///   <item>IDAT - zlib-wrapped DEFLATE via <see cref="DeflateStream"/>:
///     2-byte zlib header (CMF=0x78, FLG=0x9C), raw DEFLATE stream, then 4-byte big-endian
///     Adler-32 checksum of the uncompressed scanline data (filter-byte 0 per row + RGBA pixels).</item>
///   <item>IEND.</item>
/// </list>
///
/// <para>CRC-32 per PNG spec (ISO 3309, polynomial 0xEDB88320).  Chunk format:
/// 4-byte length + 4-byte type + data + 4-byte CRC32(type+data).</para>
///
/// <para>Colours are <c>uint</c> in <b>RRGGBBAA</b> byte order (shift 24/16/8/0).</para>
///
/// <para>Platform-agnostic, reflection-free, trim/AOT-safe.  Allocation-heavy on encode
/// (buffers one full copy of raw scanlines) but encode is an infrequent cold path.</para>
/// </summary>
public sealed class PngWriter
{
    // -- Canvas ---------------------------------------------------------------

    /// <summary>Canvas width in pixels.</summary>
    public int Width  { get; }
    /// <summary>Canvas height in pixels.</summary>
    public int Height { get; }

    // RGBA, row-major, 4 bytes per pixel (R at idx, G at idx+1, B at idx+2, A at idx+3).
    private readonly byte[] _pixels;

    /// <summary>Create an RGBA canvas of the given dimensions (all pixels transparent black).</summary>
    public PngWriter(int width, int height)
    {
        if (width  <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width   = width;
        Height  = height;
        _pixels = new byte[width * height * 4];
    }

    // -- Drawing primitives ----------------------------------------------------

    /// <summary>Fill the entire canvas with <paramref name="rgba"/>.</summary>
    public void Clear(uint rgba)
    {
        byte r = (byte)(rgba >> 24), g = (byte)(rgba >> 16),
             b = (byte)(rgba >>  8), a = (byte) rgba;
        for (int i = 0; i < _pixels.Length; i += 4)
        { _pixels[i] = r; _pixels[i+1] = g; _pixels[i+2] = b; _pixels[i+3] = a; }
    }

    /// <summary>Set one pixel (RRGGBBAA). Out-of-bounds calls are silently ignored.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPixel(int x, int y, uint rgba)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        int i = (y * Width + x) * 4;
        _pixels[i]   = (byte)(rgba >> 24);
        _pixels[i+1] = (byte)(rgba >> 16);
        _pixels[i+2] = (byte)(rgba >>  8);
        _pixels[i+3] = (byte) rgba;
    }

    /// <summary>
    /// Alpha-blend a colour over the canvas pixel at (x, y): Porter-Duff "over" with straight alpha.
    /// Out-of-bounds calls are silently ignored.
    /// </summary>
    public void BlendPixel(int x, int y, uint rgba)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        int i = (y * Width + x) * 4;

        int sr = (int)(rgba >> 24), sg = (int)(rgba >> 16) & 0xFF,
            sb = (int)(rgba >>  8) & 0xFF, sa = (int)rgba  & 0xFF;
        int dr = _pixels[i], dg = _pixels[i+1], db = _pixels[i+2], da = _pixels[i+3];

        int inv = 255 - sa;
        _pixels[i]   = (byte)((sr * sa + dr * inv) / 255);
        _pixels[i+1] = (byte)((sg * sa + dg * inv) / 255);
        _pixels[i+2] = (byte)((sb * sa + db * inv) / 255);
        _pixels[i+3] = (byte)(sa + da * inv / 255);
    }

    /// <summary>Draw a horizontal line.</summary>
    public void DrawHLine(int x0, int x1, int y, uint rgba)
    {
        if (x0 > x1) (x0, x1) = (x1, x0);
        for (int x = x0; x <= x1; x++) SetPixel(x, y, rgba);
    }

    /// <summary>Draw a vertical line.</summary>
    public void DrawVLine(int x, int y0, int y1, uint rgba)
    {
        if (y0 > y1) (y0, y1) = (y1, y0);
        for (int y = y0; y <= y1; y++) SetPixel(x, y, rgba);
    }

    /// <summary>Draw a line using Bresenham's algorithm.</summary>
    public void DrawLine(int x0, int y0, int x1, int y1, uint rgba)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        while (true)
        {
            SetPixel(x0, y0, rgba);
            if (x0 == x1 && y0 == y1) break;
            int e2 = err * 2;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 <  dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// Draw a dashed line using Bresenham's algorithm.
    /// <paramref name="dash"/> pixels on, <paramref name="gap"/> pixels off, repeating.
    /// </summary>
    public void DrawDashedLine(int x0, int y0, int x1, int y1, uint rgba, int dash = 6, int gap = 4)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx - dy, phase = 0, cycle = dash + gap;
        while (true)
        {
            if (phase < dash) SetPixel(x0, y0, rgba);
            phase = (phase + 1) % cycle;
            if (x0 == x1 && y0 == y1) break;
            int e2 = err * 2;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 <  dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>Fill an axis-aligned rectangle.</summary>
    public void FillRect(int x, int y, int w, int h, uint rgba)
    {
        for (int row = y; row < y + h; row++)
            for (int col = x; col < x + w; col++)
                SetPixel(col, row, rgba);
    }

    /// <summary>Alpha-blend-fill an axis-aligned rectangle.</summary>
    public void BlendRect(int x, int y, int w, int h, uint rgba)
    {
        for (int row = y; row < y + h; row++)
            for (int col = x; col < x + w; col++)
                BlendPixel(col, row, rgba);
    }

    /// <summary>Draw the outline of an axis-aligned rectangle.</summary>
    public void DrawRect(int x, int y, int w, int h, uint rgba)
    {
        DrawHLine(x,         x + w - 1, y,         rgba);
        DrawHLine(x,         x + w - 1, y + h - 1, rgba);
        DrawVLine(x,         y,         y + h - 1, rgba);
        DrawVLine(x + w - 1, y,         y + h - 1, rgba);
    }

    // -- Text rendering (built-in 5x7 bitmap font) ----------------------------

    /// <summary>
    /// Draw a string using the built-in 5x7 bitmap font.
    /// Characters not in the glyph table are drawn as a blank 5-pixel gap.
    /// Lowercase letters without dedicated glyphs are mapped to uppercase.
    /// </summary>
    /// <param name="x">Left edge of the first glyph.</param>
    /// <param name="y">Top edge of the first glyph.</param>
    /// <param name="text">String to render.</param>
    /// <param name="rgba">Pixel colour (RRGGBBAA).</param>
    /// <param name="scale">Integer pixel scale (1 = 5x7 px natural size).</param>
    public void DrawText(int x, int y, string text, uint rgba, int scale = 1)
    {
        if (string.IsNullOrEmpty(text)) return;
        int cx = x;
        foreach (char ch in text)
        {
            DrawGlyph(cx, y, ch, rgba, scale);
            cx += (GlyphW + 1) * scale;  // glyph width + 1 px kerning gap
        }
    }

    /// <summary>Pixel width of <paramref name="text"/> in the built-in font at the given scale.</summary>
    public static int TextWidth(string text, int scale = 1)
        => string.IsNullOrEmpty(text) ? 0 : text.Length * (GlyphW + 1) * scale;

    /// <summary>Pixel height of the built-in font at the given scale (7 rows).</summary>
    public static int TextHeight(int scale = 1) => GlyphH * scale;

    private const int GlyphW = 5;
    private const int GlyphH = 7;

    private void DrawGlyph(int x, int y, char ch, uint rgba, int scale)
    {
        if (!Font.TryGetGlyph(ch, out byte[]? rows) || rows is null) return;
        for (int row = 0; row < GlyphH; row++)
        {
            byte mask = rows[row];
            for (int col = 0; col < GlyphW; col++)
            {
                if ((mask & (0x10 >> col)) != 0)
                {
                    if (scale == 1)
                        SetPixel(x + col, y + row, rgba);
                    else
                        FillRect(x + col * scale, y + row * scale, scale, scale, rgba);
                }
            }
        }
    }

    // -- PNG encode ------------------------------------------------------------

    /// <summary>Encode the canvas to a PNG byte array.</summary>
    public byte[] EncodePng()
    {
        using var ms = new MemoryStream();
        WritePng(ms);
        return ms.ToArray();
    }

    /// <summary>Encode the canvas and write the PNG to <paramref name="path"/>.</summary>
    public void Save(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        WritePng(fs);
    }

    private void WritePng(Stream s)
    {
        // -- Signature ---------------------------------------------------------
        s.Write(PngSig);

        // -- IHDR --------------------------------------------------------------
        byte[] ihdr = new byte[13];
        WriteU32BE(ihdr, 0, (uint)Width);
        WriteU32BE(ihdr, 4, (uint)Height);
        ihdr[8]  = 8; // bit depth
        ihdr[9]  = 6; // colour type: RGBA
        // ihdr[10..12] = 0: deflate / adaptive filter / no interlace (already zeroed)
        WriteChunk(s, IhdrType, ihdr);

        // -- IDAT --------------------------------------------------------------
        // Raw scanlines: one filter-byte (0 = None) + Width*4 RGBA bytes per row.
        int    scanStride = 1 + Width * 4;
        byte[] raw        = new byte[Height * scanStride];
        for (int y = 0; y < Height; y++)
        {
            int dst = y * scanStride;
            raw[dst] = 0;   // filter = None
            Buffer.BlockCopy(_pixels, y * Width * 4, raw, dst + 1, Width * 4);
        }

        // Adler-32 is computed over the UNCOMPRESSED data (zlib requirement).
        uint adler = Adler32(raw);

        // Build the zlib stream: 2-byte header + raw DEFLATE + 4-byte Adler-32.
        using var idat = new MemoryStream();
        idat.WriteByte(0x78);  // CMF: deflate, window 32 KB
        idat.WriteByte(0x9C);  // FLG: default compression, no dict; 0x789C % 31 == 0 ok
        using (var deflate = new DeflateStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw);
        idat.WriteByte((byte)(adler >> 24));
        idat.WriteByte((byte)(adler >> 16));
        idat.WriteByte((byte)(adler >>  8));
        idat.WriteByte((byte) adler);

        WriteChunk(s, IdatType, idat.ToArray());

        // -- IEND --------------------------------------------------------------
        WriteChunk(s, IendType, []);
    }

    // -- Chunk writer ----------------------------------------------------------

    private static void WriteChunk(Stream s, byte[] type, byte[] data)
    {
        Span<byte> len4 = stackalloc byte[4];
        WriteU32BE(len4, (uint)data.Length);
        s.Write(len4);
        s.Write(type);
        if (data.Length > 0) s.Write(data);
        uint crc = CrcInit;
        crc = CrcUpdate(crc, type);
        if (data.Length > 0) crc = CrcUpdate(crc, data);
        crc ^= 0xFFFFFFFF;
        Span<byte> crc4 = stackalloc byte[4];
        WriteU32BE(crc4, crc);
        s.Write(crc4);
    }

    private static void WriteU32BE(byte[] buf, int off, uint v)
    {
        buf[off]   = (byte)(v >> 24); buf[off+1] = (byte)(v >> 16);
        buf[off+2] = (byte)(v >>  8); buf[off+3] = (byte) v;
    }

    private static void WriteU32BE(Span<byte> buf, uint v)
    {
        buf[0] = (byte)(v >> 24); buf[1] = (byte)(v >> 16);
        buf[2] = (byte)(v >>  8); buf[3] = (byte) v;
    }

    // -- CRC-32 (ITU-T V.42 / ISO 3309, polynomial 0xEDB88320) ----------------

    private const uint CrcInit = 0xFFFFFFFF;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        const uint poly = 0xEDB88320u;
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++) c = (c & 1u) != 0 ? poly ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint CrcUpdate(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    // -- Adler-32 -------------------------------------------------------------

    private static uint Adler32(byte[] data)
    {
        const uint mod = 65521u;
        uint s1 = 1, s2 = 0;
        foreach (byte b in data) { s1 = (s1 + b) % mod; s2 = (s2 + s1) % mod; }
        return (s2 << 16) | s1;
    }

    // -- PNG type constants ----------------------------------------------------

    private static readonly byte[] PngSig   = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IhdrType = [0x49, 0x48, 0x44, 0x52]; // "IHDR"
    private static readonly byte[] IdatType = [0x49, 0x44, 0x41, 0x54]; // "IDAT"
    private static readonly byte[] IendType = [0x49, 0x45, 0x4E, 0x44]; // "IEND"

    // -- Built-in 5x7 bitmap font ---------------------------------------------
    //
    // Each glyph: 7 bytes, one per row (top->bottom).  Each byte encodes 5 pixels in bits
    // [4..0]: bit 4 = leftmost column, bit 0 = rightmost column.
    // Lowercase letters without an explicit entry are mapped to their uppercase equivalent.

    private static class Font
    {
        private static readonly Dictionary<char, byte[]> Glyphs = new(96)
        {
            // -- Specials ------------------------------------------------------
            [' '] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            ['-'] = [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
            ['+'] = [0x00, 0x04, 0x04, 0x1F, 0x04, 0x04, 0x00],
            ['.'] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C],
            ['_'] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F],
            ['/'] = [0x01, 0x01, 0x02, 0x04, 0x08, 0x10, 0x10],

            // -- Digits --------------------------------------------------------
            ['0'] = [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            ['1'] = [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
            ['2'] = [0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F],
            ['3'] = [0x0E, 0x11, 0x01, 0x06, 0x01, 0x11, 0x0E],
            ['4'] = [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
            ['5'] = [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
            ['6'] = [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
            ['7'] = [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
            ['8'] = [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
            ['9'] = [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],

            // -- Uppercase A-Z -------------------------------------------------
            ['A'] = [0x04, 0x0A, 0x11, 0x11, 0x1F, 0x11, 0x11],
            ['B'] = [0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E],
            ['C'] = [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
            ['D'] = [0x1C, 0x12, 0x11, 0x11, 0x11, 0x12, 0x1C],
            ['E'] = [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
            ['F'] = [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10],
            ['G'] = [0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F],
            ['H'] = [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
            ['I'] = [0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E],
            ['J'] = [0x01, 0x01, 0x01, 0x01, 0x01, 0x11, 0x0E],
            ['K'] = [0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11],
            ['L'] = [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F],
            ['M'] = [0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11],
            ['N'] = [0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11],
            ['O'] = [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            ['P'] = [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
            ['Q'] = [0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D],
            ['R'] = [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
            ['S'] = [0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E],
            ['T'] = [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
            ['U'] = [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            ['V'] = [0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04],
            ['W'] = [0x11, 0x11, 0x11, 0x15, 0x15, 0x15, 0x0A],
            ['X'] = [0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11],
            ['Y'] = [0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04],
            ['Z'] = [0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F],

            // -- Lowercase specials named in the spec --------------------------
            // 'k' (1k, 2k, 5k frequency labels)
            ['k'] = [0x10, 0x10, 0x12, 0x14, 0x18, 0x14, 0x12],
            // 'z' ("Hz" unit label)
            ['z'] = [0x00, 0x00, 0x1F, 0x02, 0x04, 0x08, 0x1F],
            // 'd' ("dB" unit label)
            ['d'] = [0x01, 0x01, 0x0D, 0x13, 0x11, 0x13, 0x0D],
        };

        internal static bool TryGetGlyph(char ch, out byte[]? rows)
        {
            if (Glyphs.TryGetValue(ch, out rows)) return true;
            // Map lowercase -> uppercase for any other letters not in the table.
            if (ch >= 'a' && ch <= 'z')
            {
                char upper = (char)(ch - ('a' - 'A'));
                if (Glyphs.TryGetValue(upper, out rows)) return true;
            }
            rows = null;
            return false;
        }
    }
}
