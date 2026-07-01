// JustCheck.Probe — JUST CHECK audio-setup data-collection probe
// Part of J.U.S.T. — Just Useful Sound Tools
//
// PRIVACY NOTICE (written to every log file):
//   This probe collects ONLY anonymous audio-configuration diagnostics.
//   It contains NO personal data, NO credentials, NO stream passwords,
//   NO library contents, and NO account information.
//   The Windows username is never logged — paths use %USERPROFILE% etc.
//   You can read the entire log file before sharing it with anyone.

using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

// WASAPI COM objects are apartment-threaded; run on STA to be safe.
var staThread = new Thread(ProbeMain);
staThread.SetApartmentState(ApartmentState.STA);
staThread.Start();
staThread.Join();

// ────────────────────────────────────────────────────────────────────────────

static void ProbeMain()
{
    const string ProbeVersion = "0.1.0-probe";

    // ── Log setup ─────────────────────────────────────────────────────────
    string logFileName = $"JustCheck-{DateTime.Now:yyyyMMdd-HHmmss}.log";
    string logPath = Path.Combine(AppContext.BaseDirectory, logFileName);
    using var logFile = new StreamWriter(logPath, false, new UTF8Encoding(false));

    void Log(string line = "")
    {
        Console.WriteLine(line);
        logFile.WriteLine(line);
        logFile.Flush();
    }

    void Banner(string title) => Log($"\n==== {title} ====");

    int sectOk = 0, sectErr = 0;

    void RunSection(string name, Action body)
    {
        Banner(name);
        try { body(); sectOk++; }
        catch (Exception ex)
        {
            Log($"  [SECTION FAILED] {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is { } inner)
                Log($"    Inner: {inner.Message}");
            sectErr++;
        }
    }

    // ── Privacy helpers ───────────────────────────────────────────────────
    // Replace literal user-profile paths with env-var placeholders so
    // usernames never appear in the log.
    static string RedactPath(string path)
    {
        var lad   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var ad    = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var docs  = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var up    = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var music = Path.Combine(up, "Music");

        if (!string.IsNullOrEmpty(lad)   && path.StartsWith(lad,   StringComparison.OrdinalIgnoreCase))
            return "%LOCALAPPDATA%" + path[lad.Length..];
        if (!string.IsNullOrEmpty(ad)    && path.StartsWith(ad,    StringComparison.OrdinalIgnoreCase))
            return "%APPDATA%" + path[ad.Length..];
        if (!string.IsNullOrEmpty(docs)  && path.StartsWith(docs,  StringComparison.OrdinalIgnoreCase))
            return "%USERPROFILE%\\Documents" + path[docs.Length..];
        if (!string.IsNullOrEmpty(music) && path.StartsWith(music, StringComparison.OrdinalIgnoreCase))
            return "%USERPROFILE%\\Music" + path[music.Length..];
        if (!string.IsNullOrEmpty(up)    && path.StartsWith(up,    StringComparison.OrdinalIgnoreCase))
            return "%USERPROFILE%" + path[up.Length..];
        return path;
    }

    static bool LooksLikeCredential(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("password") || n.Contains("passwd") || n.Contains("secret") ||
               n.Contains("license")  || n.Contains("serial") || n.Contains("registration") ||
               n.Contains("activat")  || n.Contains("account") || n.Contains("email") ||
               n.Contains("token")    || n.Contains("apikey")  || n.Contains("api_key");
    }

    static bool IsRunning(string exeBaseName) =>
        Process.GetProcessesByName(exeBaseName).Length > 0;

    static string WaveStr(WaveFormat fmt)
        => $"{fmt.SampleRate} Hz / {fmt.BitsPerSample} bit / {fmt.Channels} ch / {fmt.Encoding}";

    // Try to parse a raw WAVEFORMATEX blob (VT_BLOB from property store)
    static string ParseWaveFmtBlob(object? val)
    {
        if (val is byte[] blob && blob.Length >= 16)
        {
            ushort channels   = BitConverter.ToUInt16(blob, 2);
            uint   sampleRate = BitConverter.ToUInt32(blob, 4);
            ushort bps        = BitConverter.ToUInt16(blob, 14);
            return $"{sampleRate} Hz / {bps} bit / {channels} ch (from blob)";
        }
        return val?.ToString() ?? "(null / unsupported variant type)";
    }

    // ── PKEY GUIDs (from Windows SDK Mmdeviceapi.h) ───────────────────────
    // PKEY_AudioEngine_DeviceFormat  {f19f064d-082c-4e27-bc73-6882a1bb8e4c}, 0
    // PKEY_AudioEngine_OEMFormat     {e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04}, 3
    // PKEY_AudioEndpoint_Disable_SysFx {1da5d803-d492-4edd-8c23-e0c0ffee7f0e}, 5
    var pkDevFmt = new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c");
    var pkOemFmt = new Guid("e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04");
    var pkSysFx  = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e");

    // Endpoint cache used across sections 2–5
    var endpointCache = new List<EndpointInfo>();

    // ─────────────────────────────────────────────────────────────────────────
    // §1  HEADER
    // ─────────────────────────────────────────────────────────────────────────
    Banner("PRIVACY NOTICE");
    Log("  This log contains ONLY anonymous audio-configuration diagnostics.");
    Log("  No personal data, no credentials, no stream passwords, no library");
    Log("  contents, and no account information are included. Windows usernames");
    Log("  are replaced with environment-variable placeholders. You can read");
    Log("  the entire file before sharing it.");

    Banner("HEADER");
    Log($"  Probe version : {ProbeVersion}");
    Log($"  Run ID        : {Guid.NewGuid():N}");
    Log($"  Timestamp     : {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
    Log($"  OS            : {Environment.OSVersion}");
    Log($"  Runtime       : {RuntimeInformation.FrameworkDescription}");
    Log($"  Architecture  : {RuntimeInformation.ProcessArchitecture} / OS:{RuntimeInformation.OSArchitecture}");
    Log($"  64-bit proc   : {Environment.Is64BitProcess}");
    Log($"  64-bit OS     : {Environment.Is64BitOperatingSystem}");
    Log($"  Log file      : {logFileName}  (in exe folder)");

    // ─────────────────────────────────────────────────────────────────────────
    // §2  AUDIO ENDPOINTS
    // ─────────────────────────────────────────────────────────────────────────
    RunSection("AUDIO ENDPOINTS", () =>
    {
        using var enumerator = new MMDeviceEnumerator();

        // ── Default endpoints ─────────────────────────────────────────────
        var defaults = new Dictionary<string, string>();
        foreach (var (flow, role, label) in new[]
        {
            (DataFlow.Render,  Role.Console,        "Render-Console"),
            (DataFlow.Render,  Role.Multimedia,     "Render-Multimedia"),
            (DataFlow.Render,  Role.Communications, "Render-Comms"),
            (DataFlow.Capture, Role.Console,        "Capture-Console"),
            (DataFlow.Capture, Role.Communications, "Capture-Comms"),
        })
        {
            try
            {
                using var d = enumerator.GetDefaultAudioEndpoint(flow, role);
                defaults[label] = d.ID;
            }
            catch { defaults[label] = "(none)"; }
        }
        foreach (var kv in defaults) Log($"  Default {kv.Key,-25}: {kv.Value}");

        // ── Enumerate ALL devices, both flows ─────────────────────────────
        int idx = 0;
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            MMDeviceCollection? col = null;
            try { col = enumerator.EnumerateAudioEndPoints(flow, DeviceState.All); }
            catch (Exception ex) { Log($"  [Enumerate {flow} failed: {ex.Message}]"); continue; }

            for (int i = 0; i < col.Count; i++)
            {
                try
                {
                    var dev = col[i];
                    idx++;
                    Log();
                    Log($"  [{idx:00}] {(flow == DataFlow.Render ? "RENDER " : "CAPTURE")}  {dev.FriendlyName}");
                    Log($"        ID    : {dev.ID}");
                    Log($"        State : {dev.State}");

                    // Mark default roles
                    var roles = defaults.Where(kv => kv.Value == dev.ID).Select(kv => kv.Key).ToList();
                    if (roles.Count > 0) Log($"        Roles : {string.Join(", ", roles)}  *** DEFAULT ***");

                    // MixFormat via IAudioClient::GetMixFormat
                    string mixStr = "(unavailable)";
                    int mixSR = 0, mixBits = 0, mixCh = 0;
                    try
                    {
                        if (dev.State == DeviceState.Active)
                        {
                            var fmt = dev.AudioClient.MixFormat;
                            mixSR   = fmt.SampleRate;
                            mixBits = fmt.BitsPerSample;
                            mixCh   = fmt.Channels;
                            mixStr  = WaveStr(fmt);
                        }
                    }
                    catch (Exception ex) { mixStr = $"[error: {ex.Message}]"; }
                    Log($"        MixFormat : {mixStr}");

                    // Property store — PKEY labels + generic dump
                    try
                    {
                        var store = dev.Properties;
                        Log($"        PropertyStore ({store.Count} entries):");
                        for (int p = 0; p < store.Count; p++)
                        {
                            try
                            {
                                var prop = store[p];
                                var key  = prop.Key;
                                var val  = prop.Value;

                                string label =
                                    key.formatId == pkDevFmt && key.propertyId == 0 ? " [PKEY_AudioEngine_DeviceFormat]" :
                                    key.formatId == pkOemFmt && key.propertyId == 3 ? " [PKEY_AudioEngine_OEMFormat]" :
                                    key.formatId == pkSysFx  && key.propertyId == 5 ? " [PKEY_AudioEndpoint_Disable_SysFx]" :
                                    "";

                                string valStr;
                                if (key.formatId == pkDevFmt || key.formatId == pkOemFmt)
                                    valStr = ParseWaveFmtBlob(val);
                                else
                                    valStr = val?.ToString() ?? "(null)";

                                if (key.formatId == pkSysFx && key.propertyId == 5)
                                    valStr += $"  ({(valStr == "0" ? "enhancements ENABLED" : "enhancements DISABLED")})";

                                Log($"          [{p:00}] {key.formatId},{key.propertyId,-3}{label} = {valStr}");
                            }
                            catch (Exception pex)
                            {
                                Log($"          [{p:00}] [unreadable: {pex.Message}]");
                            }
                        }
                    }
                    catch (Exception ex) { Log($"        PropertyStore: [error: {ex.Message}]"); }

                    // Cache for later sections
                    endpointCache.Add(new EndpointInfo(
                        dev.ID, dev.FriendlyName, flow, dev.State,
                        mixStr, mixSR, mixBits, mixCh));
                }
                catch (Exception ex) { Log($"  [{idx:00}] [device error: {ex.Message}]"); }
            }
        }
        Log();
        Log($"  Total: {idx} endpoints");
    });

    // ─────────────────────────────────────────────────────────────────────────
    // §3  ACTIVE SESSIONS  (process → endpoint map)
    // ─────────────────────────────────────────────────────────────────────────
    RunSection("ACTIVE SESSIONS", () =>
    {
        using var enumerator = new MMDeviceEnumerator();
        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        for (int i = 0; i < renderDevices.Count; i++)
        {
            try
            {
                var dev = renderDevices[i];
                Log($"\n  Endpoint: {dev.FriendlyName}");

                SessionCollection? sessions = null;
                try { sessions = dev.AudioSessionManager.Sessions; }
                catch (Exception ex) { Log($"    [SessionManager error: {ex.Message}]"); continue; }

                if (sessions.Count == 0) { Log("    (no active sessions)"); continue; }

                for (int s = 0; s < sessions.Count; s++)
                {
                    try
                    {
                        var sess = sessions[s];
                        uint pid = 0;
                        try { pid = sess.GetProcessID; } catch { }

                        string procName = "(unknown)";
                        if (pid > 0)
                        {
                            try
                            {
                                using var proc = Process.GetProcessById((int)pid);
                                procName = proc.ProcessName;
                            }
                            catch { procName = $"(pid {pid} — exited or access denied)"; }
                        }
                        else procName = "(system / no PID)";

                        bool isSys = false;
                        try { isSys = sess.IsSystemSoundsSession; } catch { }

                        string display = "";
                        try { display = sess.DisplayName ?? ""; } catch { }

                        Log($"    Session[{s}]: PID={pid,-6} proc={procName,-30} " +
                            $"state={sess.State}  sysSounds={isSys}" +
                            (display.Length > 0 ? $"  display=\"{display}\"" : ""));
                    }
                    catch (Exception ex) { Log($"    Session[{s}]: [error: {ex.Message}]"); }
                }
            }
            catch (Exception ex) { Log($"  [endpoint error: {ex.Message}]"); }
        }
    });

    // ─────────────────────────────────────────────────────────────────────────
    // §4  METERS  (per-endpoint peak, 3 reads ~100 ms apart)
    // ─────────────────────────────────────────────────────────────────────────
    RunSection("METERS", () =>
    {
        Log("  Per-session peak meters for foreign processes: NOT available via public WASAPI API.");
        Log("  Showing per-endpoint peaks (all sessions mixed). Exclusive-mode endpoints");
        Log("  without hardware meters always read 0.0.");
        Log();

        using var enumerator = new MMDeviceEnumerator();
        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        for (int i = 0; i < renderDevices.Count; i++)
        {
            try
            {
                var dev = renderDevices[i];
                var meter = dev.AudioMeterInformation;
                float[] reads = new float[3];
                for (int r = 0; r < 3; r++)
                {
                    reads[r] = meter.MasterPeakValue;
                    if (r < 2) Thread.Sleep(100);
                }
                float max = reads.Max();
                bool clip = max >= 0.99f;
                Log($"  {dev.FriendlyName}");
                Log($"    Reads : [{reads[0]:F4}, {reads[1]:F4}, {reads[2]:F4}]  max={max:F4}" +
                    (clip ? "  *** CLIPPING / NEAR-CLIP ***" : ""));

                try
                {
                    int nCh = meter.PeakValues.Count;
                    var chStr = string.Join(", ", Enumerable.Range(0, nCh)
                        .Select(c => meter.PeakValues[c].ToString("F4")));
                    Log($"    Channels ({nCh}): [{chStr}]");
                }
                catch { }
            }
            catch (Exception ex) { Log($"  [meter error: {ex.Message}]"); }
            Log();
        }
    });

    // ─────────────────────────────────────────────────────────────────────────
    // §5  VIRTUAL CABLES
    // ─────────────────────────────────────────────────────────────────────────
    RunSection("VIRTUAL CABLES", () =>
    {
        // Known endpoint name substrings → from virtual-cables.md §6
        var cablePatterns = new (string Pattern, string Label)[]
        {
            ("VB-Audio Virtual Cable",  "VB-CABLE (free)"),
            ("VB-Audio Cable A",        "VB-CABLE A"),
            ("VB-Audio Cable B",        "VB-CABLE B"),
            ("VB-Audio Cable C",        "VB-CABLE C"),
            ("VB-Audio Cable D",        "VB-CABLE D"),
            ("VB-Audio Hi-Fi Cable",    "Hi-Fi Cable"),
            ("VoiceMeeter",             "VoiceMeeter"),
            ("VB-Audio VoiceMeeter",    "VoiceMeeter VAIO"),
            ("Virtual Audio Cable",     "VAC (Muzychenko)"),
            ("CABLE Input",             "VB-CABLE Input side"),
            ("CABLE Output",            "VB-CABLE Output side"),
            ("HI-FI Cable",             "Hi-Fi Cable"),
        };

        Log("  ── Endpoint name scan ──");
        bool anyVirtual = false;
        foreach (var ep in endpointCache)
        {
            foreach (var (pattern, label) in cablePatterns)
            {
                if (ep.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"  DETECTED: {label}");
                    Log($"    Endpoint : {ep.Name}  [{ep.Flow} / {ep.State}]");
                    if (ep.MixSampleRate > 0)
                        Log($"    Format   : {ep.MixSampleRate} Hz / {ep.MixBits} bit / {ep.MixCh} ch");
                    anyVirtual = true;
                    break;
                }
            }
        }
        if (!anyVirtual) Log("  No virtual cable endpoints detected.");

        // ── VoiceMeeter process check ─────────────────────────────────────
        Log();
        Log("  ── VoiceMeeter process check ──");
        var vmProcs = new (string Exe, string Tier)[]
        {
            ("voicemeeter",        "Standard"),
            ("voicemeeterpro",     "Banana"),
            ("voicemeeterpro_x64", "Banana x64"),
            ("voicemeeter8",       "Potato"),
            ("voicemeeter8x64",    "Potato x64"),
        };
        bool vmRunning = false;
        foreach (var (exe, tier) in vmProcs)
            if (IsRunning(exe)) { Log($"  RUNNING: VoiceMeeter {tier} ({exe}.exe)"); vmRunning = true; }

        bool vmEndpoints = endpointCache.Any(e =>
            e.Name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase));
        if (vmEndpoints && !vmRunning)
            Log("  WARNING: VoiceMeeter endpoints exist but engine is NOT RUNNING — audio sent there is discarded!");
        else if (!vmEndpoints && !vmRunning)
            Log("  VoiceMeeter: not installed / not detected.");

        // ── VAC driver file ───────────────────────────────────────────────
        Log();
        Log("  ── Virtual Audio Cable (VAC) driver ──");
        bool vacDriver = File.Exists(@"C:\Windows\System32\drivers\vrtaucbl.sys");
        Log($"  vrtaucbl.sys : {(vacDriver ? "PRESENT" : "not found")}");
        if (vacDriver && !endpointCache.Any(e =>
            e.Name.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase)))
            Log("  NOTE: VAC driver installed but no 'Virtual Audio Cable' endpoint currently active.");

        // ── Sample-rate consistency check across all active endpoints ─────
        Log();
        Log("  ── Sample-rate consistency ──");
        var activeSRs = endpointCache
            .Where(e => e.State == DeviceState.Active && e.MixSampleRate > 0)
            .GroupBy(e => e.MixSampleRate)
            .ToList();

        if (activeSRs.Count == 0) { Log("  (no active endpoints with format info)"); }
        else if (activeSRs.Count == 1) { Log($"  All active endpoints at {activeSRs[0].Key} Hz — consistent."); }
        else
        {
            Log("  SR MISMATCH across active endpoints — resampling will occur somewhere:");
            foreach (var g in activeSRs)
                Log($"    {g.Key} Hz : {string.Join(" | ", g.Select(e => e.Name))}");
        }

        // ── WASAPI exclusive-mode probe ───────────────────────────────────
        Log();
        Log("  ── Exclusive-mode probe (AUDCLNT_E_DEVICE_IN_USE check) ──");
        using var en2 = new MMDeviceEnumerator();
        try
        {
            var renders = en2.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int i = 0; i < renders.Count; i++)
            {
                try
                {
                    var dev = renders[i];
                    _ = dev.AudioClient.MixFormat; // fails if exclusive-locked
                    Log($"  {dev.FriendlyName}: shared-mode probe OK");
                }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x88890004))
                {
                    Log($"  EXCLUSIVE-LOCKED: [AUDCLNT_E_DEVICE_IN_USE]");
                }
                catch (Exception ex)
                {
                    Log($"  [probe error: {ex.Message}]");
                }
            }
        }
        catch (Exception ex) { Log($"  [exclusive probe failed: {ex.Message}]"); }
    });

    // ─────────────────────────────────────────────────────────────────────────
    // §6  DJ SOFTWARE
    // ─────────────────────────────────────────────────────────────────────────
    RunSection("DJ SOFTWARE", () =>
    {
        var up   = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var lad  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var ad   = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // ── Traktor Pro 3 / 4 ─────────────────────────────────────────────
        Log("  ── Traktor Pro ──");
        bool traktorRunning = IsRunning("traktor") || IsRunning("traktor3") || IsRunning("traktor4");
        Log($"  Process: {(traktorRunning ? "RUNNING" : "not running")}");
        try
        {
            string niBase = Path.Combine(docs, "Native Instruments");
            if (Directory.Exists(niBase))
            {
                var dirs = Directory.GetDirectories(niBase, "Traktor*")
                    .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                    .Take(3);
                bool foundAny = false;
                foreach (var dir in dirs)
                {
                    foundAny = true;
                    string tsi = Path.Combine(dir, "Traktor Settings.tsi");
                    bool exists = File.Exists(tsi);
                    Log($"  Config dir   : {RedactPath(dir)}");
                    if (exists)
                    {
                        var fi = new FileInfo(tsi);
                        Log($"  Settings.tsi : FOUND  {fi.Length:N0} bytes  last-write={fi.LastWriteTime:yyyy-MM-dd HH:mm}");
                        // Parse audio-relevant Entry elements (keys containing DeviceIO/Audio/Latency/SampleRate)
                        try
                        {
                            var xml = XDocument.Load(tsi);
                            var entries = xml.Descendants("Entry")
                                .Where(el =>
                                {
                                    var n = (string?)el.Attribute("Name") ?? "";
                                    return n.Contains("DeviceIO", StringComparison.OrdinalIgnoreCase)
                                        || n.Contains("SampleRate", StringComparison.OrdinalIgnoreCase)
                                        || n.Contains("Latency", StringComparison.OrdinalIgnoreCase)
                                        || n.Contains("AudioDevice", StringComparison.OrdinalIgnoreCase);
                                })
                                .Take(50);
                            foreach (var e in entries)
                                Log($"    Entry: Name={e.Attribute("Name")?.Value}  Type={e.Attribute("Type")?.Value}  Value={e.Attribute("Value")?.Value}");
                            Log("  NOTE: Traktor audio Entry names are undocumented — see dj-software-settings.md §1.2");
                        }
                        catch (Exception ex) { Log($"    [TSI parse error: {ex.Message}]"); }
                    }
                    else Log($"  Settings.tsi : NOT FOUND at {RedactPath(tsi)}");
                }
                if (!foundAny) Log($"  No Traktor folder found in {RedactPath(niBase)}");
            }
            else Log($"  {RedactPath(niBase)} not found — Traktor not installed.");
        }
        catch (Exception ex) { Log($"  [Traktor probe error: {ex.Message}]"); }

        // ── rekordbox 6 / 7 ──────────────────────────────────────────────
        Log();
        Log("  ── rekordbox ──");
        Log($"  Process: {(IsRunning("rekordbox") ? "RUNNING" : "not running")}");
        try
        {
            string rbDir      = Path.Combine(ad, "Pioneer", "rekordbox");
            string rbSettings = Path.Combine(rbDir, "rekordbox3.settings");
            Log($"  Config dir          : {RedactPath(rbDir)}  {(Directory.Exists(rbDir) ? "(exists)" : "(NOT FOUND)")}");
            if (File.Exists(rbSettings))
            {
                var fi = new FileInfo(rbSettings);
                Log($"  rekordbox3.settings : FOUND  {fi.Length:N0} bytes  last-write={fi.LastWriteTime:yyyy-MM-dd HH:mm}");
            }
            else Log($"  rekordbox3.settings : NOT FOUND");
            Log("  NOTE: rekordbox audio config keys are undocumented — path+size only (dj-software-settings.md §2.2)");
        }
        catch (Exception ex) { Log($"  [rekordbox probe error: {ex.Message}]"); }

        // ── Serato DJ ─────────────────────────────────────────────────────
        Log();
        Log("  ── Serato DJ ──");
        Log($"  Process: {(IsRunning("seratodj") || IsRunning("seratodj64") ? "RUNNING" : "not running")}");
        try
        {
            string seratoMusic = Path.Combine(up, "Music", "_Serato_");
            string seratoApp   = Path.Combine(lad, "Serato");
            Log($"  Library folder : {RedactPath(seratoMusic)}  {(Directory.Exists(seratoMusic) ? "(exists)" : "(NOT FOUND)")}");
            Log($"  AppData folder : {RedactPath(seratoApp)}  {(Directory.Exists(seratoApp) ? "(exists)" : "(NOT FOUND)")}");

            if (Directory.Exists(seratoApp))
            {
                foreach (var f in Directory.EnumerateFiles(seratoApp, "*.pref",
                    SearchOption.AllDirectories).Take(10))
                    Log($"    .pref : {RedactPath(f)}  {new FileInfo(f).Length:N0} bytes");
            }

            // Registry — audio/device-relevant keys only; skip credentials
            Log("  Registry HKCU\\Software\\Serato\\SeratoDJ (audio keys only):");
            using var seratoKey = Registry.CurrentUser.OpenSubKey(@"Software\Serato\SeratoDJ");
            if (seratoKey != null)
            {
                static bool IsSeratoAudioKey(string n)
                {
                    var l = n.ToLowerInvariant();
                    return l.Contains("buffer") || l.Contains("samplerate") ||
                           l.Contains("audio")  || l.Contains("device") ||
                           l.Contains("latency") || l.Contains("asio") ||
                           l.Contains("wasapi")  || l.Contains("driver") ||
                           l.Contains("dvs")    || l.Contains("monitor") ||
                           l.Contains("headphone") || l.Contains("mixer") ||
                           l.Contains("output") || l.Contains("input") ||
                           l.Contains("plugin_enabled");
                }
                int found = 0;
                foreach (var valName in seratoKey.GetValueNames())
                {
                    if (LooksLikeCredential(valName)) continue;
                    if (!IsSeratoAudioKey(valName)) continue;
                    var val = seratoKey.GetValue(valName);
                    string valStr = val?.ToString() ?? "(null)";
                    if (valStr.Contains(up, StringComparison.OrdinalIgnoreCase))
                        valStr = RedactPath(valStr);
                    Log($"    {valName} = {valStr}");
                    found++;
                }
                if (found == 0) Log("    (no audio-relevant values found)");
            }
            else Log("    HKCU\\Software\\Serato\\SeratoDJ not found.");
            Log("  NOTE: Serato .pref format is undocumented binary — not parsed (see serato-pref-reverse.md).");
        }
        catch (Exception ex) { Log($"  [Serato probe error: {ex.Message}]"); }

        // ── VirtualDJ ─────────────────────────────────────────────────────
        Log();
        Log("  ── VirtualDJ ──");
        Log($"  Process: {(IsRunning("virtualdj") || IsRunning("virtualdj8") ? "RUNNING" : "not running")}");
        try
        {
            string vdjHome = Path.Combine(lad, "VirtualDJ");
            try
            {
                using var vdjKey = Registry.CurrentUser.OpenSubKey(@"Software\VirtualDJ");
                var hf = vdjKey?.GetValue("HomeFolder") as string;
                if (!string.IsNullOrEmpty(hf)) vdjHome = hf;
            }
            catch { }

            string vdjSettings = Path.Combine(vdjHome, "settings.xml");
            Log($"  Home folder  : {RedactPath(vdjHome)}");
            Log($"  settings.xml : {(File.Exists(vdjSettings) ? $"FOUND  {new FileInfo(vdjSettings).Length:N0} bytes" : "NOT FOUND")}");

            if (File.Exists(vdjSettings))
            {
                var audioKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "sampleRate", "latency", "ultraLatency", "exclusiveAudioAccess",
                    "splitHeadphones", "headphonesGain", "zeroDB",
                    "pitchQuality", "scratchFilterQuality",
                };
                try
                {
                    var xml = XDocument.Load(vdjSettings);
                    Log("  Audio-relevant options:");
                    bool anyFound = false;
                    foreach (var opt in xml.Descendants("option"))
                    {
                        var nm = (string?)opt.Attribute("name") ?? "";
                        if (audioKeys.Contains(nm))
                        {
                            Log($"    {nm} = {opt.Attribute("value")?.Value}");
                            anyFound = true;
                        }
                    }
                    if (!anyFound) Log("    (none found — may be at default values and not written)");
                }
                catch (Exception ex) { Log($"    [XML parse error: {ex.Message}]"); }
            }
        }
        catch (Exception ex) { Log($"  [VirtualDJ probe error: {ex.Message}]"); }

        // ── Mixxx ─────────────────────────────────────────────────────────
        Log();
        Log("  ── Mixxx ──");
        Log($"  Process: {(IsRunning("mixxx") ? "RUNNING" : "not running")}");
        try
        {
            string mixxxDir = Path.Combine(lad, "Mixxx");
            string soundCfg = Path.Combine(mixxxDir, "soundconfig.xml");
            string mixxxCfg = Path.Combine(mixxxDir, "mixxx.cfg");
            Log($"  Config dir     : {RedactPath(mixxxDir)}  {(Directory.Exists(mixxxDir) ? "(exists)" : "(NOT FOUND)")}");
            Log($"  soundconfig.xml: {(File.Exists(soundCfg) ? $"FOUND  {new FileInfo(soundCfg).Length:N0} bytes" : "NOT FOUND")}");
            Log($"  mixxx.cfg      : {(File.Exists(mixxxCfg)  ? $"FOUND  {new FileInfo(mixxxCfg).Length:N0} bytes"  : "NOT FOUND")}");

            if (File.Exists(soundCfg))
            {
                try
                {
                    var xml = XDocument.Load(soundCfg);
                    Log("  soundconfig.xml (device assignments):");
                    foreach (var el in xml.Descendants().Where(e => !e.HasElements))
                        Log($"    {el.Name}: {el.Value}");
                }
                catch (Exception ex) { Log($"    [soundconfig.xml error: {ex.Message}]"); }
            }

            if (File.Exists(mixxxCfg))
            {
                try
                {
                    Log("  mixxx.cfg [Soundcard/SoundDevices/SoundManager sections]:");
                    bool inSection = false;
                    foreach (var line in File.ReadAllLines(mixxxCfg))
                    {
                        if (line.StartsWith('['))
                        {
                            inSection = line.StartsWith("[Soundcard",   StringComparison.OrdinalIgnoreCase)
                                     || line.StartsWith("[SoundDevices", StringComparison.OrdinalIgnoreCase)
                                     || line.StartsWith("[SoundManager", StringComparison.OrdinalIgnoreCase);
                            if (inSection) Log($"  Section: {line}");
                        }
                        else if (inSection && line.Contains('='))
                            Log($"    {line}");
                    }
                }
                catch (Exception ex) { Log($"    [mixxx.cfg error: {ex.Message}]"); }
            }
        }
        catch (Exception ex) { Log($"  [Mixxx probe error: {ex.Message}]"); }
    });

    // ─────────────────────────────────────────────────────────────────────────
    // §7  STREAMING TOOLS
    // ─────────────────────────────────────────────────────────────────────────
    RunSection("STREAMING TOOLS", () =>
    {
        var ad  = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // ── BUTT ─────────────────────────────────────────────────────────
        Log("  ── BUTT ──");
        Log($"  Process: {(IsRunning("butt") ? "RUNNING" : "not running")}");
        try
        {
            string buttCfg = Path.Combine(ad, "buttrc");
            Log($"  Config: {RedactPath(buttCfg)}  {(File.Exists(buttCfg) ? $"FOUND  {new FileInfo(buttCfg).Length:N0} bytes" : "NOT FOUND")}");
            if (File.Exists(buttCfg))
            {
                // Only log audio codec/bitrate/SR/channel fields.
                // NEVER log: addr, ip, port, pass, user, mount, server, host, url.
                var audioFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "codec", "bitrate", "samplerate", "ch", "channel", "channels",
                    "mp3_bitrate", "aac_bitrate", "ogg_bitrate", "opus_bitrate",
                    "rec_codec", "rec_bitrate", "rec_samplerate", "rec_ch", "gain",
                };
                var credFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "addr", "ip", "port", "pass", "pwd", "user", "username",
                    "mount", "mountpoint", "server", "host", "url", "stream_name",
                    "password", "name",
                };
                Log("  Audio settings:");
                foreach (var line in File.ReadAllLines(buttCfg))
                {
                    var eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var key = line[..eq].Trim();
                    var val = line[(eq + 1)..].Trim();
                    if (credFields.Contains(key)) continue;  // skip credentials
                    if (audioFields.Contains(key)) Log($"    {key} = {val}");
                }
            }
        }
        catch (Exception ex) { Log($"  [BUTT probe error: {ex.Message}]"); }

        // ── OBS Studio ───────────────────────────────────────────────────
        Log();
        Log("  ── OBS Studio ──");
        Log($"  Process: {(IsRunning("obs64") || IsRunning("obs32") || IsRunning("obs") ? "RUNNING" : "not running")}");
        try
        {
            string obsBase = Path.Combine(ad, "obs-studio");
            Log($"  Config dir: {RedactPath(obsBase)}  {(Directory.Exists(obsBase) ? "(exists)" : "(NOT FOUND)")}");

            string profilesDir = Path.Combine(obsBase, "basic", "profiles");
            if (Directory.Exists(profilesDir))
            {
                // Only log audio-relevant fields; NEVER log StreamKey, Server, URL, auth
                var obsAudioKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "ABitrate", "SampleRate", "ChannelSetup", "MixersCount",
                    "AudioEncoder", "RecABitrate", "RecSampleRate",
                };
                foreach (var profileDir in Directory.GetDirectories(profilesDir))
                {
                    string basic = Path.Combine(profileDir, "basic.ini");
                    if (!File.Exists(basic)) continue;
                    Log($"  Profile: {Path.GetFileName(profileDir)}");
                    bool inAudio = false;
                    foreach (var line in File.ReadAllLines(basic))
                    {
                        if (line.StartsWith('['))
                        {
                            inAudio = line.Equals("[SimpleOutput]", StringComparison.OrdinalIgnoreCase)
                                   || line.Equals("[AdvOut]", StringComparison.OrdinalIgnoreCase)
                                   || line.Equals("[Audio]", StringComparison.OrdinalIgnoreCase);
                            continue;
                        }
                        if (!inAudio) continue;
                        var eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        var key = line[..eq].Trim();
                        if (obsAudioKeys.Contains(key)) Log($"    {key} = {line[(eq + 1)..].Trim()}");
                    }
                }
            }
        }
        catch (Exception ex) { Log($"  [OBS probe error: {ex.Message}]"); }

        // ── Rocket Broadcaster ────────────────────────────────────────────
        Log();
        Log("  ── Rocket Broadcaster ──");
        Log($"  Process: {(IsRunning("RocketBroadcaster") ? "RUNNING" : "not running")}");
        Log("  Config: user-saved project file (no guaranteed auto-save path — check manually).");

        // ── SAM Broadcaster ───────────────────────────────────────────────
        Log();
        Log("  ── SAM Broadcaster ──");
        Log($"  Process: {(IsRunning("SAM2") || IsRunning("sam_broadcaster") ? "RUNNING" : "not running")}");
        try
        {
            // Path is community-sourced; flagged [unverified] in streaming-tools.md
            string samCfg = Path.Combine(lad, "SpacialAudio", "SAMBC", "SAMBC.core.xml");
            Log($"  Config (⚠ unverified path): {RedactPath(samCfg)}  {(File.Exists(samCfg) ? $"FOUND  {new FileInfo(samCfg).Length:N0} bytes" : "NOT FOUND")}");
        }
        catch (Exception ex) { Log($"  [SAM probe error: {ex.Message}]"); }

        // ── RadioBOSS ─────────────────────────────────────────────────────
        Log();
        Log("  ── RadioBOSS ──");
        Log($"  Process: {(IsRunning("RadioBOSS") ? "RUNNING" : "not running")}");
        try
        {
            string rbossBase = Path.Combine(ad, "djsoft.net");
            Log($"  Config base: {RedactPath(rbossBase)}  {(Directory.Exists(rbossBase) ? "(exists)" : "(NOT FOUND)")}");
            if (Directory.Exists(rbossBase))
                foreach (var d in Directory.GetDirectories(rbossBase, "RadioBOSS*"))
                    Log($"    Config dir: {RedactPath(d)}");
        }
        catch (Exception ex) { Log($"  [RadioBOSS probe error: {ex.Message}]"); }

        // ── Altacast / Edcast ─────────────────────────────────────────────
        Log();
        Log("  ── Altacast / Edcast ──");
        Log($"  Process: {(IsRunning("altacast") || IsRunning("edcast") ? "RUNNING" : "not running")}");
        foreach (var cfgDir in new[] { @"C:\Program Files\altacast", @"C:\Program Files\edcast" })
        {
            if (!Directory.Exists(cfgDir)) continue;
            Log($"  Install dir: {cfgDir}");
            foreach (var f in Directory.GetFiles(cfgDir, "*.cfg").Take(5))
                Log($"    Config: {Path.GetFileName(f)}  {new FileInfo(f).Length:N0} bytes");
        }
    });

    // ─────────────────────────────────────────────────────────────────────────
    // §8  FOOTER
    // ─────────────────────────────────────────────────────────────────────────
    Banner("FOOTER");
    Log($"  Sections completed OK  : {sectOk}");
    Log($"  Sections with errors   : {sectErr}");
    Log($"  Log file               : {logFileName}");
    Log($"  (Log is in the same folder as JustCheck.Probe.exe)");
    Log();
    Log("  PRIVACY REMINDER: anonymous audio-config data only.");
    Log("  No personal info, no credentials, no stream passwords.");
    Log();
    Log("  ==== END OF JUSTCHECK PROBE LOG ====");
}

// ── File-scope types ─────────────────────────────────────────────────────────

/// <summary>Snapshot of one audio endpoint captured in §2 and reused in §4–5.</summary>
internal sealed record EndpointInfo(
    string Id,
    string Name,
    NAudio.CoreAudioApi.DataFlow Flow,
    NAudio.CoreAudioApi.DeviceState State,
    string MixFmt,
    int MixSampleRate,
    int MixBits,
    int MixCh);
