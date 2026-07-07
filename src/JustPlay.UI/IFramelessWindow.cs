namespace JustPlay.UI;

/// <summary>
/// Implemented by every JUST suite frameless window (borderless transparent card with
/// a custom maximize that fills the work area). The shared <see cref="Controls.WindowControls"/>
/// caption buttons call <see cref="ToggleMaximize"/> on the hosting window through this
/// interface, so the control needs no reference to any app-specific Window type.
/// </summary>
public interface IFramelessWindow
{
    /// <summary>Toggle the custom work-area maximize (a borderless window has no OS maximize).</summary>
    void ToggleMaximize();

    /// <summary>True while the custom maximize is active. Lets <see cref="Behaviors.WindowPlacement"/>
    /// avoid persisting the work-area-filling bounds as if they were the window's normal size.</summary>
    bool IsMaximized { get; }
}
