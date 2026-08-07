using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace JustPlay.UI.Controls;

/// <summary>
/// Bindings that go with <see cref="JustIcon"/>.
///
/// <para>When an icon means "this column is sorted" or "this row is flagged", the ABSENCE of the
/// icon is a state too - <see cref="IconKind.None"/>. A <see cref="JustIcon"/> set to None draws
/// nothing, but it would still occupy its Width in the layout and push the header label sideways,
/// so the host binds IsVisible through <see cref="IsSet"/>. This is the vector equivalent of the
/// <c>StringConverters.IsNotNullOrEmpty</c> the glyph strings used before.</para>
/// </summary>
public static class IconConverters
{
    /// <summary>True for any icon except <see cref="IconKind.None"/>.</summary>
    public static readonly IValueConverter IsSet = new IsSetConverter();

    private sealed class IsSetConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is IconKind kind && kind != IconKind.None;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException("IconConverters.IsSet is one-way.");
    }
}
