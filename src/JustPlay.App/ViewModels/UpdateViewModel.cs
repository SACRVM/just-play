using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Updates;

namespace JustPlay.App.ViewModels;

/// <summary>
/// Title-bar update badge state + background polling. Asks <see cref="IUpdateChecker"/> (Core)
/// on startup and on an interval; when a newer release lands — and the user hasn't opted out or
/// ignored that version — it flips <see cref="IsAvailable"/> so the green badge appears. The
/// actual download + installer hand-off is a view action (it needs the owner window for the
/// dialog and shuts the app down), driven from <c>UpdateFlow</c>.
/// <para>
/// A failed check is swallowed: a music player must never nag or break because GitHub was
/// briefly unreachable. The polling runs on the UI thread (the timer is a DispatcherTimer and
/// the awaits resume on the captured UI context), so setting the observable properties is safe.
/// </para>
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    // First check is delayed so it never competes with cold-start work (audio engine init,
    // file-association intake). Six hours between checks is plenty for a desktop app.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly IUpdateChecker _checker;
    private readonly ISettingsService _settings;

    private DispatcherTimer? _timer;
    private Version _current = new(0, 0, 0);

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _tooltip = "";

    /// <summary>The release to offer, once <see cref="IsAvailable"/> is true. Null otherwise.</summary>
    public UpdateInfo? Available { get; private set; }

    public UpdateViewModel(IUpdateChecker checker, ISettingsService settings)
    {
        _checker = checker;
        _settings = settings;
    }

    /// <summary>
    /// Begin background update checks for the running version. No-op when the user opted out
    /// (<c>settings.CheckForUpdates == false</c>). Call once, on the UI thread, after the window
    /// is up.
    /// </summary>
    public void Start(Version current)
    {
        _current = new Version(current.Major, current.Minor, Math.Max(0, current.Build));

        if (!_settings.Current.CheckForUpdates) return;

        _ = RunCheckAsync(StartupDelay);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CheckInterval };
        _timer.Tick += (_, _) => _ = RunCheckAsync(TimeSpan.Zero);
        _timer.Start();
    }

    private async Task RunCheckAsync(TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay);

            var info = await _checker.CheckAsync(_current, CancellationToken.None);
            if (info is null) return;

            // Respect an explicitly ignored version (and anything older than it).
            var ignored = _settings.Current.IgnoredUpdateVersion;
            if (ignored is not null
                && Version.TryParse(ignored, out var iv)
                && info.Version <= new Version(iv.Major, iv.Minor, Math.Max(0, iv.Build)))
            {
                return;
            }

            Available = info;
            VersionText = "v" + info.Version.ToString(3);
            Tooltip = $"JustPlay {info.Version.ToString(3)} is ready — click to update";
            IsAvailable = true;
        }
        catch
        {
            // A failed update check must never disrupt the app — stay silent and try next tick.
        }
    }

    /// <summary>
    /// Persist "skip this version" and hide the badge. The badge reappears only for a release
    /// newer than the one just ignored.
    /// </summary>
    public void IgnoreCurrent()
    {
        if (Available is not { } info) return;
        _settings.Save(_settings.Current with { IgnoredUpdateVersion = info.Version.ToString(3) });
        Available = null;
        IsAvailable = false;
    }
}
