using JustPlay.Cli.Commands;
using JustPlay.Core.Storage;

namespace JustPlay.Cli.Tests;

/// <summary>
/// Where <c>justplay tag clean</c> puts its undo file. Nothing is ever written next to the music, so
/// without <c>--backup-dir</c> it goes to the suite's own data folder, named after the run.
/// </summary>
public sealed class CleanCommentsBackupTests
{
    private static readonly DateTime Started = new(2026, 10, 6, 14, 22, 33);

    private static readonly string Music = Path.Combine(Path.GetTempPath(), "music");

    private static string Backups => JustDataPaths.Combine("JUST", "backups");

    [Fact]
    public void A_root_run_backs_up_to_the_suite_data_folder_not_into_the_music_root()
    {
        var root = Path.Combine(Music, "GENRES");

        var path = CleanCommentsCommand.BackupPathFor(null, root, sourceIsPlaylist: false, Started);

        Assert.Equal(Path.Combine(Backups, "tag-clean-20261006-142233-GENRES.json"), path);
    }

    [Fact]
    public void A_playlist_run_backs_up_to_the_suite_data_folder_not_into_the_playlists_folder()
    {
        var playlist = Path.Combine(Music, "SETS", "friday-set.m3u8");

        var path = CleanCommentsCommand.BackupPathFor(null, playlist, sourceIsPlaylist: true, Started);

        Assert.Equal(Path.Combine(Backups, "tag-clean-20261006-142233-friday-set.json"), path);
    }

    [Fact]
    public void A_share_root_still_names_the_run()
    {
        // A UNC share root has no file name of its own; the share's name is what identifies it.
        var path = CleanCommentsCommand.BackupPathFor(
            null, TestPath.Local(@"\\nas\music\"), sourceIsPlaylist: false, Started);

        Assert.Equal(Path.Combine(Backups, "tag-clean-20261006-142233-music.json"), path);
    }

    [Fact]
    public void The_run_name_is_made_safe_for_a_file_name()
    {
        var root = Path.Combine(Music, "Hard Techno (2026)");

        var path = CleanCommentsCommand.BackupPathFor(null, root, sourceIsPlaylist: false, Started);

        Assert.Equal("tag-clean-20261006-142233-Hard_Techno__2026_.json", Path.GetFileName(path));
    }

    [Fact]
    public void Two_runs_never_share_a_backup_file()
    {
        // A second run backs up already-cleaned values. It must neither be refused because the first
        // run's file exists nor replace that file, which holds the originals.
        var first  = CleanCommentsCommand.BackupPathFor(null, Music, false, Started);
        var second = CleanCommentsCommand.BackupPathFor(null, Music, false, Started.AddMinutes(3));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void An_explicit_backup_dir_is_still_honoured_with_the_fixed_name()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bak");

        var path = CleanCommentsCommand.BackupPathFor(
            dir, Path.Combine(Music, "GENRES"), sourceIsPlaylist: false, Started);

        Assert.Equal(Path.Combine(dir, CleanCommentsCommand.BackupFileName), path);
    }
}
