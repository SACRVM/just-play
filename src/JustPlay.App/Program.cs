using System;
using System.Collections.Generic;
using System.Net.Http;
using Avalonia;
using JustPlay.Analysis;
using JustPlay.App.Settings;
using JustPlay.UI.Theming;
using JustPlay.App.ViewModels;
using JustPlay.Audio.Bass;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Playback;
using JustPlay.Core.Updates;
using JustPlay.Metadata;
using JustPlay.ML;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.App;

sealed class Program
{
    /// <summary>Composition root — the one place that knows the concrete backends.</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    /// <summary>The single-instance gate (owned by the primary process). App wires its pipe
    /// server to the running ViewModel; null on the CLI-harness paths.</summary>
    public static SingleInstance? Single { get; private set; }

    /// <summary>Files this process was launched with (file association / double-click), handed
    /// to the ViewModel once the window exists. AddOnly = the "Add to JustPlay" verb (append,
    /// don't start playback); otherwise "open" (play if nothing is playing yet).</summary>
    public static (IReadOnlyList<string> Files, bool AddOnly) PendingOpen { get; private set; }
        = (Array.Empty<string>(), false);

    [STAThread]
    public static void Main(string[] args)
    {
        // ── Crash safety net (2026: try/catch is table stakes) ───────────────────────────────────
        // Anything that escapes a local try/catch is logged + shown in a copyable Oops dialog instead
        // of killing the app. The UI-thread handler marks the error Handled so the app keeps running;
        // background-task and AppDomain-fatal throws are at least captured + reported.
        System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ErrorReporter.Report(e.ExceptionObject as System.Exception, "AppDomain.UnhandledException (fatal)");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorReporter.Report(e.Exception, "Unobserved background-task exception");
            e.SetObserved();
        };
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            ErrorReporter.Report(e.Exception, "UI thread");
            e.Handled = true;   // keep the app alive instead of crashing
        };

        Services = ConfigureServices();

        // Headless validation tool: `--key-report <folder>` compares our key
        // detection against the key already tagged in each file (e.g. Mixed In Key)
        // and prints an accuracy summary, then exits without opening the UI.
        if (args is ["--key-report", var folder, ..])
        {
            KeyReport.Run(Services, folder);
            return;
        }

        // Tuning harness: `--key-sweep <folder>` decodes + builds each chroma ONCE and
        // re-scores it under a range of MinorBias values, so the relative-tie bias can be
        // tuned for the cosine classifier in a single pass over the (networked) library.
        if (args is ["--key-sweep", var sweepFolder, ..])
        {
            KeyReport.RunBiasSweep(Services, sweepFolder);
            return;
        }

        // Ground-truth benchmark: `--giantsteps <dataset-root> [maxFiles]` scores the shipped
        // detector against the hand-verified GiantSteps Key labels (real accuracy).
        if (args is ["--giantsteps", var gsRoot, ..])
        {
            var max = args.Length > 2 && int.TryParse(args[2], out var m) ? m : int.MaxValue;
            KeyReport.RunGiantSteps(Services, gsRoot, max);
            return;
        }

        // Same, but the experimental peak-picked HPCP detector @ 44.1 kHz (A/B experiment).
        if (args is ["--giantsteps-hpcp", var hRoot, ..])
        {
            var max = args.Length > 2 && int.TryParse(args[2], out var m) ? m : int.MaxValue;
            KeyReport.RunGiantStepsHpcp(Services, hRoot, max);
            return;
        }

        // HPCP matching sweep: build chroma once, A/B profile × (cosine|pearson).
        if (args is ["--giantsteps-hpcp-sweep", var sRoot, ..])
        {
            var max = args.Length > 2 && int.TryParse(args[2], out var m) ? m : int.MaxValue;
            KeyReport.RunGiantStepsHpcpSweep(Services, sRoot, max);
            return;
        }

        // Export 36-bin chroma + labels to CSV for offline ML training.
        if (args is ["--dump-chroma", var dRoot, var dOut, ..])
        {
            KeyReport.RunDumpChroma(Services, dRoot, dOut);
            return;
        }

        // Energy calibration: dump per-file raw+normalised features to CSV.
        // Used to calibrate normalisation ranges and blend weights against the DEAM arousal dataset.
        // Usage: --dump-energy-features <audioFolder> <out.csv>
        // [energy-detection.md §Grounding the 1–10 scale, ml/calibrate_energy.py]
        if (args is ["--dump-energy-features", var efFolder, var efOut, ..])
        {
            KeyReport.RunDumpEnergyFeatures(Services, efFolder, efOut);
            return;
        }

        // Validate the trained ONNX model end-to-end in C# (expect ~0.75).
        if (args is ["--giantsteps-ml", var mlRoot, ..])
        {
            var max = args.Length > 2 && int.TryParse(args[2], out var m) ? m : int.MaxValue;
            KeyReport.RunGiantStepsMl(Services, mlRoot, max);
            return;
        }

        // N19 tuning audit: quantifies the gain from 36-bin detuning correction vs no correction.
        // Reports per-tuning-offset group (on-pitch / sharp / flat) accuracy with and without correction.
        // Usage: --giantsteps-tuning <dataset-root> [maxFiles]
        if (args is ["--giantsteps-tuning", var tuningRoot, ..])
        {
            var max = args.Length > 2 && int.TryParse(args[2], out var m) ? m : int.MaxValue;
            KeyReport.RunGiantStepsTuningAudit(Services, tuningRoot, max);
            return;
        }

        // N19 experiment: segment-aware + HPSS key detection variants vs GiantSteps ground truth.
        // Runs four variants (global/segment-vote/HPSS/combined) in one pass, prints comparison.
        // Usage: --giantsteps-segment <dataset-root> [maxFiles]
        if (args is ["--giantsteps-segment", var segRoot, ..])
        {
            var max = args.Length > 2 && int.TryParse(args[2], out var m) ? m : int.MaxValue;
            KeyReport.RunGiantStepsSegmentExperiment(Services, segRoot, max);
            return;
        }

        // BPM diagnostic: raw BASS_FX BPM + corrector internals + ACF peak table.
        // Usage: --bpm-debug "<file path>"
        if (args is ["--bpm-debug", var bpmFile, ..])
        {
            BpmDebug.Run(Services, bpmFile);
            return;
        }

        // BPM batch validation: compare old vs new corrector against tagged BPM ground truth.
        // Usage: --bpm-validate "<folder>"   (READ-ONLY — never writes to the folder)
        if (args is ["--bpm-validate", var bpmFolder, ..])
        {
            BpmDebug.RunValidate(Services, bpmFolder);
            return;
        }

        // Beat / groove fingerprint similarity matrix (N8 — beat axis of the compat score).
        // Usage: --beat-sim "<folder>"  (READ-ONLY — decode + analyse only, no writes)
        // Builds a BeatFingerprint (Scale Transform + Cyclic Tempogram + DFA danceability)
        // for each audio file, then prints the N×N cosine similarity matrix, per-track
        // nearest neighbours, and highest/lowest similar pairs.
        // Reference: rhythm-similarity.md §"Concrete recommendation".
        if (args is ["--beat-sim", var beatSimFolder, ..])
        {
            BeatSimReport.Run(Services, beatSimFolder);
            return;
        }

        // ── Single-instance gate (GUI path only) ─────────────────────────────
        // A double-click on an associated file launches us with the file as an arg. If
        // JustPlay is already running, forward the file(s) to that instance and exit, so the
        // song joins the existing queue instead of opening a second window. ConfigureServices
        // above only REGISTERS services (singletons stay lazy), so a forward-and-exit process
        // never spins up BASS or a window.
        var (files, addOnly) = SingleInstance.ParseFileArgs(args);
        Single = new SingleInstance();
        if (!Single.Acquire())
        {
            if (files.Count > 0) SingleInstance.ForwardToPrimary(files, addOnly);
            return;
        }
        PendingOpen = (files, addOnly);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (System.Exception ex)
        {
            ErrorReporter.Report(ex, "Avalonia startup / fatal runtime");
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Backends — swapped here (and only here) when going cross-platform.
        // S2: BassAudioEngine is registered as IAudioEngine AND as IBassMixerSource so that
        // BassBroadcastService can read the mixer's OutputChannel without a concrete dependency
        // (the same seam lets JUST STREAM swap in BassInputCaptureEngine). One singleton instance
        // backs all three registrations.
        services.AddSingleton<BassAudioEngine>();
        services.AddSingleton<IAudioEngine>(sp => sp.GetRequiredService<BassAudioEngine>());
        services.AddSingleton<IBassMixerSource>(sp => sp.GetRequiredService<BassAudioEngine>());
        services.AddSingleton<IBroadcastService, BassBroadcastService>();
        services.AddSingleton<IAudioDecoder, BassAudioDecoder>();
        // Waveform overview (finder scrubber, later the JUST SPIN decks): decodes via IAudioDecoder →
        // normalised peaks, platform-agnostic so it lives in Core. Cached by path for instant re-cue.
        services.AddSingleton<IWaveformService, JustPlay.Core.Audio.WaveformService>();
        // Pre-listen (PFL) cue engine — structurally isolated from the main engine (separate
        // BASS mixer, separate device, no encoder). Singleton for the process lifetime.
        services.AddSingleton<IPreListenEngine, BassPreListenEngine>();
        services.AddSingleton<IMetadataReader, TagLibMetadataReader>();
        services.AddSingleton<IMetadataWriter, TagLibMetadataWriter>();

        // Analysis stack — BPM detector + the orchestrator that fans tracks
        // out to all registered detectors. Singletons so the BASS_FX side has
        // a single initialised instance for the process lifetime.
        services.AddSingleton<IBpmDetector, BassBpmDetector>();
        // Key detection: ONE canonical detector for the whole product — the app AND the headless
        // CLI both use BestKeyDetector, so a track keyed by the console shows the SAME key in the UI
        // (the fix for the key-conflict-dot root cause). Always the best method: the trained ML model
        // (MlKeyDetector, ONNX, MIREX ~0.75) when the model+runtime loaded, else the always-available
        // DSP template (HpcpKeyDetector, ~0.71). No user toggle — best method, always. Both analyse at
        // 44.1 kHz (TrackAnalysisService.KeySampleRate). See JustPlay.ML.BestKeyDetector.
        services.AddSingleton<HpcpKeyDetector>();
        services.AddSingleton<MlKeyDetector>();
        services.AddSingleton<IKeyDetector>(sp => new BestKeyDetector(
            sp.GetRequiredService<MlKeyDetector>(),
            sp.GetRequiredService<HpcpKeyDetector>()));
        services.AddSingleton<IEnergyDetector, SpectralEnergyDetector>();
        services.AddSingleton<ILoudnessDetector, Bs1770LoudnessDetector>();
        services.AddSingleton<ITrackAnalysisService, TrackAnalysisService>();

        // Theming + user preferences. SettingsService reads settings.json
        // from LocalAppData on construction; the ThemeService applies a
        // palette by writing to Application.Current.Resources. Both are
        // singletons so MainWindowViewModel and App.OnFrameworkInitialization
        // can share the same instance.
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        // PRE CUE FINDER add-on settings — separate file (finder.settings.json), source-gen JSON.
        services.AddSingleton<IFinderSettingsService, FinderSettingsService>();
        services.AddSingleton<IThemeService, AvaloniaThemeService>();

        // Auto-update (v0.2). One shared HttpClient backs both the API check and the installer
        // download. The checker is Core (transport-only); the download + installer hand-off is the
        // App-layer UpdateInstaller. Owner/repo match the public GitHub releases.
        services.AddSingleton(_ =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("JustPlay-Updater");
            return http;
        });
        services.AddSingleton<IUpdateChecker>(sp => new GitHubUpdateChecker(
            sp.GetRequiredService<HttpClient>(), "chloe-dream", "just-play",
            log: m => Console.WriteLine($"[Update] {m}")));
        services.AddSingleton<UpdateViewModel>();

        // Core logic.
        services.AddSingleton<PlaybackController>();

        // ViewModels. The finder VM is a singleton on purpose: closing and reopening the
        // finder window keeps the browse position, cue device and pending like-writes.
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<PreCueFinderViewModel>();

        return services.BuildServiceProvider();
    }

    // Parameterless — used by Main and by the Avalonia visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
