using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace JustPlay.UI.Controls;

/// <summary>
/// Manual edge/corner resize for the suite's borderless card windows. A window with
/// <c>WindowDecorations="None"</c> gets no OS resize frame (WS_THICKFRAME is only set when
/// decorations are on - verified in Avalonia's Win32 WindowImpl), and any OS chrome would
/// break the transparent floating-card look, so the drag maths lives here.
///
/// <para>Usage: give the window a grid of thin transparent grip <see cref="Border"/>s in the
/// shadow margin (outside the visible card), each with <c>Tag</c> set to a compass name
/// containing "North"/"South"/"East"/"West" (e.g. "NorthWest"), then call
/// <see cref="Attach"/> once with that grid and the card it surrounds.</para>
///
/// <para>(!) <b>Pass the CARD.</b> The grip band has to be exactly the card's own margin, and that
/// margin is not one number: a window card sits at <c>20,20,20,22</c> and a dialog card at
/// <c>20,20,20,26</c> (Theming/JustStyles.axaml), while every window's grip grid was written out by
/// hand as <c>20,*,20</c>. The bottom edge therefore had a dead strip between the visible card and
/// the first pixel that resizes - 2 px in a window, 6 px in a dialog, which is what made sub-windows
/// feel like they only resized "where the shadow ends" (Chloe 2026-08-08). Reading it off the card
/// makes the two agree by construction, in every window at once, and it keeps agreeing when the
/// margin changes: maximizing sets it to 0, which collapses the grips to nothing - exactly the state
/// a maximized window wants.</para>
/// </summary>
public static class FramelessResizeBehavior
{
    /// <summary>
    /// Wire resize handling onto every direct child of <paramref name="grips"/> that carries a
    /// compass-name Tag. Respects the window's MinWidth/MinHeight.
    /// </summary>
    /// <param name="card">The visible rounded card. Its Margin becomes the grip band - see the class
    /// remarks for why this must not be a constant.</param>
    /// <param name="canResize">Optional extra gate, for a window with a mode that must not resize at
    /// all (JUST PLAY's mini view). Maximizing needs no gate: the card's margin goes to 0 and the
    /// bands go with it.</param>
    public static void Attach(Window window, Grid grips, Border? card = null,
                              Func<bool>? canResize = null)
    {
        if (card is not null) BindBandToCard(grips, card);

        var state = new ResizeState(window, canResize);
        foreach (var child in grips.Children)
        {
            if (child is not Border { Tag: string } grip) continue;
            grip.PointerPressed += state.OnPressed;
            grip.PointerMoved += state.OnMoved;
            grip.PointerReleased += state.OnReleased;
        }
    }

    /// <summary>Keep the outer grid bands equal to the card's margin, now and whenever it changes.</summary>
    private static void BindBandToCard(Grid grips, Border card)
    {
        Apply();
        card.PropertyChanged += (_, e) =>
        {
            if (e.Property == Layoutable.MarginProperty) Apply();
        };

        void Apply()
        {
            var m = card.Margin;

            if (grips.ColumnDefinitions.Count == 3)
            {
                grips.ColumnDefinitions[0].Width = new GridLength(Math.Max(0, m.Left));
                grips.ColumnDefinitions[2].Width = new GridLength(Math.Max(0, m.Right));
            }

            if (grips.RowDefinitions.Count == 3)
            {
                grips.RowDefinitions[0].Height = new GridLength(Math.Max(0, m.Top));
                grips.RowDefinitions[2].Height = new GridLength(Math.Max(0, m.Bottom));
            }
        }
    }

    private sealed class ResizeState(Window window, Func<bool>? canResize)
    {
        private bool _resizing, _wEdge, _eEdge, _nEdge, _sEdge;
        private PixelPoint _pointerStart, _posStart;   // screen px, window-pos px
        private double _wStartPx, _hStartPx;           // window size px

        public void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (canResize is not null && !canResize()) return;
            if (sender is not Border { Tag: string name } grip) return;
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
            _wEdge = name.Contains("West"); _eEdge = name.Contains("East");
            _nEdge = name.Contains("North"); _sEdge = name.Contains("South");
            _posStart = window.Position;
            _wStartPx = window.Width * window.RenderScaling;
            _hStartPx = window.Height * window.RenderScaling;
            _pointerStart = window.PointToScreen(e.GetPosition(window));
            _resizing = true;
            e.Pointer.Capture(grip);
            e.Handled = true;
        }

        public void OnMoved(object? sender, PointerEventArgs e)
        {
            if (!_resizing) return;
            var p = window.PointToScreen(e.GetPosition(window));
            double dx = p.X - _pointerStart.X, dy = p.Y - _pointerStart.Y;
            double scale = window.RenderScaling;
            double minW = window.MinWidth * scale, minH = window.MinHeight * scale;

            double newW = _wStartPx, newH = _hStartPx;
            int newX = _posStart.X, newY = _posStart.Y;

            if (_eEdge) newW = Math.Max(minW, _wStartPx + dx);
            if (_sEdge) newH = Math.Max(minH, _hStartPx + dy);
            if (_wEdge) { newW = Math.Max(minW, _wStartPx - dx); newX = _posStart.X + (int)(_wStartPx - newW); }
            if (_nEdge) { newH = Math.Max(minH, _hStartPx - dy); newY = _posStart.Y + (int)(_hStartPx - newH); }

            window.Position = new PixelPoint(newX, newY);
            window.Width = newW / scale;
            window.Height = newH / scale;
            e.Handled = true;
        }

        public void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_resizing) return;
            _resizing = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
}
