using System.Text;

namespace JustPlay.Metadata;

/// <summary>
/// Keeps an ID3v2.3 genre (TCON) exactly as the file stores it, through a save that does not write
/// the genre.
/// <para>
/// TagLib# 2.3 parses a v2.3 TCON by splitting it on BOTH '/' and ';', and renders it back joined
/// with ';'. It re-renders a text frame on every save whose encoding differs from the process-global
/// <c>Tag.DefaultEncoding</c> - in practice every v2.3 frame - so ANY save turned the Beatport genre
/// "Techno (Peak Time / Driving)" into "Techno (Peak Time ; Driving)", which is what Traktor and
/// Serato then show. Measured: a save that assigns nothing at all still did it. The split also
/// cannot be undone from TagLib#'s side - which separator stood between two parts is gone.
/// </para>
/// <para>
/// So the stored text is read from the file's own bytes (<see cref="Read"/>) and the frame is set to
/// that ONE string before the save (<see cref="Pin"/>), which TagLib# renders back unchanged. Scope
/// is the leading tag of the file - an MP3's - exactly like <see cref="Id3VersionProbe"/>; a tag in an
/// AIFF/WAV chunk keeps TagLib#'s behaviour.
/// </para>
/// </summary>
internal static class Id3v23Genre
{
    /// <summary>
    /// The text of the first TCON frame of the file's leading ID3v2.3 tag, as stored (up to the first
    /// NUL, like TagLib# reads it). Null when there is no such tag or frame, when the tag uses
    /// unsynchronisation or the frame is compressed/encrypted (the bytes are not the text then), and
    /// on any read error - null means "leave it to TagLib#", never a guess.
    /// </summary>
    public static string? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[10];
            if (fs.ReadAtLeast(head, 10, throwOnEndOfStream: false) < 10) return null;
            if (head[0] != 'I' || head[1] != 'D' || head[2] != '3' || head[3] != 3) return null;

            var flags = head[5];
            if ((flags & 0x80) != 0) return null;                    // unsynchronised

            var end = 10L + Syncsafe(head[6..]);
            if (end > fs.Length) return null;

            if ((flags & 0x40) != 0)                                  // extended header: skip it
            {
                Span<byte> ext = stackalloc byte[4];
                if (fs.ReadAtLeast(ext, 4, throwOnEndOfStream: false) < 4) return null;
                fs.Seek(BigEndian(ext), SeekOrigin.Current);
            }

            Span<byte> frame = stackalloc byte[10];
            while (fs.Position + 10 <= end)
            {
                if (fs.ReadAtLeast(frame, 10, throwOnEndOfStream: false) < 10 || frame[0] == 0)
                    return null;                                          // padding: no TCON

                var size = BigEndian(frame[4..]);
                if (size < 0 || fs.Position + size > end) return null;

                if (frame[0] != 'T' || frame[1] != 'C' || frame[2] != 'O' || frame[3] != 'N')
                {
                    fs.Seek(size, SeekOrigin.Current);
                    continue;
                }

                if ((frame[9] & 0xC0) != 0 || size < 1) return null;    // compressed / encrypted / empty

                var data = new byte[size];
                fs.ReadExactly(data);
                return Decode(data);
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Set the first TCON frame of <paramref name="file"/>'s ID3v2 tag to <paramref name="stored"/> as
    /// ONE value, in the frame's own encoding, so the next save writes the text the file already held.
    /// A no-op without a stored text, and for a bare number (TagLib# would render "17" as the ID3v1
    /// index "(17)" either way).
    /// </summary>
    public static void Pin(TagLib.File file, string? stored)
    {
        if (string.IsNullOrEmpty(stored) || byte.TryParse(stored, out _)) return;
        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is not TagLib.Id3v2.Tag id3) return;
        if (id3.GetFrames<TagLib.Id3v2.TextInformationFrame>("TCON").FirstOrDefault() is not { } tcon) return;

        // Read the encoding BEFORE replacing the text: the setter drops the raw bytes the encoding
        // is parsed from, and the frame would fall back to the default one.
        var encoding = tcon.TextEncoding;
        tcon.Text = [stored];
        tcon.TextEncoding = encoding;
    }

    /// <summary>The frame's text per its encoding byte, cut at the first NUL.</summary>
    private static string Decode(byte[] data)
    {
        var body = data.AsSpan(1);
        var text = data[0] switch
        {
            0 => Encoding.Latin1.GetString(body),
            1 => body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF
                 ? Encoding.BigEndianUnicode.GetString(body[2..])
                 : Encoding.Unicode.GetString(body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE
                                              ? body[2..] : body),
            2 => Encoding.BigEndianUnicode.GetString(body),
            _ => Encoding.UTF8.GetString(body),
        };

        var nul = text.IndexOf('\0');
        return nul >= 0 ? text[..nul] : text;
    }

    private static int Syncsafe(ReadOnlySpan<byte> b) => (b[0] << 21) | (b[1] << 14) | (b[2] << 7) | b[3];

    private static int BigEndian(ReadOnlySpan<byte> b) => (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
}
