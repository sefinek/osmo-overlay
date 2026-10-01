using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using SkiaSharp;

namespace OsmoOverlay.Gui;

/// <summary>
///     What each widget's settings panel changes: the handlers behind every control in the widget panels (labels, units,
///     formats, trail and marker, map zoom and source), each one writing through UpdateElement.
/// </summary>
public partial class MainWindow
{
	private void OnCameraInfoLabelChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementLabel(id, CameraInfoLabelBox.Text);
	}

	private void OnGMeterFullScaleChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || GMeterFullScaleBox.Value is not { } fullScale) return;
		UpdateElement(id, el => el is GMeterElement g ? g with { GMeterFullScaleG = (double)fullScale } : el);
	}

	private void OnElapsedTimeLabelChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		string? label = string.IsNullOrWhiteSpace(ElapsedTimeLabelBox.Text) ? null : ElapsedTimeLabelBox.Text.Trim();
		UpdateElement(id, el => el is ElapsedTimeTextElement t ? t with { Label = label } : el);
		RefreshSelectionHighlight();
	}

	private void OnProfileSeriesChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		ProfileSeries series = ProfileSpeedRadio.IsChecked == true ? ProfileSeries.Speed : ProfileSeries.Elevation;
		ProfileLabelBox.PlaceholderText = SentenceCase(OverlayRenderer.DefaultProfileLabel(series));
		UpdateElement(id, el => el is ProfileChartElement p ? p with { Series = series } : el);
	}

	private void OnProfileAxisChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		ProfileAxis axis = ProfileTimeRadio.IsChecked == true ? ProfileAxis.Time : ProfileAxis.Distance;
		UpdateElement(id, el => el is ProfileChartElement p ? p with { Axis = axis } : el);
	}

	private void OnProfileLabelChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementLabel(id, ProfileLabelBox.Text);
		RefreshSelectionHighlight();
	}

	private void OnProfileUnitsChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementUnits(id, ProfileImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric);
	}

	private void OnTripStatKindChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || TripStatCombo.SelectedItem is not TripStatOption option) return;
		TripStatLabelBox.PlaceholderText = SentenceCase(OverlayRenderer.DefaultTripStatLabel(option.Stat));
		UpdateElement(id, el => el is TripStatElement t ? t with { Stat = option.Stat } : el);
	}

	private void OnTripStatLabelChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementLabel(id, TripStatLabelBox.Text);
		RefreshSelectionHighlight();
	}

	private void OnTripStatUnitsChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementUnits(id, TripStatImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric);
	}

	/// <summary>
	///     Every keystroke goes to the preview, the preset is saved once typing pauses - the box isn't rewritten meanwhile
	///     (OnTextContentChanged tidies it once left).
	/// </summary>
	private void OnTextContentTyped(object? sender, TextChangedEventArgs e)
	{
		if (_suppressOverlayEvents || IsActivePresetDefault || _editingElementId is not { } id) return;

		string text = string.IsNullOrWhiteSpace(TextContentBox.Text) ? OverlayRenderer.TextDefault : TextContentBox.Text;
		List<OverlayElement> elements = [.. ActiveElements];
		int index = elements.FindIndex(el => el.Id == id);
		if (index < 0 || elements[index] is not TextElement element || element.Text == text) return;

		elements[index] = element with { Text = text };
		ReplaceActiveElements(elements, null, "text:" + id);
		ShowLayout();
		SaveOverlayPresetsSoon();
		RefreshSelectionHighlight();
	}

	/// <summary>An emptied box goes back to the default text rather than leave an invisible widget behind.</summary>
	private void OnTextContentChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		string text = string.IsNullOrWhiteSpace(TextContentBox.Text) ? OverlayRenderer.TextDefault : TextContentBox.Text.TrimEnd();
		if (TextContentBox.Text != text) TextContentBox.Text = text;
		UpdateElement(id, el => el is TextElement t ? t with { Text = text } : el);
		RefreshSelectionHighlight();
	}

	private async void OnImageBrowseClick(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id || GetTopLevel(this) is not { } topLevel) return;

		IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Choose an image",
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType("Image") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] }]
		});
		if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

		UpdateElement(id, el => el is ImageElement image ? image with { ImagePath = path } : el);
		ShowImagePath(path);
		RefreshSelectionHighlight();
	}

	private void ShowImagePath(string? path)
	{
		ImagePathBox.Text = path;
		ImageUnreadableHint.IsVisible = !string.IsNullOrWhiteSpace(path) && OverlayElementBounds.ImageSize(path) is null;
	}

	private void OnImageOpacityChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || ImageOpacityBox.Value is not { } percent) return;
		UpdateElement(id, el => el is ImageElement image ? image with { Opacity = (float)percent / 100f } : el);
	}

	private void OnDateTimeFormatChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || DateTimeFormatCombo.SelectedItem is not DateFormatOption option) return;
		SetElementDateFormat(id, option.Format);
	}

	private void OnDateTimeLocaleChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || DateTimeLocaleCombo.SelectedItem is not LocaleOption option) return;
		SetElementLocale(id, option.CultureName);
	}

	private void OnUtcTimeFormatChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || UtcTimeFormatCombo.SelectedItem is not DateFormatOption option) return;
		SetElementDateFormat(id, option.Format);
	}

	private void OnUtcTimeLocaleChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || UtcTimeLocaleCombo.SelectedItem is not LocaleOption option) return;
		SetElementLocale(id, option.CultureName);
	}

	private void OnElevationLabelChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementLabel(id, ElevationLabelBox.Text);
	}

	private void OnElevationUnitsChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementUnits(id, ElevationImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric);
	}

	private void OnElevationReferenceChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		ElevationReference reference = ElevationSeaLevelRadio.IsChecked == true ? ElevationReference.SeaLevel : ElevationReference.Start;
		UpdateElement(id, el => el is ElevationElement x ? x with { Reference = reference } : el);
	}

	private void OnGradientLabelChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementLabel(id, GradientLabelBox.Text);
	}

	private void OnDistanceLabelChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementLabel(id, DistanceLabelBox.Text);
	}

	private void OnDistanceUnitsChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementUnits(id, DistanceImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric);
	}

	private void OnSpeedUnitsChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementUnits(id, SpeedImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric);
	}

	private void OnSpeedThemeChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SpeedGaugeTheme theme = SpeedThemeRingRadio.IsChecked == true ? SpeedGaugeTheme.Ring : SpeedGaugeTheme.Classic;
		UpdateElement(id, el => el is SpeedGaugeElement s ? s with { Theme = theme } : el);
	}

	private void OnTripProgressUnitsChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementUnits(id, TripProgressImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric);
	}

	private void OnTripProgressToleranceChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || TripProgressToleranceBox.Value is not { } tolerance) return;
		UpdateElement(id, el => el is TripProgressBarElement t ? t with { TripArrivedToleranceMeters = (double)tolerance } : el);
	}

	private void OnTripProgressLabelChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		string label = string.IsNullOrWhiteSpace(TripProgressLabelBox.Text)
			? OverlayRenderer.TripArrivedLabelDefault
			: TripProgressLabelBox.Text.Trim();
		UpdateElement(id, el => el is TripProgressBarElement t ? t with { TripArrivedLabel = label } : el);
	}

	private void OnTrailStyleChanged(TrailStyle style)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id) return;
		UpdateElement(id, el => el is TrailOverlayElement t
			? t with { TrailVisible = style.Visible, TrailColor = style.Color, TrailColorBySpeed = style.BySpeed, TrailWidth = style.Width }
			: el);
	}

	private void OnMarkerStyleChanged(MarkerStyle style)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id) return;
		UpdateElement(id, el => el is TrailOverlayElement t
			? t with { TrailUseArrow = style.UseArrow, MarkerScale = style.Scale, MarkerColor = style.Color }
			: el);
	}

	private void OnRotateWithHeadingChanged(object? sender, RoutedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || sender is not CheckBox check) return;
		bool rotate = check.IsChecked == true;
		UpdateElement(id, el => el is TrailOverlayElement t ? t with { RotateWithHeading = rotate } : el);
	}

	private void OnCompassOptionChanged(object? sender, RoutedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id) return;
		bool center = CompassCenterCheck.IsChecked == true, north = CompassNorthCheck.IsChecked == true, heading = CompassHeadingTextCheck.IsChecked == true;
		UpdateElement(id, el => el is CompassElement c ? c with { CenterOnPosition = center, ShowNorthLabel = north, ShowHeadingText = heading } : el);
	}

	private void OnMapSourceChanged(string providerId)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id) return;
		UpdateElement(id, el => el is MapWidgetElement m ? m with { MapProviderId = providerId } : el);
	}

	/// <summary>The shared map settings can change under an open widget panel (in Settings) - it shows them again.</summary>
	private void RefreshMapSourceEditor()
	{
		if (_editingElementId is { } id && ActiveElements.FirstOrDefault(e => e.Id == id) is MapWidgetElement map)
			MapSourceEditor.Load(map.MapProviderId, OverlaySettingsStore.Load());
	}

	private void OnMapSourcesShared()
	{
		_previewPlayer.SetMapSources(MapSources.From(OverlaySettingsStore.Load()));
	}

	private void OnMapZoomChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || MapZoomBox.Value is not { } zoom) return;
		UpdateElement(id, el => el is MapWidgetElement m ? m with { MapZoom = (int)zoom } : el);
	}

	private void OnMapDynamicZoomChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;

		bool enabled = MapDynamicZoomCheck.IsChecked == true;
		MapZoomOutMaxLabel.IsVisible = enabled;
		MapZoomOutMaxBox.IsVisible = enabled;
		MapZoomOutMaxHint.IsVisible = enabled;
		UpdateElement(id, el => el is MapWidgetElement m ? m with { MapDynamicZoom = enabled } : el);
	}

	private void OnMapZoomOutMaxChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || MapZoomOutMaxBox.Value is not { } factor) return;
		UpdateElement(id, el => el is MapWidgetElement m ? m with { MapDynamicZoomMaxFactor = (double)factor } : el);
	}

	/// <summary>
	///     Pulls the language list from .NET's own culture database instead of hand-maintaining one, so
	///     it covers whatever locales the runtime supports without the GUI needing to keep up.
	/// </summary>
	private static List<LocaleOption> BuildLocaleOptions()
	{
		List<LocaleOption> options = [new("System default", null)];
		options.AddRange(CultureInfo.GetCultures(CultureTypes.SpecificCultures)
			.OrderBy(c => c.NativeName, StringComparer.Ordinal)
			.Select(c => new LocaleOption($"{c.NativeName} ({c.Name})", c.Name)));
		return options;
	}
}
