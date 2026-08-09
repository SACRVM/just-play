using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace JustPlay.UI.Controls;

/// <summary>Which icon a <see cref="JustIcon"/> draws. One value per shape, suite-wide.</summary>
public enum IconKind
{
    None = 0,

    /// <summary>A folder row in a browser pane (PRE CUE FINDER, JUST TAG).</summary>
    Folder,

    /// <summary>A playlist shown as a virtual folder - the list mark.</summary>
    Playlist,

    /// <summary>Locked / read-only (STREAM's "on air, settings frozen" note).</summary>
    Lock,

    /// <summary>Close / dismiss / remove - panels, dialogs, list-row deletes.</summary>
    Close,

    /// <summary>Re-scan, re-read, refresh (audio-device rescan).</summary>
    Refresh,

    /// <summary>Like. Filled shape either way; the LIKED state is a colour, not a second icon.</summary>
    Heart,

    /// <summary>Affirmative tick - analysed / tagged / copied / saved.</summary>
    Check,

    /// <summary>Play / start / go on air.</summary>
    Play,

    /// <summary>Stop / disconnect.</summary>
    Stop,

    /// <summary>Column sorted ascending.</summary>
    SortAsc,

    /// <summary>Column sorted descending.</summary>
    SortDesc,

    /// <summary>Caution - a soft/unreliable value, not an error.</summary>
    Warning,

    /// <summary>Write/rename - an action that CHANGES the file, not one that reads it.</summary>
    Pencil,

    /// <summary>Drop target - "put something here" (the empty-queue hint).</summary>
    ArrowDown,

    /// <summary>Opens a list of choices. Honest here and nowhere else: a chevron promises "pick one
    /// of these", so it belongs on a picker and not on a button that DOES something.</summary>
    ChevronDown,

    /// <summary>Look, do not touch - a preview of what an action would do, before it runs.</summary>
    Eye,

    /// <summary>The operating system's bin. (!) It means "filed where you can get it back", never
    /// "destroyed" - this suite has no permanent delete, so the mark must not read like one.</summary>
    Trash,
}

/// <summary>
/// THE icon control for the whole suite: a shipped vector path, drawn in <see cref="Foreground"/>.
///
/// <para>(!!) <b>Icons are never characters.</b> A pictograph in <c>Text=</c> is rendered by whatever
/// font the OS hands us, and that is genuinely not under our control - the same <c>folder</c> (U+1F4C1),
/// from source lines with byte-identical UTF-16, came out BRIGHT YELLOW in JUST TAG and monochrome
/// white in the PRE CUE FINDER: two processes, same Avalonia build, same <c>WithInterFont()</c>.
/// On macOS it changes again, because Apple gives many symbols emoji presentation by default, so
/// <c>heart ok (!) > stop</c> would come out coloured there - you can never be sure an emoji glyph
/// renders identically everywhere, especially on Mac or Linux. See CLAUDE.md, UI philosophy rule 5.</para>
///
/// <para>Being a vector also buys what a glyph cannot: it inherits <see cref="Foreground"/> through
/// <see cref="TextElement"/>, so an icon tracks the live theme and the row it sits in with no wiring
/// - drop it in a folder row and it picks up that row's colour, in both browsers, identically.</para>
///
/// <para>Adding an icon: one <see cref="IconKind"/> value + one entry in <see cref="Icons"/>. Paths
/// are authored in a <b>24 x 24</b> box and scaled to the control's bounds, so one definition serves
/// every size. (!!) Do not re-draw an existing icon as a local <c>Path</c> in an app - that is how two
/// windows drift apart on something as basic as a folder.</para>
/// </summary>
public class JustIcon : Control
{
    /// <summary>The box every path below is authored in.</summary>
    private const double DesignSize = 24d;

    public static readonly StyledProperty<IconKind> KindProperty =
        AvaloniaProperty.Register<JustIcon, IconKind>(nameof(Kind));

    /// <summary>Inherited from the surrounding text context (AddOwner of
    /// <see cref="TextElement.ForegroundProperty"/>) - an icon in a row takes the row's colour
    /// without anyone binding anything.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<JustIcon>();

    static JustIcon()
    {
        AffectsRender<JustIcon>(KindProperty, ForegroundProperty);
    }

    public IconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (!Icons.TryGetValue(Kind, out var icon) || Foreground is not { } brush)
            return;

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        // Uniform fit, centred - the maths Stretch=Uniform does, done here so the control needs
        // neither a template nor a Viewbox. The pen scales with the box, so a 10-px icon keeps the
        // same visual weight as a 20-px one.
        var scale = Math.Min(bounds.Width, bounds.Height) / DesignSize;
        var offsetX = (bounds.Width - DesignSize * scale) / 2;
        var offsetY = (bounds.Height - DesignSize * scale) / 2;

        var pen = icon.Stroke > 0
            ? new Pen(brush, icon.Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
            : null;

        using (context.PushTransform(
                   Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            context.DrawGeometry(icon.Stroke > 0 ? null : brush, pen, icon.Path);
        }
    }

    /// <summary>One shipped icon. <paramref name="Stroke"/> 0 means a FILLED silhouette; anything
    /// else strokes the path at that width (in the 24-box, so it scales with the icon).</summary>
    private readonly record struct IconDef(Geometry Path, double Stroke = 0);

    /// <summary>
    /// The shipped paths, authored in a 24 x 24 box.
    /// <para>Filled or stroked is chosen per icon by what the shape IS: a heart, a play triangle and
    /// a folder are silhouettes; a tick, a cross and a refresh arrow are strokes. Getting that wrong
    /// is what makes a replacement read as a DIFFERENT icon rather than the same one - the complaint
    /// that started this pass.</para>
    /// <para><c>F0</c> selects the even-odd fill rule, which is how the lock gets its shackle hole and
    /// the warning triangle its exclamation mark.</para>
    /// </summary>
    private static readonly Dictionary<IconKind, IconDef> Icons = new()
    {
        // -- Browser rows ----------------------------------------------------------------------
        // Classic folder: back panel with the left tab, front panel filled solid.
        [IconKind.Folder] = new(Geometry.Parse(
            "M2.6 6.1 C2.6 5.2 3.3 4.5 4.2 4.5 H9.1 C9.6 4.5 10.1 4.75 10.4 5.15 " +
            "L11.7 6.9 H19.8 C20.7 6.9 21.4 7.6 21.4 8.5 V17.9 C21.4 18.8 20.7 19.5 19.8 19.5 " +
            "H4.2 C3.3 19.5 2.6 18.8 2.6 17.9 Z")),

        // Three solid bars - the list mark list had, so a playlist row keeps reading as a playlist.
        [IconKind.Playlist] = new(Geometry.Parse(
            "M3.2 5.6 H20.8 V7.8 H3.2 Z M3.2 10.9 H20.8 V13.1 H3.2 Z M3.2 16.2 H20.8 V18.4 H3.2 Z")),

        // -- State / status --------------------------------------------------------------------
        // Padlock: body plus shackle, the shackle's inside cut out by the even-odd rule.
        [IconKind.Lock] = new(Geometry.Parse(
            "F0 M12 2.6 A5.4 5.4 0 0 0 6.6 8 V10 H5.4 A1.8 1.8 0 0 0 3.6 11.8 V19.6 " +
            "A1.8 1.8 0 0 0 5.4 21.4 H18.6 A1.8 1.8 0 0 0 20.4 19.6 V11.8 A1.8 1.8 0 0 0 18.6 10 " +
            "H17.4 V8 A5.4 5.4 0 0 0 12 2.6 Z M8.8 10 V8 A3.2 3.2 0 0 1 15.2 8 V10 Z")),

        // Caution triangle with the bar + dot punched out.
        [IconKind.Warning] = new(Geometry.Parse(
            "F0 M12 2.4 L22.8 21.2 H1.2 Z M11 8.8 H13 V15 H11 Z M11 16.8 H13 V19 H11 Z")),

        // -- Actions ---------------------------------------------------------------------------
        // (!) SPANS 2.6...21.4 - the SAME reach as the folder above, and that is not cosmetic bookkeeping:
        // every icon is scaled from this 24-box, so a mark drawn across only 58 % of it (which this
        // cross was) comes out visibly smaller than its siblings at the same Width - exactly what
        // showed up on the About dialog's close icon. Keep new icons in the same 2.6...21.4 reach
        // unless there is a reason not to.
        // Stroke 2.4 -> ~1 px at the 12-px caption size, the hairline WindowControls' own cross uses.
        [IconKind.Close] = new(Geometry.Parse("M2.6 2.6 L21.4 21.4 M21.4 2.6 L2.6 21.4"), Stroke: 2.4),

        // A single circular arrow, like the refresh it replaces: the arc, then the arrowhead corner.
        [IconKind.Refresh] = new(Geometry.Parse(
            "M20.5 15 A9 9 0 1 1 18.4 5.6 L22.5 9.6 M22.5 3.6 V9.6 H16.5"), Stroke: 2),

        [IconKind.Check] = new(Geometry.Parse("M2.8 12.9 L9.1 19.2 L21.2 4.8"), Stroke: 2.4),

        // Pencil: the body, then the ferrule line that stops it reading as a plain arrow. It marks
        // the one control in the tag editor that CHANGES a file rather than a tag - a chevron there
        // said "pick from a list" and hid what the list actually does.
        [IconKind.Pencil] = new(Geometry.Parse(
            "M16.8 3.2 A2.4 2.4 0 0 1 20.2 6.6 L8.2 18.6 L3.4 20.6 L5.4 15.8 Z M14.4 5.6 L17.8 9"),
            Stroke: 2),

        // Shaft + head, full 2.6...21.4 reach. The empty queue used to say this with a letter "v" at
        // 56 px - a FONT glyph pretending to be an arrow, and one that read as a giant lowercase v.
        [IconKind.ArrowDown] = new(Geometry.Parse(
            "M12 2.6 V21.4 M4.6 14 L12 21.4 L19.4 14"), Stroke: 2),

        [IconKind.ChevronDown] = new(Geometry.Parse("M3.4 8 L12 16.6 L20.6 8"), Stroke: 2.2),

        // Almond plus pupil, drawn as two subpaths of one stroke: the eye of every preview in the
        // suite. Wider than tall on purpose - a round one reads as a target, not an eye.
        [IconKind.Eye] = new(Geometry.Parse(
            "M2.2 12 C5 6.6 8.5 4.4 12 4.4 C15.5 4.4 19 6.6 21.8 12 " +
            "C19 17.4 15.5 19.6 12 19.6 C8.5 19.6 5 17.4 2.2 12 Z " +
            "M8.6 12 A3.4 3.4 0 1 0 15.4 12 A3.4 3.4 0 1 0 8.6 12"), Stroke: 1.9),

        // Bin: lid, tapered body, handle, two ribs - one stroke, five subpaths. Stroked rather than
        // filled, like the pencil and the refresh arrow: it is an ACTION mark, not a silhouette, and
        // a solid black bin reads as destruction where this one is a filing cabinet you can reopen.
        // Lid spans the full 2.6...21.4 reach so it sits at the same visual weight as its siblings.
        [IconKind.Trash] = new(Geometry.Parse(
            "M2.6 6.2 H21.4 M5.9 6.2 L7 21.2 H17 L18.1 6.2 " +
            "M9.4 6.2 V4.2 A1.4 1.4 0 0 1 10.8 2.8 H13.2 A1.4 1.4 0 0 1 14.6 4.2 V6.2 " +
            "M10 10 V17.6 M14 10 V17.6"), Stroke: 2),

        [IconKind.Play] = new(Geometry.Parse("M6.5 4 L19.5 12 L6.5 20 Z")),

        [IconKind.Stop] = new(Geometry.Parse("M6 6 H18 V18 H6 Z")),

        // Symmetric, so a liked row and the column header read as the same mark.
        [IconKind.Heart] = new(Geometry.Parse(
            "M12 20.8 C12 20.8 2 14 2 8.2 A5.4 5.4 0 0 1 12 5.2 A5.4 5.4 0 0 1 22 8.2 " +
            "C22 14 12 20.8 12 20.8 Z")),

        // -- Sort arrows -----------------------------------------------------------------------
        // Same reach as the rest (see the Close note): drawn across 4...20 rather than 6...18, or a 7-px
        // arrow next to a header label would have under 4 px of actual ink.
        [IconKind.SortAsc] = new(Geometry.Parse("M12 5 L20 19 H4 Z")),
        [IconKind.SortDesc] = new(Geometry.Parse("M12 19 L4 5 H20 Z")),
    };
}
