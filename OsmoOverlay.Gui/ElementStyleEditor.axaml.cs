using Avalonia.Controls;
using Avalonia.Media;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>AccentColor is null for a widget without a Value color (see ElementStyleEditor.HasAccentColor).</summary>
public sealed record ElementStyle(
	string? FontFamily,
	float? Scale,
	string? TextColor,
	string? AccentColor,
	string? OutlineColor,
	float? OutlineWidth);

/// <summary>
///     Font/Text size/Text color/Outline color/Outline width for every text-based widget and every round
///     gauge's readout. HasAccentColor adds the Value color the four LabeledStatElement widgets
///     (Elevation/Gradient/Distance/CameraInfo) have, and renames Text color to Label color there.
/// </summary>
public partial class ElementStyleEditor : UserControl
{
	// Swatch fallbacks for an empty/unparsable box - match OverlayRenderer's built-in White/Accent/Shadow
	// defaults (OverlayRenderer.TextWidgets.cs' *Of helpers), so the swatch shows what the render will use.
	private const string DefaultTextColorHex = "#FFFFFF";
	private const string DefaultAccentColorHex = "#46BEFF";
	private const string DefaultOutlineColorHex = "#000000";

	// Built once and shared by every editor instance - enumerating installed fonts isn't free.
	private static readonly List<FontOption> FontOptions = BuildFontOptions();

	private bool _hasAccentColor;
	private bool _populating;

	public ElementStyleEditor()
	{
		InitializeComponent();
		FontCombo.ItemsSource = FontOptions;

		FontCombo.SelectionChanged += (_, _) => OnChanged();
		ScaleBox.ValueChanged += (_, _) => OnChanged();
		OutlineWidthBox.ValueChanged += (_, _) => OnChanged();
		// LostFocus, not TextChanged: a half-typed hex would otherwise re-render the preview per keystroke.
		TextColorBox.LostFocus += (_, _) => OnChanged();
		AccentColorBox.LostFocus += (_, _) => OnChanged();
		OutlineColorBox.LostFocus += (_, _) => OnChanged();
	}

	public bool HasAccentColor
	{
		get => _hasAccentColor;
		set
		{
			_hasAccentColor = value;
			AccentColorPanel.IsVisible = value;
			TextColorLabel.Text = value ? "Label color" : "Text color";
		}
	}

	/// <summary>Raised on user edits only, not while Populate fills the controls.</summary>
	public event Action<ElementStyle>? StyleChanged;

	public void Populate(StyledOverlayElement element)
	{
		_populating = true;
		FontCombo.SelectedItem = FontOptions.FirstOrDefault(o => o.Family == element.FontFamily) ?? FontOptions[0];
		ScaleBox.Value = (decimal)element.Scale;
		TextColorBox.Text = element.TextColor ?? DefaultTextColorHex;
		OutlineColorBox.Text = element.OutlineColor ?? DefaultOutlineColorHex;
		OutlineWidthBox.Value = (decimal)element.OutlineWidth;
		if (_hasAccentColor) AccentColorBox.Text = (element as LabeledStatElement)?.AccentColor ?? DefaultAccentColorHex;
		UpdateSwatches();
		_populating = false;
	}

	private void OnChanged()
	{
		if (_populating) return;

		UpdateSwatches();
		StyleChanged?.Invoke(new ElementStyle(
			(FontCombo.SelectedItem as FontOption)?.Family,
			ScaleBox.Value is { } scale ? (float)scale : null,
			NormalizeHex(TextColorBox.Text),
			_hasAccentColor ? NormalizeHex(AccentColorBox.Text) : null,
			NormalizeHex(OutlineColorBox.Text),
			OutlineWidthBox.Value is { } width ? (float)width : null));
	}

	private void UpdateSwatches()
	{
		ColorSwatch.Update(TextColorSwatch, TextColorBox.Text, DefaultTextColorHex);
		ColorSwatch.Update(OutlineColorSwatch, OutlineColorBox.Text, DefaultOutlineColorHex);
		if (_hasAccentColor) ColorSwatch.Update(AccentColorSwatch, AccentColorBox.Text, DefaultAccentColorHex);
	}

	private static string? NormalizeHex(string? text)
	{
		return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
	}

	/// <summary>
	///     Pulls the installed-font list from Avalonia's own FontManager instead of hand-maintaining one, so
	///     it covers whatever fonts this machine actually has - the same fail-soft policy as Locale/DateFormat
	///     applies on the render side (OverlayElementBounds.ResolveTypefaceOrFallback) if a preset picks a
	///     family that turns out not to be installed on whatever machine later renders it.
	/// </summary>
	private static List<FontOption> BuildFontOptions()
	{
		List<FontOption> options = [new("System default", null)];
		options.AddRange(FontManager.Current.SystemFonts
			.Select(f => f.Name)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(n => n, StringComparer.Ordinal)
			.Select(n => new FontOption(n, n)));
		return options;
	}

	private sealed record FontOption(string Display, string? Family)
	{
		public override string ToString()
		{
			return Display;
		}
	}
}
