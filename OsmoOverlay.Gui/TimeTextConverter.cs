using System.Globalization;
using Avalonia.Data.Converters;
using OsmoOverlay.Core;

namespace OsmoOverlay.Gui;

/// <summary>
///     A NumericUpDown's seconds as [h:]mm:ss.fff (TimeText, as the preview's time readout shows them) - typed as that or as
///     plain seconds. NumericUpDown.TextConverter runs the other way round from a binding's converter: Convert gets the typed
///     text, ConvertBack the value to show. Text that isn't a time throws, which the box takes as invalid input and puts its
///     value back.
/// </summary>
public sealed class TimeTextConverter : IValueConverter
{
	public static readonly TimeTextConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is not string text || string.IsNullOrWhiteSpace(text)) return null;
		if (!TimeText.TryParse(text, out var seconds)) throw new FormatException($"'{text}' isn't a time");

		return (decimal)Math.Round(seconds, 3);
	}

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		return value is decimal seconds ? TimeText.Format((double)seconds) : "";
	}
}
