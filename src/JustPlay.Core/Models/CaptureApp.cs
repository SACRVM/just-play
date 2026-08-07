namespace JustPlay.Core.Models;

/// <summary>
/// One application whose audio JUST STREAM can capture directly - the "capture a specific APP"
/// source (Phase 0 of the JUST STREAM capture layer; see
/// <c>.claude/skills/just-route/references/just-route-vision.md</c>).
///
/// Unlike an <see cref="AudioInputDevice"/> (a whole sound-card / loopback endpoint that mixes in
/// every system sound), a CaptureApp is ONE process's render stream - captured invisibly via
/// per-process WASAPI loopback, so a DJ can stream ONLY their rekordbox/Traktor/player with zero
/// system-sound leak, no virtual cable, and no reconfiguration.
/// </summary>
/// <param name="ProcessId">The OS process id of the app whose audio to capture.</param>
/// <param name="DisplayName">Human-friendly name for the picker (e.g. "rekordbox", "Traktor Pro").</param>
/// <param name="ExecutableName">The process's executable name, lower-cased, no extension
/// (e.g. "rekordbox", "traktor"). Stable across launches; used to remember the last-picked app.</param>
/// <param name="IsDjApp">True when the executable is a recognised DJ application - these sort to the
/// top of the picker (the streaming use case), ahead of generic media players/browsers.</param>
public sealed record CaptureApp(int ProcessId, string DisplayName, string ExecutableName, bool IsDjApp);
