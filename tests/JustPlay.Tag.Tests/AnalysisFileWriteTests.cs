using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Library;

namespace JustPlay.Tag.Tests;

/// <summary>
/// One file of JUST TAG's ANALYZE run, driven through the real batch runner so the counts are the
/// ones the run's report shows.
///
/// <para>What was wrong: the run took the per-file tag read as the file's truth even when the read
/// had FAILED. A failed read is a bare record - no blob, so no KEPT decisions and no stashed original
/// value - and writing on top of it overwrote a hand-corrected key and lost the original, over a lock
/// or a share that was gone for a second.</para>
/// </summary>
public class AnalysisFileWriteTests
{
    private const string File1 = @"\\nas\music\a.flac";

    private sealed class FakeAnalysis : ITrackAnalysisService
    {
        public Task<AnalysisResult> AnalyzeAsync(string filePath, IProgress<AnalysisResult>? progress = null,
                                                 CancellationToken ct = default) =>
            Task.FromResult(new AnalysisResult { Bpm = 128, Energy = 7 });
    }

    private sealed class FakeReader(TrackMetadata answer) : IMetadataReader
    {
        public TrackMetadata Read(string filePath) => answer;

        public EditableTags ReadEditable(string filePath) =>
            throw new InvalidOperationException("The analyze run reads the whole record.");
    }

    private sealed class RecordingWriter : IMetadataWriter
    {
        public List<(string Path, TagWrite Write)> Writes { get; } = [];
        public Exception? Throw { get; init; }

        public void Write(string filePath, TagWrite write, TagWritePolicy? policy = null)
        {
            if (Throw is not null) throw Throw;
            Writes.Add((filePath, write));
        }

        public void WriteEditable(string filePath, EditableTags tags, CoverAction coverAction,
                                  byte[]? newCover, string? coverMimeType) =>
            throw new InvalidOperationException("The analyze run never writes editorial tags.");

        public void Restore(string filePath, TagRestore restore) =>
            throw new InvalidOperationException("The analyze run never restores.");

        public void ConfigureId3WriteFormat(Id3WriteFormat format) { }
    }

    private readonly List<string> _log = [];

    private async Task<AnalysisBatchReport> Run(TrackMetadata read, RecordingWriter writer)
    {
        var step = new AnalysisFileWrite(new FakeAnalysis(), new FakeReader(read), writer,
                                         new TagWriteGate(releaseFile: _ => { }, log: _log.Add));
        var runner = new AnalysisBatchRunner(step.RunAsync, mayWorkNow: () => true);
        return await runner.RunAsync([File1]);
    }

    [Fact]
    public async Task A_file_whose_tags_could_not_be_read_is_failed_named_and_not_written()
    {
        var writer = new RecordingWriter();

        var report = await Run(new TrackMetadata { FallbackName = "a", ReadFailed = true }, writer);

        Assert.Empty(writer.Writes);
        Assert.Equal(0, report.Succeeded);
        Assert.Equal(1, report.Failed);
        var failure = Assert.Single(report.Failures);
        Assert.Equal(File1, failure.Path);
        Assert.Equal(AnalysisFileWrite.UnreadableTags, failure.Message);
    }

    /// <summary>The other side of the same flag: a file that was READ and simply carries no tags is
    /// an ordinary file to analyse, not a failure.</summary>
    [Fact]
    public async Task A_file_read_fine_but_untagged_is_analysed_and_written()
    {
        var writer = new RecordingWriter();

        var report = await Run(new TrackMetadata { FallbackName = "a" }, writer);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(0, report.Failed);
        var (path, write) = Assert.Single(writer.Writes);
        Assert.Equal(File1, path);
        Assert.Equal(128, write.Bpm);
    }

    /// <summary>A write that fails must reach the runner as a failure. Through the editor's entry
    /// point it would have come back as an outcome code and been counted as analysed.</summary>
    [Fact]
    public async Task A_write_that_fails_is_counted_as_failed()
    {
        var writer = new RecordingWriter { Throw = new IOException("The media is write protected.") };

        var report = await Run(new TrackMetadata { FallbackName = "a" }, writer);

        Assert.Equal(0, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Equal("The media is write protected.", Assert.Single(report.Failures).Message);
    }
}
