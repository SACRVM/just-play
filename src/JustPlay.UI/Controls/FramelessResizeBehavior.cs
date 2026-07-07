using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace JustPlay.UI.Controls;

/// <summary>
/// Manual edge/corner resize for the suite's borderless card windows. A window with
/// <c>WindowDecorations="None"</c> gets no OS resize frame (WS_THICKFRAME is only set when
/// decorations are on — verified in Avalonia's Win32 WindowImpl), and any OS chrome would
/// break the transparent floating-card look, so the drag maths lives here.
///
/// <para>Usage: give the window a grid of thin transparent grip <see cref="Border"/>s in the
/// shadow margin (outside the visible card), each with <c>Tag</c> set to a compass name
/// containing "North"/"South"/"East"/"West" (e.g. "NorthWest"), then call
/// <see cref="Attach"/> once with that grid. Grip visibility (maximized, mini mode …)
/// stays the window's concern. Extracted from MainWindow so the PRE CUE FINDER — and any
/// future JUST window — resizes identically without copying the pointer maths.</para>
/// </summary>
public static class FramelessResizeBehavior
{
    /// <summary>Wire resize handling onto every direct child of <paramref name="grips"/> that
    /// carries a compass-name Tag. Respects the window's MinWidth/MinHeight.</summary>
    public static void Attach(Window window, Grid grips)
    {
        var state = new ResizeState(window);
        foreach (var child in grips.Children)
        {
            if (child is not Border { Tag: string } grip) continue;
            grip.PointerPressed += state.OnPressed;
            grip.PointerMoved += state.OnMoved;
            grip.PointerReleased += state.OnReleased;
        }
    }

    private sealed class ResizeState(Window window)
    {
        private bool _resizing, _wEdge, _eEdge, _nEdge, _sEdge;
        private PixelPoint _pointerStart, _posStart;   // screen px, window-pos px
        private double _wStartPx, _hStartPx;           // window size px

        public void OnPressed(object? sender, PointerPressedEventArgs e)
        {
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
