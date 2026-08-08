using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;

namespace JustPlay.UI.Views;

/// <summary>One line of the cheat sheet: a piece of the language, and what it means. A named record
/// rather than a KeyValuePair so the XAML can state its type - compiled bindings need one, and
/// "Term / Means" reads better in the template than "Key / Value" anyway.</summary>
public sealed record MaskHelpRow(string Term, string Means);

/// <summary>
/// The pattern cheat sheet - see the XAML header for why it is a window rather than a line of help
/// under the box.
///
/// <para>ONE instance, kept by the static <see cref="Open"/>: a second copy of a reference card is
/// only ever in the way. Opening it again brings the existing one forward.</para>
/// </summary>
public partial class MaskHelpWindow : Window
{
    private static MaskHelpWindow? _open;

    public MaskHelpWindow()
    {
        InitializeComponent();
        FramelessResizeBehavior.Attach(this, this.FindControl<Grid>("ResizeGrips")!,
                                       this.FindControl<Border>("DialogCard"));
        WindowPlacement.Track(this, "Just.MaskHelp");

        this.FindControl<ItemsControl>("Fields")!.ItemsSource = PlaceholderRows;
        this.FindControl<ItemsControl>("Examples")!.ItemsSource = ExampleRows;
    }

    /// <summary>Open the cheat sheet, or bring it forward if it is already up.</summary>
    public static void Open(Window owner)
    {
        if (_open is not null) { _open.Activate(); return; }

        _open = new MaskHelpWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show(owner);
    }

    /// <summary>
    /// What each placeholder means. Kept here rather than generated from
    /// <c>FileNameMask.Fields</c> on purpose: the LIST is the language's, but the SENTENCE explaining
    /// each one is editorial, and a list of bare field names would not have been worth a window.
    /// </summary>
    private static IReadOnlyList<MaskHelpRow> PlaceholderRows =>
    [
        new("%artist%", "the track's artist"),
        new("%title%", "the track's title"),
        new("%album%", "the release it came from"),
        new("%albumartist%", "the release's artist - a compilation's label, say"),
        new("%genre%", "the style"),
        new("%year%", "the release year"),
        new("%track%", "the number within the release"),
        new("%dummy%", "matches a part you do NOT want, and throws it away"),
        new("/", "a folder boundary - reaches up into the path"),
    ];

    private static IReadOnlyList<MaskHelpRow> ExampleRows =>
    [
        new("%artist% - %title%",
            "Perc - Gob        also fits Perc_-_Gob, Perc-Gob and both long dashes"),
        new("%dummy% - %artist% - %title%",
            "01 - Perc - Gob        the leading number is eaten"),
        new("%dummy%. %artist% - %title%",
            "01. Perc - Gob        for a dot after the number"),
        new("%artist% - %title% (%album%)",
            "Ansome - Stowaway (Bulk EP)"),
        new("%artist%_%title%",
            "Perc_Gob        when there is no dash at all"),
        new("%genre%/%artist% - %title%",
            "Hard Techno/Perc - Gob        the folder carries the genre"),
        new("%artist%/%album%/%track% - %title%",
            "Perc/Bulk EP/03 - Gob        two folders deep"),
    ];

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
