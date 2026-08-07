using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace JustPlay.Core.Playlists;

/// <summary>
/// Bundles a queue into a portable set - either a single self-contained .zip or a plain folder - with
/// every audio file PLUS an .m3u8 that references them, so a set can be shared / uploaded / dropped on
/// a USB stick and imported into any DJ tool in one move. Files are written FLAT and prefixed with
/// their zero-padded set position ("01 - ...", "002 - ..."): that guarantees collision-free names (two
/// "track.mp3" from different folders no longer clash and silently overwrite) AND makes the files sort
/// in set order in any file browser, even when the importing tool ignores the playlist and just reads
/// the folder. The padding width scales with the set size (>=100 tracks -> three digits). The .m3u8 sits
/// alongside the audio and points at those names relatively, so the bundle round-trips through
/// <see cref="M3uPlaylist.ReadPaths"/>.
///
/// <para>Analysis (BPM/key/energy) rides along in the files' OWN tags - we copy the bytes verbatim, so
/// the recipient's tool reads our detection on import. Platform-agnostic: BCL only.</para>
/// </summary>
public static class PlaylistBundle
{
    /// <summary>One track to bundle: its source file on disk plus optional playlist metadata.</summary>
    public readonly record struct Entry(string SourcePath, TimeSpan? Duration, string? Title);

    /// <summary>Write a .zip containing every existing source file (flat, set-position-prefixed) and a
    /// single <paramref name="playlistName"/>.m3u8 at the root listing them in order. A missing or
    /// unreadable source file is skipped (never throws on one bad path). Reports progress as
    /// (done, total) after each file. Honours <paramref name="ct"/>: a cancel deletes the partial .zip
    /// and throws <see cref="OperationCanceledException"/> - so an aborted export leaves nothing behind.
    /// Returns the number of audio files actually written.</summary>
    public static int WriteZip(string destZip, string playlistName, IReadOnlyList<Entry> entries,
        IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var planned = PlanNames(entries);
        if (planned.Count == 0) return 0;
        var total = planned.Count;
        progress?.Report((0, total));

        try
        {
            int result;
            // Explicit using blocks (not `using var`) so the archive + file handle are released BEFORE
            // the catch deletes the partial file on cancel.
            using (var fs = new FileStream(destZip, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // Add the audio first, collecting the ones that actually made it in - so a file that
                // fails to open (locked, vanished mid-export) is left out of the playlist too.
                var written = new List<(Entry e, string name)>();
                foreach (var (e, name) in planned)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var src = OpenShared(e.SourcePath);
                        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                        using var dst = entry.Open();
                        CopyStream(src, dst, ct);
                        written.Add((e, name));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* skip - one unreadable track must not abort the whole export */ }
                    progress?.Report((written.Count, total));
                }

                if (written.Count == 0)
                {
                    result = 0;
                }
                else
                {
                    var m3uEntry = zip.CreateEntry(MakeSafeFileName(playlistName) + ".m3u8", CompressionLevel.NoCompression);
                    using var w = new StreamWriter(m3uEntry.Open(), Utf8NoBom);
                    w.Write(BuildM3u(written));
                    result = written.Count;
                }
            }

            if (result == 0) TryDeleteFile(destZip); // nothing usable - don't leave an empty archive
            return result;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(destZip);
            throw;
        }
    }

    /// <summary>Copy every existing source file (flat, set-position-prefixed) into <paramref name="destFolder"/>
    /// and write a <paramref name="playlistName"/>.m3u8 next to them listing the set in order. The folder
    /// is created if needed. Same portable layout as <see cref="WriteZip"/>, just unpacked - ideal for a
    /// USB stick. A missing / unreadable source file is skipped. Returns the number of files copied.</summary>
    public static int WriteToFolder(string destFolder, string playlistName, IReadOnlyList<Entry> entries,
        IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var planned = PlanNames(entries);
        if (planned.Count == 0) return 0;
        var total = planned.Count;
        progress?.Report((0, total));

        // Only delete the folder on cancel/empty if WE created it - never nuke a pre-existing one.
        var createdFolder = !Directory.Exists(destFolder);
        try
        {
            Directory.CreateDirectory(destFolder);

            var written = new List<(Entry e, string name)>();
            foreach (var (e, name) in planned)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var src = OpenShared(e.SourcePath);
                    using var dst = new FileStream(Path.Combine(destFolder, name), FileMode.Create, FileAccess.Write);
                    CopyStream(src, dst, ct);
                    written.Add((e, name));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* skip - one unreadable track must not abort the whole export */ }
                progress?.Report((written.Count, total));
            }

            if (written.Count == 0)
            {
                if (createdFolder) TryDeleteDir(destFolder);
                return 0;
            }

            File.WriteAllText(Path.Combine(destFolder, MakeSafeFileName(playlistName) + ".m3u8"), BuildM3u(written), Utf8NoBom);
            return written.Count;
        }
        catch (OperationCanceledException)
        {
            if (createdFolder) TryDeleteDir(destFolder);
            throw;
        }
    }

    // -- helpers --------------------------------------------------------------

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Resolve the existing source files in order and assign collision-free, set-ordered in-bundle
    /// names. Padding width scales with the count (min two digits, three at >=100, four at >=1000, ...).</summary>
    private static List<(Entry e, string name)> PlanNames(IReadOnlyList<Entry> entries)
    {
        var present = new List<Entry>();
        foreach (var e in entries)
            if (!string.IsNullOrEmpty(e.SourcePath) && File.Exists(e.SourcePath))
                present.Add(e);

        var pad = Math.Max(2, present.Count.ToString().Length);
        var planned = new List<(Entry, string)>(present.Count);
        for (var i = 0; i < present.Count; i++)
        {
            var e = present[i];
            var name = $"{(i + 1).ToString().PadLeft(pad, '0')} - {Path.GetFileName(e.SourcePath)}";
            planned.Add((e, name));
        }
        return planned;
    }

    /// <summary>Build the extended-M3U text (UTF-8 body) referencing the in-bundle names relatively.</summary>
    private static string BuildM3u(IReadOnlyList<(Entry e, string name)> written)
    {
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        foreach (var (e, name) in written)
        {
            var secs = e.Duration is { } d ? (int)Math.Round(d.TotalSeconds) : -1;
            var title = (e.Title ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
            sb.Append("#EXTINF:").Append(secs).Append(',').Append(title).Append('\n');
            sb.Append(name).Append('\n');
        }
        return sb.ToString();
    }

    // Shared read so a currently-playing file (held by the audio engine) still copies.
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    /// <summary>Copy in chunks, checking the cancellation token between blocks so a cancel interrupts
    /// promptly even mid-file (a large WAV won't block the abort).</summary>
    private static void CopyStream(Stream src, Stream dst, CancellationToken ct)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dst.Write(buffer, 0, read);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Strip characters that aren't legal in a file name, falling back to a default.</summary>
    private static string MakeSafeFileName(string name)
    {
        var s = FileNames.Sanitize(name).Trim();
        return s.Length == 0 ? "JustPlay set" : s;
    }
}
