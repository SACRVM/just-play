using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace JustPlay.UI.Controls;

/// <summary>
/// One editable field in the tag editor - see the XAML header for the shape and for why this is a
/// control rather than a style.
///
/// <para>It holds no view model and knows nothing about tags: a caption, a value, a tick and an X.
/// The two-way <see cref="Text"/> / <see cref="Ticked"/> bindings are the whole contract, which is
/// what lets the X do its job here - <c>clear it and tick it</c> is pure field behaviour, so it needs
/// no command plumbed through from the view model to work.</para>
/// </summary>
public partial class TagFieldBox : UserControl
{
    public TagFieldBox()
    {
        AvaloniaXamlLoader.Load(this);

        // The suggestion box's INNER text box needs the bare class put on from code - see BareInput
        // for why a style cannot reach it. Without this a focused GENRE field drew two rings.
        if (this.FindControl<AutoCompleteBox>("Suggest") is { } suggest) BareInput.Apply(suggest);
    }

    /// <summary>The caption above the box.</summary>
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<TagFieldBox, string?>(nameof(Label));

    /// <summary>The value. Two-way by default - this is an input.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TagFieldBox, string?>(
            nameof(Text), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Does this field take part in the save? Two-way; only meaningful with
    /// <see cref="ShowTick"/>.</summary>
    public static readonly StyledProperty<bool> TickedProperty =
        AvaloniaProperty.Register<TagFieldBox, bool>(
            nameof(Ticked), defaultValue: true,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Show the tick and the X - i.e. the editor is pointed at more than one file.</summary>
    public static readonly StyledProperty<bool> ShowTickProperty =
        AvaloniaProperty.Register<TagFieldBox, bool>(nameof(ShowTick));

    /// <summary>The grey line inside an empty box ("different values"). Empty string = none.</summary>
    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<TagFieldBox, string?>(nameof(Hint));

    /// <summary>Suggestions turn the input into an AutoCompleteBox. Null = a plain box.</summary>
    public static readonly StyledProperty<IEnumerable?> SuggestionsProperty =
        AvaloniaProperty.Register<TagFieldBox, IEnumerable?>(nameof(Suggestions));

    /// <summary>A comment-sized box: accepts returns, wraps, and grows.</summary>
    public static readonly StyledProperty<bool> MultilineProperty =
        AvaloniaProperty.Register<TagFieldBox, bool>(nameof(Multiline));

    public static readonly StyledProperty<string?> TipProperty =
        AvaloniaProperty.Register<TagFieldBox, string?>(nameof(Tip));

    public string? Label      { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? Text       { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool Ticked        { get => GetValue(TickedProperty); set => SetValue(TickedProperty, value); }
    public bool ShowTick      { get => GetValue(ShowTickProperty); set => SetValue(ShowTickProperty, value); }
    public string? Hint       { get => GetValue(HintProperty); set => SetValue(HintProperty, value); }
    public IEnumerable? Suggestions { get => GetValue(SuggestionsProperty); set => SetValue(SuggestionsProperty, value); }
    public bool Multiline     { get => GetValue(MultilineProperty); set => SetValue(MultilineProperty, value); }
    public string? Tip        { get => GetValue(TipProperty); set => SetValue(TipProperty, value); }

    // -- Derived, so the XAML stays free of converters --------------------------------------------

    /// <summary>
    /// Locked from OUTSIDE, whatever the tick says: the field is being filled per file from
    /// somewhere else (the file name), so it has no single value to show and none to accept.
    /// </summary>
    public static readonly StyledProperty<bool> LockedProperty =
        AvaloniaProperty.Register<TagFieldBox, bool>(nameof(Locked));

    public bool Locked { get => GetValue(LockedProperty); set => SetValue(LockedProperty, value); }

    /// <summary>Read-only because it is not part of the save, or because something else owns it. A
    /// box you cannot write should not invite typing into it.</summary>
    public static readonly StyledProperty<bool> IsLockedProperty =
        AvaloniaProperty.Register<TagFieldBox, bool>(nameof(IsLocked));

    /// <summary>The hint line is showing - the box is empty AND there is something to say about it.</summary>
    public static readonly StyledProperty<bool> ShowHintProperty =
        AvaloniaProperty.Register<TagFieldBox, bool>(nameof(ShowHint));

    public static readonly StyledProperty<bool> HasSuggestionsProperty =
        AvaloniaProperty.Register<TagFieldBox, bool>(nameof(HasSuggestions));

    /// <summary>Where the tick and the X sit. Centre on a one-line box, top on the comment - a
    /// control that floats in the middle of a six-line field reads as belonging to no line.</summary>
    public static readonly StyledProperty<VerticalAlignment> TickAlignmentProperty =
        AvaloniaProperty.Register<TagFieldBox, VerticalAlignment>(
            nameof(TickAlignment), defaultValue: VerticalAlignment.Center);

    public static readonly StyledProperty<TextWrapping> WrappingProperty =
        AvaloniaProperty.Register<TagFieldBox, TextWrapping>(
            nameof(Wrapping), defaultValue: TextWrapping.NoWrap);

    public bool IsLocked       { get => GetValue(IsLockedProperty); private set => SetValue(IsLockedProperty, value); }
    public bool ShowHint       { get => GetValue(ShowHintProperty); private set => SetValue(ShowHintProperty, value); }
    public bool HasSuggestions { get => GetValue(HasSuggestionsProperty); private set => SetValue(HasSuggestionsProperty, value); }
    public VerticalAlignment TickAlignment { get => GetValue(TickAlignmentProperty); private set => SetValue(TickAlignmentProperty, value); }
    public TextWrapping Wrapping { get => GetValue(WrappingProperty); private set => SetValue(WrappingProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty || change.Property == HintProperty ||
            change.Property == TickedProperty || change.Property == ShowTickProperty ||
            change.Property == LockedProperty)
        {
            IsLocked = Locked || (ShowTick && !Ticked);
            ShowHint = string.IsNullOrEmpty(Text) && !string.IsNullOrEmpty(Hint);
        }
        else if (change.Property == SuggestionsProperty)
        {
            HasSuggestions = Suggestions is not null;
        }
        else if (change.Property == MultilineProperty)
        {
            TickAlignment = Multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
            Wrapping = Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        }
    }

    /// <summary>
    /// The X. Emptying a field and putting it in the save are one gesture, not two: nobody clears a
    /// field they were not going to write. Doing both here means the behaviour is the field's, so it
    /// cannot differ between the panel's eleven of them.
    /// </summary>
    private void OnClear(object? sender, RoutedEventArgs e)
    {
        Text = null;
        Ticked = true;
    }
}
