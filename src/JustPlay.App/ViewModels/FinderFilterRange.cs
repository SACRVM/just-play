using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JustPlay.App.ViewModels;

/// <summary>
/// One numeric range filter in the finder's FILTER tab (BPM / energy / gain / LUFS / duration + the
/// vibes). Its domain (<see cref="DomainMin"/>..<see cref="DomainMax"/>) is auto-scaled to the values
/// actually present in the current file pane, so the dual-handle slider always spans real data; the
/// handles (<see cref="Lower"/>/<see cref="Upper"/>) start at full span (= no restriction). A range only
/// constrains once a handle has moved off the domain edge (<see cref="IsActive"/>).
/// </summary>
public sealed partial class FinderFilterRange : ObservableObject
{
    private readonly Action _onChanged;
    private readonly Func<TrackViewModel, double?> _selector;
    private readonly Func<double, string> _format;
    private bool _suppress; // true while re-scaling the domain, so the reset doesn't re-fire the filter

    public FinderFilterRange(string key, string label,
        Func<TrackViewModel, double?> selector, Func<double, string> format, Action onChanged)
    {
        Key = key;
        Label = label;
        _selector = selector;
        _format = format;
        _onChanged = onChanged;
    }

    public string Key { get; }
    public string Label { get; }

    [ObservableProperty] private double _domainMin;
    [ObservableProperty] private double _domainMax;
    [ObservableProperty] private double _lower;
    [ObservableProperty] private double _upper;

    /// <summary>Normalised (0..1) per-bucket track count across the domain — the faint distribution the
    /// slider draws behind its track, so you can drag the handles around where the music actually clusters.</summary>
    [ObservableProperty] private IReadOnlyList<double>? _histogram;

    /// <summary>The field has a usable spread in the current view (else the row is hidden — a single-valued
    /// or empty column is nothing to filter).</summary>
    public bool HasSpread => DomainMax - DomainMin > 1e-9;

    /// <summary>A handle has moved off the edge → this range actually constrains the result.</summary>
    public bool IsActive => HasSpread && (Lower > DomainMin + 1e-9 || Upper < DomainMax - 1e-9);

    public string LowerText => _format(Lower);
    public string UpperText => _format(Upper);
    public string RangeText => $"{LowerText} – {UpperText}";

    /// <summary>The field's value for <paramref name="t"/> (null = not analyzed) — used to auto-scale the
    /// domain across the current view.</summary>
    public double? ValueOf(TrackViewModel t) => _selector(t);

    partial void OnLowerChanged(double value)
    {
        if (value > Upper) SetProperty(ref _upper, value, nameof(Upper)); // don't let the handles cross
        RaiseState();
        if (!_suppress) _onChanged();
    }

    partial void OnUpperChanged(double value)
    {
        if (value < Lower) SetProperty(ref _lower, value, nameof(Lower));
        RaiseState();
        if (!_suppress) _onChanged();
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(LowerText));
        OnPropertyChanged(nameof(UpperText));
        OnPropertyChanged(nameof(RangeText));
    }

    /// <summary>Re-scale the domain to the current view and reset the handles to full span (no restriction).
    /// Suppresses the change callback so the reset doesn't trigger a redundant re-filter.</summary>
    public void SetDomain(double min, double max, IReadOnlyList<double>? histogram = null)
    {
        _suppress = true;
        DomainMin = min;
        DomainMax = max;
        Lower = min;
        Upper = max;
        _suppress = false;
        Histogram = histogram;
        OnPropertyChanged(nameof(HasSpread));
        RaiseState();
    }

    /// <summary>Reset just the handles to full span (used by "Clear filters").</summary>
    public void Reset()
    {
        _suppress = true;
        Lower = DomainMin;
        Upper = DomainMax;
        _suppress = false;
        RaiseState();
    }

    /// <summary>Does <paramref name="t"/> pass this range? An inactive range passes everything; an active
    /// one excludes un-analyzed rows (null value) — you asked for a band, they have no value in it.</summary>
    public bool Passes(TrackViewModel t)
    {
        if (!IsActive) return true;
        var v = _selector(t);
        return v is { } x && x >= Lower - 1e-9 && x <= Upper + 1e-9;
    }
}
