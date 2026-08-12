using System.Runtime.InteropServices;

namespace MakeTestLib;

/// <summary>
/// MP3 and FLAC files, encoded in process by the un4seen encoders the repo already vendors
/// (src/JustPlay.Audio.Bass/native/win-x64: bassenc_mp3.dll, bassenc_flac.dll - the csproj copies
/// them next to this exe). Nothing is downloaded and no external encoder is shelled out to.
///
/// <para>The mechanism is the one BassRecordingService uses: attach an encoder to a channel with
/// BASS_ENCODE_PAUSE so it creates the file but never pulls anything, then push the sample data in
/// by hand with BASS_Encode_Write. The channel here is a DUMMY decode stream, which exists only to
/// declare the sample format (44.1 kHz, 16 bit, stereo) - it never decodes and never plays, so this
/// runs on the "no sound" device and needs no audio hardware at all.</para>
/// </summary>
internal sealed class BassEncoder : IDisposable
{
    private const int BassStreamDecode = 0x200000; // BASS_STREAM_DECODE
    private const int BassEncodePause = 32;        // BASS_ENCODE_PAUSE (bassenc.h)

    private int _stream;
    private bool _initialised;

    // un4seen.com/doc/bass/BASS_Init.html - device 0 is the "no sound" device.
    [DllImport("bass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BASS_Init(int device, int freq, int flags, IntPtr win, IntPtr clsid);

    [DllImport("bass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BASS_Free();

    [DllImport("bass")]
    private static extern int BASS_ErrorGetCode();

    // un4seen.com/doc/bass/BASS_StreamCreate.html - a null STREAMPROC is STREAMPROC_DUMMY.
    [DllImport("bass")]
    private static extern int BASS_StreamCreate(int freq, int chans, int flags, IntPtr proc, IntPtr user);

    [DllImport("bass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BASS_StreamFree(int handle);

    // un4seen.com/doc/bassenc/BASS_Encode_Write.html - length is in BYTES, in the channel's format.
    [DllImport("bassenc")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BASS_Encode_Write(int handle, byte[] buffer, int length);

    [DllImport("bassenc")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BASS_Encode_Stop(int handle);

    // un4seen.com/doc/bassenc_mp3/BASS_Encode_MP3_StartFile.html
    [DllImport("bassenc_mp3", CharSet = CharSet.Ansi)]
    private static extern int BASS_Encode_MP3_StartFile(int handle, string? options, int flags, string filename);

    // un4seen.com/doc/bassenc_flac/BASS_Encode_FLAC_StartFile.html
    [DllImport("bassenc_flac", CharSet = CharSet.Ansi)]
    private static extern int BASS_Encode_FLAC_StartFile(int handle, string? options, int flags, string filename);

    public void Open()
    {
        if (!BASS_Init(0, Pcm.SampleRate, 0, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException($"BASS_Init failed (error {BASS_ErrorGetCode()}).");
        _initialised = true;

        _stream = BASS_StreamCreate(Pcm.SampleRate, Pcm.Channels, BassStreamDecode, IntPtr.Zero, IntPtr.Zero);
        if (_stream == 0)
            throw new InvalidOperationException($"BASS_StreamCreate failed (error {BASS_ErrorGetCode()}).");
    }

    /// <summary>CBR 160 kbit/s, the LAME option string BassBroadcastService also uses.</summary>
    public void WriteMp3(string path, byte[] samples) =>
        Encode(path, samples, BASS_Encode_MP3_StartFile(_stream, "-b 160", BassEncodePause, path), "MP3");

    /// <summary>Default FLAC compression. The source is already 16-bit integer, so no float
    /// conversion flags are needed (that is the only thing FLAC refuses to take).</summary>
    public void WriteFlac(string path, byte[] samples) =>
        Encode(path, samples, BASS_Encode_FLAC_StartFile(_stream, null, BassEncodePause, path), "FLAC");

    private static void Encode(string path, byte[] samples, int encoder, string what)
    {
        if (encoder == 0)
            throw new InvalidOperationException(
                $"{what} encoder failed to start for {path} (error {BASS_ErrorGetCode()}).");

        try
        {
            if (!BASS_Encode_Write(encoder, samples, samples.Length))
                throw new InvalidOperationException(
                    $"{what} encode write failed for {path} (error {BASS_ErrorGetCode()}).");
        }
        finally
        {
            BASS_Encode_Stop(encoder);
        }
    }

    public void Dispose()
    {
        if (_stream != 0) { BASS_StreamFree(_stream); _stream = 0; }
        if (_initialised) { BASS_Free(); _initialised = false; }
    }
}
