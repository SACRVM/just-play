namespace MakeTestLib;

/// <summary>The two files the test library carries for its user: what it is, and how to get it back.</summary>
internal static class Docs
{
    public static string Readme(string projectDir) =>
        Template.Replace("{PROJECT}", projectDir, StringComparison.Ordinal);

    public static string ResetScript(string projectDir) =>
        ResetTemplate.Replace("{PROJECT}", projectDir, StringComparison.Ordinal);

    private const string Template =
"""
JUST-TESTLIB - a throwaway music library for hand testing JUST TAG's ORGANISE feature.

WHAT THIS IS

  Every audio file in here was SYNTHESISED by the generator (see the last section): a
  short, quiet tone, varying in pitch and length so you can tell one from the next in the
  preview, with real tags written by the same writer the apps use. Nothing was copied
  out of any music library and nothing in here is worth keeping. Move it, copy it,
  delete it, make a mess of it - RESET.ps1 puts it all back.

HOW TO RESET

  Right-click RESET.ps1 and "Run with PowerShell", or from a terminal:

      powershell -ExecutionPolicy Bypass -File D:\JUST-TESTLIB\RESET.ps1

  It wipes this folder and regenerates it. The result is byte for byte the same tree
  every time - same names, same sizes, same modification times - so a reset really is
  back to the start, not close to it.

  Files you deleted went to the Recycle Bin (JUST TAG never hard deletes). The reset
  does not empty it; that stays your call.

THE FOLDERS, AND WHAT EACH ONE IS FOR

  01 BULK                 30 files. Bulk selection: select the lot, move it, and see
                          whether the preview count and the progress hold up. Mixed by
                          design: 18 MP3, 6 FLAC, 4 WAV, 2 AIFF.

  02 CRATES               Nesting, three levels deep.
    HARD TECHNO           5 files.
      DEEP CUTS           3 files. The deepest folder, and a small count next to a big
                          one.
    VINAHOUSE             4 files.

                          NAME COLLISION: "Collider - Same Name Twice.mp3" is in BOTH
                          crates, and the two copies are deliberately NOT the same file -
                          different length, so a different size, and a different
                          modification time. Moving one into the other collides, and
                          anything that skips unchanged files has to notice these two
                          are not interchangeable.

  03 TARGET HAS IT        3 files. A destination that ALREADY holds a name that is about
                          to arrive: "Bulk 06 - Falcon.mp3" sits in here and in 01 BULK,
                          and this copy is byte for byte identical, down to the
                          modification time. The unchanged half of the pair above.

  04 AWKWARD NAMES        7 files whose names are legal but awkward: a leading number,
                          runs of spaces, [brackets] and (parentheses), an ampersand,
                          dots in the middle of the name, mixed case, and one name long
                          enough to be uncomfortable.
    Bits & Bobs (2026)    2 files, in a folder whose own name carries an ampersand and
                          brackets.

  05 MIXED FORMATS        4 files, one of each format, side by side in one folder.

  06 EMPTY LANDING        Empty on purpose. A clean destination with nothing in it to
                          collide with.

  Four files carry embedded cover art, so the editor has a picture to show.

ABOUT THE LIBRARY INDEX

  ORGANISE reconciles the library index only for files that sit under a REGISTERED
  library root. This tree is deliberately NOT one: the index on this machine has exactly
  one root (the NAS), and a pile of test files has no business being added to it. So
  moving things around in here changes files on disk and nothing else - which is exactly
  what you want while reviewing the file operations themselves.

  If you do want to exercise the index reconcile path, register it deliberately: open

      %LOCALAPPDATA%\JUST\library\roots.json

  and add D:\JUST-TESTLIB as a second entry in the "roots" array (JSON wants every
  backslash doubled, so it looks like the NAS entry already in there). Undo it by
  removing that entry again and deleting the .db file that appeared next to roots.json
  for this root. Registering it also means a scan will index these files - that is the
  point of doing it, but do not leave it registered afterwards.

WHERE THE GENERATOR LIVES

  {PROJECT}

  It is a build tool in the JustPlay repo and is not part of JustPlay.slnx, so it changes
  neither the solution build nor the test count.

      dotnet run --project <that folder> -c Release
          generate (refuses if this folder already exists)

      dotnet run --project <that folder> -c Release -- --reset
          wipe and regenerate - what RESET.ps1 calls

      dotnet run --project <that folder> -c Release -- --check
          run the safety checks and report, change nothing

      dotnet run --project <that folder> -c Release -- --verify
          read every file back with the app's own metadata reader and list what
          came out: format, duration, cover art, artist, album

  The reset refuses to run against any folder other than D:\JUST-TESTLIB. It refuses if
  _manifest.json is missing or belongs somewhere else, and it refuses if it finds
  anything it did not write - a stray document, a real track, a junction pointing out of
  the tree. If it ever refuses, read what it says before working around it: that check is
  the only thing standing between "reset my test files" and "delete the wrong folder".
""";

    private const string ResetTemplate =
"""
# RESET.ps1 - put D:\JUST-TESTLIB back exactly as it was.
#
# Wipes this folder and regenerates it from the generator in the JustPlay repo. The tree
# is deterministic, so what comes back is byte for byte what was here at the start.
# All the safety checks live in the generator, not here: it will only ever touch
# D:\JUST-TESTLIB, and it refuses if it finds anything it did not write itself.

$ErrorActionPreference = 'Stop'

$project = '{PROJECT}'

if (-not (Test-Path -LiteralPath $project)) {
    Write-Host "The generator is not at $project any more." -ForegroundColor Yellow
    Write-Host "Find build/make-testlib in the JustPlay repo and run it with the reset switch."
    exit 1
}

Write-Host "Resetting D:\JUST-TESTLIB ..." -ForegroundColor Cyan
dotnet run --project $project -c Release -- --reset
exit $LASTEXITCODE
""";
}
