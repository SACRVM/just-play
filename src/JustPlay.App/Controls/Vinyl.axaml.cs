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

    // 12 seconds per revolution = 30°/s.
    private const double DegreesPerSecond = 30.0;

    // Pivot mathematics baked into a TransformGroup sandwich:
    //   visual = T(-180,-180) * R(angle) * T(+180,+180)
    // This rotates around (180, 180) — the geometric centre of the 360×360 VinylGroup —
    // regardless of what RenderTransformOrigin is doing. We tried RotateTransform.CenterX/Y
    // alone first; that gives the right Matrix.Value in theory, but in Avalonia 12 there
    // was a lifetime race where the centre wasn't applied at the first render. With the
    // explicit Translate sandwich the pivot is encoded structurally — no single property
    // setter can break it.
    private readonly RotateTransform _rotation = new() { Angle = 0 };
    private readonly TransformGroup _transform;
    private Grid? _vinylGroup;
    private DispatcherTimer? _timer;
    private DateTime _spinStartTime;
    private double _spinStartAngle;

    public Vinyl()
    {
        // Build the pivot sandwich: Translate(-180,-180) → Rotate(angle) → Translate(+180,+180).
        // TransformGroup composes its children left-to-right (row-vector convention),
        // so this matrix product rotates around (180, 180) in the Grid's local coords.
        _transform = new TransformGroup();
        _transform.Children.Add(new TranslateTransform(-180, -180));
        _transform.Children.Add(_rotation);
        _transform.Children.Add(new TranslateTransform(180, 180));

        InitializeComponent();

        // NO DataContext = this anymore — the Image/placeholder bindings inside Vinyl.axaml
        // now use $parent[controls:Vinyl].Cover, which is element-relative and doesn't depend
        // on DataContext at all. That removes the timing window where compiled bindings could
        // evaluate against the inherited (wrong) DataContext and never re-bind.

        // Diagnostic: confirm the Cover styled-property is actually being written. If you see
        // [Vinyl] Cover changed: null → null repeatedly the binding source (Current.Cover) is
        // null — likely the MP3 has no embedded picture (TrackViewModel.Cover then logs
        // "[Cover NONE]" with the filename).
        CoverProperty.Changed.AddClassHandler<Vinyl>((v, e) =>
        {
            // Non-generic class-handler: OldValue/NewValue are typed as object?, no .Value indirection.
            var oldT = e.OldValue?.GetType().Name ?? "null";
            var newT = e.NewValue?.GetType().Name ?? "null";
            Console.WriteLine($"[Vinyl] Cover changed: {oldT} → {newT}");
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
            _vinylGroup.RenderTransform = _transform;
            Console.WriteLine("[Vinyl] attached, TransformGroup wired (pivot 180,180)");
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
    /// some configurations — a plain timer is foolproof.
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
