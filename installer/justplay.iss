; JustPlay — Inno Setup installer.
;
; Per-user install (no UAC / admin): drops the self-contained single-file app plus its
; native sidecars (Avalonia Skia/HarfBuzz + BASS / BASS_FX / BASSmix / BASSenc) into
; %LOCALAPPDATA%\Programs\JustPlay, adds Start-menu (+ optional desktop) shortcuts, and
; registers a clean uninstaller. The headless JustPlayCLI.exe ships in the SAME folder
; (it shares the app's runtime, BASS natives, ONNX runtime and keymodel.onnx — one copy
; each; for agent/power-user library tagging+sorting). JUST STREAM will join the same dir.
;
; Build it via build\publish-installer.ps1 (publishes the app, then runs ISCC with the
; version from Directory.Build.props). AppVersion can be overridden on the command line:
;   ISCC.exe /DAppVersion=0.1.0 installer\justplay.iss

#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif

#define AppName "JustPlay"
#define AppPublisher "Chloe Dream"
#define AppExe "JustPlay.exe"

[Setup]
; AppId must stay STABLE across versions so upgrades replace in place (never change it).
AppId={{D150D1FA-86CB-4A28-B684-2608DDABE567}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/chloe-dream/just-play
AppSupportURL=https://github.com/chloe-dream/just-play/issues
AppUpdatesURL=https://github.com/chloe-dream/just-play/releases
; Per-user, no elevation — installs under the user's profile.
DefaultDirName={localappdata}\Programs\JustPlay
DefaultGroupName=JustPlay
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\bin\installer
OutputBaseFilename=JustPlaySetup-{#AppVersion}-win-x64
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\JustPlay.App\Assets\justplay.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; File associations — one individually-checkable box per format. Double-clicking an
; associated file opens it in JustPlay (added to the running queue, not a new window).
; Common formats are pre-checked; rarer ones start unchecked.
Name: "assoc_mp3";  Description: ".mp3";        GroupDescription: "Open these in JustPlay on double-click:"
Name: "assoc_flac"; Description: ".flac";       GroupDescription: "Open these in JustPlay on double-click:"
Name: "assoc_wav";  Description: ".wav";        GroupDescription: "Open these in JustPlay on double-click:"
Name: "assoc_m4a";  Description: ".m4a";        GroupDescription: "Open these in JustPlay on double-click:"
Name: "assoc_aac";  Description: ".aac";        GroupDescription: "Open these in JustPlay on double-click:"; Flags: unchecked
Name: "assoc_ogg";  Description: ".ogg";        GroupDescription: "Open these in JustPlay on double-click:"; Flags: unchecked
Name: "assoc_opus"; Description: ".opus";       GroupDescription: "Open these in JustPlay on double-click:"; Flags: unchecked
Name: "assoc_wma";  Description: ".wma";        GroupDescription: "Open these in JustPlay on double-click:"; Flags: unchecked
Name: "assoc_aiff"; Description: ".aiff / .aif"; GroupDescription: "Open these in JustPlay on double-click:"; Flags: unchecked
Name: "assoc_m3u";  Description: ".m3u8 / .m3u playlists (opening one loads the whole set)"; GroupDescription: "Open these in JustPlay on double-click:"

[InstallDelete]
; Remove the pre-0.3.1 GUI exe (rebrand JustPlay.App.exe -> JustPlay.exe, commit a3927aa). Without
; this, an over-install leaves the old exe behind and a pre-rebrand build's auto-updater relaunches
; it -> the app appears to "downgrade" to the old version on every restart (the 0.3.0<->0.3.1 loop).
Type: files; Name: "{app}\JustPlay.App.exe"

[Files]
; The entire self-contained suite drop, shipped verbatim into one folder: JustPlay.exe +
; JustPlayCLI.exe (+ JustPlayCLI.howto.txt) sharing ONE copy of the .NET runtime, the
; Avalonia natives (libSkiaSharp / av_libglesv2 / libHarfBuzzSharp), the BASS natives, the
; ONNX runtime and keymodel.onnx. build\publish-win-x64.ps1 produces this folder.
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Registry]
; ── ProgID: the JustPlay audio-file handler (always registered under the per-user hive). ──
Root: HKCU; Subkey: "Software\Classes\JustPlay.AudioFile"; ValueType: string; ValueData: "Audio File"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\JustPlay.AudioFile\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"
Root: HKCU; Subkey: "Software\Classes\JustPlay.AudioFile\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""

; ── Per-extension association (only for the ticked formats). Two entries each: ──
;   • OpenWithProgids — non-destructive; makes JustPlay appear in "Open with" / the chooser.
;   • the extension's default ProgID — takes effect where Windows allows it (no prior UserChoice).
;     Already-associated types (e.g. .mp3) keep their UserChoice until the user picks JustPlay
;     once via "Open with → Always" or Settings → Default apps (a Windows lockdown, not ours).
Root: HKCU; Subkey: "Software\Classes\.mp3\OpenWithProgids";  ValueType: string; ValueName: "JustPlay.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_mp3
Root: HKCU; Subkey: "Software\Classes\.mp3";  ValueType: string; ValueData: "JustPlay.AudioFile"; Flags: uninsdeletevalue; Tasks: assoc_mp3
Root: HKCU; Subkey: "Software\Classes\.flac\OpenWithProgids"; ValueType: string; ValueName: "JustPlay.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_flac
Root: HKCU; Subkey: "Software\Classes\.flac"; ValueType: string; ValueData: "JustPlay.AudioFile"; Flags: uninsdeletevalue; Tasks: assoc_flac
Root: HKCU; Subkey: "Software\Classes\.wav\OpenWithProgids";  ValueType: string; ValueName: "JustPlay.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_wav
Root: HKCU; Subkey: "Software\Classes\.wav";  ValueType: string; ValueData: "JustPlay.AudioFile"; Flags: uninsdeletevalue; Tasks: assoc_wav
Root: HKCU; Subkey: "Software\Classes\.m4a\OpenWithProgids";  ValueType: string; ValueName: "JustPlay.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_m4a
Root: HKCU; Subkey: "Software\Classes\.m4a";  ValueType: string; ValueData: "JustPlay.AudioFile"; Flags: uninsdeletevalue; Tasks: assoc_m4a
Root: HKCU; Subkey: "Software\Classes\.aac\OpenWithProgids";  ValueType: string; ValueName: "JustPlay.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_aac
Root: HKCU; Subkey: "Software\Classes\.aac";  ValueType: string; ValueData: "JustPlay.AudioFile"; Flags: uninsdeletevalue; Tasks: assoc_aac
Root: HKCU; Subkey: "Software\Classes\.ogg\OpenWithProgids";  ValueType: string; ValueName: "JustPlay.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_ogg
Root: HKCU; Subkey: "Software\Classes\.ogg";  ValueType: string; ValueData: "JustPlay.AudioFile"; Flags: uninsdeletevalue; Tasks: assoc_ogg
Root: HKCU; Subkey: "Software\Classes\.opus\OpenWithProgids"; ValueType: string; ValueName: "JustPlay.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_opus
Root: HKCU; Subkey: "Software\Classes\.opus"; ValueType: string; ValueData: "JustPlay.AudioFile"; Flags: uninsdeletevalue; Tasks: assoc_opus
Root: HKCU; Subkey: "Software\Classes\.wma\OpenWithProgids";  ValueType: string; ValueName: "JustPlay.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_wma
Root: HKCU; Subkey: "Software\Classes\.wma";  ValueType: string; ValueData: "JustPlay.AudioFile"; Flags: uninsdeletevalue; Tasks: assoc_wma
Root: HKCU; Subkey: "Software\Classes\.aiff\OpenWithProgids"; ValueType: string; ValueName: "JustPlay.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_aiff
Root: HKCU; Subkey: "Software\Classes\.aiff"; ValueType: string; ValueData: "JustPlay.AudioFile"; Flags: uninsdeletevalue; Tasks: assoc_aiff
Root: HKCU; Subkey: "Software\Classes\.aif\OpenWithProgids";  ValueType: string; ValueName: "JustPlay.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_aiff
Root: HKCU; Subkey: "Software\Classes\.aif";  ValueType: string; ValueData: "JustPlay.AudioFile"; Flags: uninsdeletevalue; Tasks: assoc_aiff

; ── Playlist ProgID (.m3u8 / .m3u) — opening one REPLACES the queue with the whole set. ──
Root: HKCU; Subkey: "Software\Classes\JustPlay.Playlist"; ValueType: string; ValueData: "JustPlay Playlist"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\JustPlay.Playlist\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"
Root: HKCU; Subkey: "Software\Classes\JustPlay.Playlist\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.m3u8\OpenWithProgids"; ValueType: string; ValueName: "JustPlay.Playlist"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_m3u
Root: HKCU; Subkey: "Software\Classes\.m3u8"; ValueType: string; ValueData: "JustPlay.Playlist"; Flags: uninsdeletevalue; Tasks: assoc_m3u
Root: HKCU; Subkey: "Software\Classes\.m3u\OpenWithProgids"; ValueType: string; ValueName: "JustPlay.Playlist"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_m3u
Root: HKCU; Subkey: "Software\Classes\.m3u"; ValueType: string; ValueData: "JustPlay.Playlist"; Flags: uninsdeletevalue; Tasks: assoc_m3u

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
