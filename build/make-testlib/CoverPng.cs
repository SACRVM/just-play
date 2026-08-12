using System.Buffers.Binary;

namespace MakeTestLib;

/// <summary>
/// A tiny PNG, written by hand, so a couple of the test files carry real cover art without any
/// image library and without shipping a bitmap in the repo. The deflate stream uses STORED blocks
/// only - a stored block is defined by this file alone, so the bytes cannot change under us when
/// the runtime's compressor changes, and the tree stays reproducible.
/// </summary>
internal static class CoverPng
{
    private const int Size = 96;

    /// <summary>A flat two tone chip whose colours come from the seed, so different tracks get
    /// visibly different covers and the same track always gets the same one.</summary>
    public static byte[] Render(uint seed)
    {
        var r1 = (byte)(40 + seed % 120);
        var g1 = (byte)(60 + seed / 7 % 150);
        var b1 = (byte)(90 + seed / 13 % 140);
        var r2 = (byte)(255 - r1);
        var g2 = (byte)(255 - g1);
        var b2 = (byte)(255 - b1);

        // Raw scanlines: one filter byte (0 = None) then RGB triples.
        var raw = new byte[Size * (1 + Size * 3)];
        var p = 0;
        for (var y = 0; y < Size; y++)
        {
            raw[p++] = 0;
            for (var x = 0; x < Size; x++)
            {
                // Diagonal split plus a vertical ramp - enough to look like artwork at 96 px.
                var top = x + y < Size;
                var ramp = y / (double)Size;
                raw[p++] = Mix(top ? r1 : r2, ramp);
                raw[p++] = Mix(top ? g1 : g2, ramp);
                raw[p++] = Mix(top ? b1 : b2, ramp);
            }
        }

        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), Size);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), Size);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: truecolour RGB
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering
        ihdr[12] = 0; // no interlace
        Chunk(ms, "IHDR"u8, ihdr);
        Chunk(ms, "IDAT"u8, ZlibStored(raw));
        Chunk(ms, "IEND"u8, []);

        return ms.ToArray();
    }

    private static byte Mix(byte channel, double ramp) => (byte)(channel * (0.65 + 0.35 * ramp));

    private static void Chunk(Stream target, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        target.Write(len);
        target.Write(type);
        target.Write(data);

        var crc = Crc32(type, data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        target.Write(c);
    }

    /// <summary>zlib container (RFC 1950) whose deflate payload (RFC 1951) is stored blocks only.</summary>
    private static byte[] ZlibStored(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); // CMF: deflate, 32 KiB window
        ms.WriteByte(0x01); // FLG: level 0, and 0x7801 is divisible by 31 as the spec requires

        var offset = 0;
        do
        {
            var take = Math.Min(0xFFFF, data.Length - offset);
            var final = offset + take >= data.Length;
            ms.WriteByte((byte)(final ? 1 : 0));           // BFINAL, BTYPE = 00 (stored)
            ms.WriteByte((byte)(take & 0xFF));             // LEN, little endian
            ms.WriteByte((byte)(take >> 8));
            ms.WriteByte((byte)(~take & 0xFF));            // NLEN, the ones complement of LEN
            ms.WriteByte((byte)(~take >> 8 & 0xFF));
            ms.Write(data, offset, take);
            offset += take;
        }
        while (offset < data.Length);

        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(data));
        ms.Write(adler);
        return ms.ToArray();
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return b << 16 | a;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ c >> 1 : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in type) c = CrcTable[(c ^ x) & 0xFF] ^ c >> 8;
        foreach (var x in data) c = CrcTable[(c ^ x) & 0xFF] ^ c >> 8;
        return c ^ 0xFFFFFFFFu;
    }
}
