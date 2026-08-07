using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace JustPlay.App.Controls;

/// <summary>
/// Album sleeve with a 12" vinyl peeking out the right side (~30% of disc visible).
/// Spins when <see cref="Spinning"/> is true. <see cref="Cover"/> overrides the
/// themed gradient placeholder when set.
/// </summary>
public partial class Vinyl : UserControl
{
    public static readonly StyledProperty<IImage?> CoverProperty =
        AvaloniaProperty.Register<Vinyl, IImage?>(nameof(Cover));

    public static readonly StyledProperty<bool> SpinningProperty =
        AvaloniaProperty.Register<Vinyl, bool>(nameof(Spinning));

    // 12 seconds per revolution = 30deg/s.
    private const double DegreesPerSecond = 30.0;

    // Avalonia's Visual.RenderTransformOriginProperty defaults to RelativePoint.Center
    // (verified against Avalonia 12.0.3 source: Visual.cs registers the property with
    //  defaultValue: RelativePoint.Center). That means assigning a bare RotateTransform
    // here already rotates around the VinylGroup's geometric centre - no Translate
    // sandwich or RotateTransform.CenterX/Y needed.
    //
    // History: an earlier version stacked T(-180) * R * T(+180) on top of the implicit
    // origin transform. Both sandwiches composed and the disc rotated around (360, 360)
    // = bottom-right corner. The fix is to *not* fight the framework default.
    private readonly RotateTransform _rotation = new() { Angle = 0 };
    private Grid? _vinylGroup;
    private DispatcherTimer? _timer;
    private DateTime _spinStartTime;
    private double _spinStartAngle;

    public Vinyl()
    {
        InitializeComponent();

        // NO DataContext = this anymore - the Image/placeholder bindings inside Vinyl.axaml
        // now use $parent[controls:Vinyl].Cover, which is element-relative and doesn't depend
        // on DataContext at all. That removes the timing window where compiled bindings could
        // evaluate against the inherited (wrong) DataContext and never re-bind.

        // Diagnostic: confirm the Cover styled-property is actually being written. If you see
        // [Vinyl] Cover changed: null -> null repeatedly the binding source (Current.Cover) is
        // null - likely the MP3 has no embedded picture (TrackViewModel.Cover then logs
        // "[Cover NONE]" with the filename).
        CoverProperty.Changed.AddClassHandler<Vinyl>((v, e) =>
        {
            // Non-generic class-handler: OldValue/NewValue are typed as object?, no .Value indirection.
            var oldT = e.OldValue?.GetType().Name ?? "null";
            var newT = e.NewValue?.GetType().Name ?? "null";
            Console.WriteLine($"[Vinyl] Cover changed: {oldT} -> {newT}");
        });

        SpinningProperty.Changed.AddClassHandler<Vinyl>((v, _) => v.UpdateSpin());
    }

    public IImage? Cover
    {
        get => GetValue(CoverProperty);
        set => SetValue(CoverProperty, value);
    }

    public bool Spinning
    {
        get => GetValue(SpinningProperty);
        set => SetValue(SpinningProperty, value);
    }

    /// <summary>
    /// Wire up the rotate transform once the visual tree is built. FindControl from the
    /// constructor was unreliable for the Grid.RenderTransform path; doing it here
    /// guarantees the named child exists and we can REPLACE its render transform with our
    /// own field-owned one so animation writes always reach the right object.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vinylGroup = this.FindControl<Grid>("VinylGroup");
        if (_vinylGroup is not null)
        {
            // Bare RotateTransform - Avalonia's default RenderTransformOrigin=Center
            // gives us the (180, 180) pivot for free.
            _vinylGroup.RenderTransform = _rotation;
            Console.WriteLine("[Vinyl] attached, RotateTransform wired (pivot via default origin)");
        }
        else
        {
            Console.WriteLine("[Vinyl] WARN: VinylGroup not found");
        }
        UpdateSpin();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>
    /// Tick a DispatcherTimer at ~60 FPS and write the angle directly to the (field-owned)
    /// RotateTransform. Earlier <c>Animation.RunAsync</c> approach silently did nothing in
    /// some configurations - a plain timer is foolproof.
    /// </summary>
    private void UpdateSpin()
    {
        _timer?.Stop();
        _timer = null;

        if (!Spinning || _vinylGroup is null) return;

        _spinStartTime = DateTime.UtcNow;
        _spinStartAngle = _rotation.Angle;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - _spinStartTime).TotalSeconds;
            _rotation.Angle = (_spinStartAngle + elapsed * DegreesPerSecond) % 360.0;
        };
        _timer.Start();
        Console.WriteLine("[Vinyl] spin started");
    }
}
