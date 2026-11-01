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
	float? OutlineWidth,
	TextAlign Align,
	float? Opacity,
	int? Decimals,
	bool ShowLabel,
	bool ShowUnit,
	bool UppercaseLabel);

/// <summary>
///     Font/Text size/Text color/Outline color/Outline width/Opacity for every text-based widget and every round
///     gauge's readout, and the text alignment for the widgets that line up several lines (OverlayElementBounds.SupportsTextAlign). HasAccentColor adds the Value color the four LabeledStatElement widgets
///     (Elevation/Gradient/Distance/CameraInfo) have, and renames Text color to Label color there.
/// </summary>
public partial class ElementStyleEditor : UserControl
{
	// Built once and shared by every editor instance - enumerating installed fonts isn't free.
	private static readonly List<FontOption> FontOptions = BuildFontOptions();

	private bool _hasAccentColor;
	private bool _populating;
	private TextAlign _align;

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
		OpacityBox.ValueChanged += (_, _) => OnChanged();
		DecimalsCombo.ItemsSource = new[] { Strings.ElementStyle_DecimalsAuto, "0", "1", "2", "3" };
		DecimalsCombo.SelectionChanged += (_, _) => OnChanged();
		ShowLabelCheck.Click += (_, _) => OnChanged();
		ShowUnitCheck.Click += (_, _) => OnChanged();
		UppercaseCheck.Click += (_, _) => OnChanged();
		// One group per editor: every widget has its own, and a shared name would make them one choice.
		string group = $"Align{Guid.NewGuid():N}";
		AlignLeftButton.GroupName = AlignCenterButton.GroupName = AlignRightButton.GroupName = group;
		AlignLeftButton.Click += (_, _) => SetAlign(TextAlign.Left);
		AlignCenterButton.Click += (_, _) => SetAlign(TextAlign.Center);
		AlignRightButton.Click += (_, _) => SetAlign(TextAlign.Right);
		ApplyToAllButton.Click += (_, _) => ApplyToAllRequested?.Invoke();
	}

	private void SetAlign(TextAlign align)
	{
		_align = align;
		ShowAlign();
		OnChanged();
	}

	private void ShowAlign()
	{
		AlignLeftButton.IsChecked = _align == TextAlign.Left;
		AlignCenterButton.IsChecked = _align == TextAlign.Center;
		AlignRightButton.IsChecked = _align == TextAlign.Right;
	}

	public bool HasAccentColor
	{
		get => _hasAccentColor;
		set
		{
			_hasAccentColor = value;
			AccentColorPanel.IsVisible = value;
			TextColorLabel.Text = value ? Strings.Style_LabelColor : Strings.ElementStyle_TextColor;
		}
	}

	/// <summary>Raised on user edits only, not while Populate fills the controls.</summary>
	public event Action<ElementStyle>? StyleChanged;

	public event Action? ApplyToAllRequested;

	public void Populate(StyledOverlayElement element)
	{
		_populating = true;
		FontCombo.SelectedItem = FontOptions.FirstOrDefault(o => o.Family == element.FontFamily) ?? FontOptions[0];
		ScaleBox.Minimum = (decimal)OverlayElementBounds.MinScale(element);
		ScaleBox.Value = (decimal)element.Scale;
		TextColorBox.Text = element.TextColor ?? OverlayRenderer.DefaultTextColorHex;
		OutlineColorBox.Text = element.OutlineColor ?? OverlayRenderer.DefaultOutlineColorHex;
		OutlineWidthBox.Value = (decimal)element.OutlineWidth;
		OpacityBox.Value = (decimal)Math.Round(element.Opacity * 100);
		ReadoutPanel.IsVisible = OverlayElementBounds.SupportsStatParts(element);
		DecimalsPanel.IsVisible = OverlayElementBounds.SupportsDecimals(element);
		if (element is LabeledStatElement stat)
		{
			DecimalsCombo.SelectedIndex = stat.Decimals is { } digits ? Math.Clamp(digits, 0, 3) + 1 : 0;
			ShowLabelCheck.IsChecked = stat.ShowLabel;
			ShowUnitCheck.IsChecked = stat.ShowUnit;
			UppercaseCheck.IsChecked = stat.UppercaseLabel;
		}

		_align = element.TextAlign;
		AlignPanel.IsVisible = OverlayElementBounds.SupportsTextAlign(element);
		ShowAlign();
		if (_hasAccentColor) AccentColorBox.Text = (element as LabeledStatElement)?.AccentColor ?? OverlayRenderer.DefaultAccentColorHex;
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
			OutlineWidthBox.Value is { } width ? (float)width : null,
			_align,
			OpacityBox.Value is { } opacity ? (float)opacity / 100f : null,
			DecimalsCombo.SelectedIndex > 0 ? DecimalsCombo.SelectedIndex - 1 : null,
			ShowLabelCheck.IsChecked != false,
			ShowUnitCheck.IsChecked != false,
			UppercaseCheck.IsChecked != false));
	}

	private void UpdateSwatches()
	{
		ColorSwatch.Update(TextColorSwatch, TextColorBox.Text, OverlayRenderer.DefaultTextColorHex);
		ColorSwatch.Update(OutlineColorSwatch, OutlineColorBox.Text, OverlayRenderer.DefaultOutlineColorHex);
		if (_hasAccentColor) ColorSwatch.Update(AccentColorSwatch, AccentColorBox.Text, OverlayRenderer.DefaultAccentColorHex);
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
		List<FontOption> options = [new(string.Format(Strings.Style_DefaultFont, OverlayElementBounds.HudFontFamily), null)];
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
