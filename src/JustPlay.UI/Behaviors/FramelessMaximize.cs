using System;
using Avalonia;
using Avalonia.Controls;

namespace JustPlay.UI.Behaviors;

/// <summary>
/// The suite's custom MAXIMIZE, written once.
///
/// <para><b>Why it is custom at all.</b> Every JUST window is a borderless, transparent card
/// (<c>WindowDecorations="None"</c>) with a drop shadow blooming into a transparent margin. Windows'
/// own <c>WindowState.Maximized</c> on such a window keeps that margin and the rounded corners - so
/// "maximized" left a transparent frame around the screen and the card still had corners cut off it.
/// Full screen means FILLING THE SCREEN. So we size the window to the screen's work
/// area ourselves (respecting the taskbar) and square the card off through the <c>maximized</c> class.</para>
///
/// <para><b>Why it is shared.</b> This logic existed three times - JUST PLAY's main window, the PRE CUE
/// FINDER and JUST STREAM - as three copies of the same 25 lines, and JUST TAG had the <c>.maximized</c>
/// STYLE but nothing that ever set the class, so its maximize did exactly the wrong thing. One copy per
/// app is how a suite rule becomes three-quarters true. Attach this instead.</para>
/// </summary>
public sealed class FramelessMaximize
{
    private readonly Window _window;
    private readonly string _cardName;
    private readonly string _gripsName;
    private PixelRect _restoreBounds;

    private FramelessMaximize(Window window, string cardName, string gripsName)
    {
        _window = window;
        _cardName = cardName;
        _gripsName = gripsName;
    }

    /// <summary>
    /// Wire a frameless window up. The card and the resize grips are found BY NAME, because every JUST
    /// window is built from the same chrome recipe and names them the same - a window that does not
    /// have them simply gets the sizing without the squaring off, never an exception.
    /// </summary>
    public static FramelessMaximize Attach(Window window, string cardName = "RootCard",
                                           string gripsName = "ResizeGrips")
    {
        var behavior = new FramelessMaximize(window, cardName, gripsName);

        // WINDOWS can maximize us too - Win+^, a snap, the taskbar, a double-click on the chrome -
        // and none of that goes through our button. Without this watcher the card kept its margin and
        // its corners on every one of those routes. (JUST PLAY's XAML has claimed a "WindowState
        // watcher" in a comment since it was written; there wasn't one. Verified 2026-08-05.)
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty) return;
            behavior.IsMaximized = window.WindowState is WindowState.Maximized or WindowState.FullScreen;
            behavior.Apply();
        };

        window.Opened += (_, _) => behavior.Apply();
        return behavior;
    }

    /// <summary>True while the custom maximize is active. <see cref="WindowPlacement"/> reads it so the
    /// screen-filling bounds are never persisted as the window's normal size.</summary>
    public bool IsMaximized { get; private set; }

    /// <summary>
    /// The same flag, ATTACHED TO THE WINDOW, so anything in the window's tree can bind to it.
    ///
    /// <para>Why it exists: the shared caption button drew a plain square in every state, so a
    /// maximized JUST window still offered "maximize" - Windows draws two overlapping rectangles
    /// (restore) there, and we did not. The state lived here and was told to nobody.</para>
    ///
    /// <para>Attached rather than an interface member on purpose: our maximize is custom, so
    /// <c>Window.WindowState</c> never changes and there is nothing else to observe - and this way
    /// all four frameless windows get it without a single line of per-window plumbing.</para>
    /// </summary>
    public static readonly AttachedProperty<bool> IsMaximizedProperty =
        AvaloniaProperty.RegisterAttached<FramelessMaximize, Window, bool>("IsMaximized");

    public static bool GetIsMaximized(Window window) => window.GetValue(IsMaximizedProperty);

    public static void SetIsMaximized(Window window, bool value) =>
        window.SetValue(IsMaximizedProperty, value);

    /// <summary>
    /// An extra condition for showing the resize grips, for a window that hides them for its own
    /// reasons too (JUST PLAY's mini mode). Grips are ALWAYS hidden while maximized; this only decides
    /// the restored case. Null = show them when restored.
    /// </summary>
    public Func<bool>? GripsVisibleWhen { get; set; }

    /// <summary>Fill the current screen's work area, or go back to the exact bounds from before.</summary>
    public void Toggle()
    {
        var screen = _window.Screens.ScreenFromWindow(_window) ?? _window.Screens.Primary;
        if (screen is null) return;

        var scale = _window.RenderScaling;

        if (!IsMaximized)
        {
            _restoreBounds = new PixelRect(
                _window.Position, PixelSize.FromSize(new Size(_window.Width, _window.Height), scale));

            var area = screen.WorkingArea;
            _window.Position = area.Position;
            _window.Width = area.Width / scale;
            _window.Height = area.Height / scale;
            IsMaximized = true;
        }
        else
        {
            _window.Position = _restoreBounds.Position;
            _window.Width = _restoreBounds.Width / scale;
            _window.Height = _restoreBounds.Height / scale;
            IsMaximized = false;
        }

        Apply();
    }

    /// <summary>Re-apply the chrome for the current state - call it when something ELSE changed that
    /// affects the grips (JUST PLAY leaving mini mode).</summary>
    public void Apply()
    {
        // Publish the state on the window first, so the caption button's restore mark and the card's
        // squared-off corners always change in the same frame.
        SetIsMaximized(_window, IsMaximized);

        // No margin, no corner radius, no shadow: the card IS the screen.
        _window.FindControl<Border>(_cardName)?.Classes.Set("maximized", IsMaximized);

        if (_window.FindControl<Grid>(_gripsName) is { } grips)
            grips.IsVisible = !IsMaximized && (GripsVisibleWhen?.Invoke() ?? true);
    }
}
