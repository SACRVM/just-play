using System;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;

namespace JustPlay.App.Controls;

public partial class WaveformHeader : UserControl
{
    public WaveformHeader()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Rebuild();
        Rebuild();
    }

    private void Rebuild()
    {
        var w = Bounds.Width > 0 ? Bounds.Width : 1280;
        var h = Bounds.Height > 0 ? Bounds.Height : 90;
        Fill(this.FindControl<Polyline>("P0")!, w, h, amp: 28, freq: 2.0, phase: 0.0, yOff: h * 0.55);
        Fill(this.FindControl<Polyline>("P1")!, w, h, amp: 22, freq: 3.1, phase: 1.2, yOff: h * 0.50);
        Fill(this.FindControl<Polyline>("P2")!, w, h, amp: 34, freq: 1.6, phase: 2.4, yOff: h * 0.58);
        Fill(this.FindControl<Polyline>("P3")!, w, h, amp: 14, freq: 4.6, phase: 0.8, yOff: h * 0.45);
    }

    // Same shape generator as Waveform.jsx in the design bundle.
    private static void Fill(Polyline p, double w, double h, double amp, double freq, double phase, double yOff)
    {
        const int steps = 80;
        var pts = new Points();
        for (var i = 0; i <= steps; i++)
        {
            var x = i / (double)steps * w;
            var y = yOff + Math.Sin(i / (double)steps * Math.PI * 2 * freq + phase) * amp;
            pts.Add(new Point(x, y));
        }
        p.Points = pts;
    }
}
