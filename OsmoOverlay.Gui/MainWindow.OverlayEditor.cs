using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core.Overlay;
using SkiaSharp;

namespace OsmoOverlay.Gui;

/// <summary>The Overlay card: preset management, per-widget settings/visibility, and dragging elements on the preview canvas.</summary>
public partial class MainWindow
{
	private static readonly List<DateFormatOption> DateFormatOptions =
	[
		new("Default (dd/MM/yyyy HH:mm:ss)", null),
		new("yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd  HH:mm:ss"),
		new("MM/dd/yyyy hh:mm:ss tt", "MM/dd/yyyy  hh:mm:ss tt"),
		new("yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd  HH:mm:ss"),
		new("dd MMM yyyy HH:mm", "dd MMM yyyy  HH:mm"),
		new("HH:mm:ss", "HH:mm:ss")
	];

	private static readonly List<LocaleOption> LocaleOptions = BuildLocaleOptions();

	private static readonly List<AnimationOption> AnimationOptions =
	[
		new("None (instant)", OverlayAnimationType.None),
		new("Fade", OverlayAnimationType.Fade),
		new("Slide up", OverlayAnimationType.SlideUp),
		new("Slide down", OverlayAnimationType.SlideDown),
		new("Slide left", OverlayAnimationType.SlideLeft),
		new("Slide right", OverlayAnimationType.SlideRight)
	];

	private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
	private static readonly Cursor SizeAllCursor = new(StandardCursorType.SizeAll);

	// FirstOrDefault, not First: there's a narrow window right after picking a file where
	// _summary is already set but LoadOverlayPresets (an earlier await) hasn't finished yet, so a
	// pointer click on the drag canvas in that gap must not crash on an empty/stale preset list.
	private List<OverlayElement> ActiveElements =>
		_overlayPresets.FirstOrDefault(p => p.Id == _activePresetId)?.Elements ?? [];

	/// <summary>
	///     The built-in "Default" preset is read-only, so there's always one untouched baseline layout
	///     to fall back to or Duplicate from - editing it directly would mean losing that baseline the
	///     first time someone drags a widget.
	/// </summary>
	private bool IsActivePresetDefault => _activePresetId == OverlayPresetStore.DefaultPresetId;

	private void LoadOverlayPresets(int width, int height)
	{
		(_overlayPresets, _activePresetId) = OverlayPresetStore.Load(width, height);
		_overlayPresetsLoaded = true;
		RefreshPresetComboBox();
		RefreshElementCheckboxes();
		_previewPlayer.SetLayout(ActiveElements);
		// Re-evaluate SetPhase's visibility now that _overlayPresetsLoaded flipped - the Overlay
		// panel (New/Duplicate/etc.) only actually appears from this point on, see the field's doc.
		SetPhase(_phase);
	}

	private void SaveOverlayPresets()
	{
		OverlayPresetStore.Save(_overlayPresets, _activePresetId);
	}

	private void RefreshPresetComboBox()
	{
		_suppressOverlayEvents = true;
		PresetComboBox.ItemsSource = _overlayPresets.Select(p => p.Name).ToList();
		PresetComboBox.SelectedIndex = _overlayPresets.FindIndex(p => p.Id == _activePresetId);
		_suppressOverlayEvents = false;
	}

	private void RefreshElementCheckboxes()
	{
		List<OverlayElement> elements = ActiveElements;

		_suppressOverlayEvents = true;
		DateTimeVisibleCheck.IsChecked = IsVisible(OverlayElementType.DateTimeText);
		UtcTimeVisibleCheck.IsChecked = IsVisible(OverlayElementType.UtcTimeText);
		ElevationVisibleCheck.IsChecked = IsVisible(OverlayElementType.Elevation);
		GradientVisibleCheck.IsChecked = IsVisible(OverlayElementType.Gradient);
		DistanceVisibleCheck.IsChecked = IsVisible(OverlayElementType.Distance);
		CameraInfoVisibleCheck.IsChecked = IsVisible(OverlayElementType.CameraInfo);
		CompassVisibleCheck.IsChecked = IsVisible(OverlayElementType.Compass);
		SunVisibleCheck.IsChecked = IsVisible(OverlayElementType.SunWidget);
		PitchVisibleCheck.IsChecked = IsVisible(OverlayElementType.PitchGauge);
		GMeterVisibleCheck.IsChecked = IsVisible(OverlayElementType.GMeter);
		GMeterFullScaleBox.Value =
			(decimal)(Find(OverlayElementType.GMeter)?.GMeterFullScaleG ?? OverlayRenderer.GMeterFullScaleGDefault);
		ElapsedTimeVisibleCheck.IsChecked = IsVisible(OverlayElementType.ElapsedTimeText);
		CameraModelVisibleCheck.IsChecked = IsVisible(OverlayElementType.CameraModelText);
		SpeedVisibleCheck.IsChecked = IsVisible(OverlayElementType.SpeedGauge);
		MapVisibleCheck.IsChecked = IsVisible(OverlayElementType.MapWidget);
		TripProgressBarVisibleCheck.IsChecked = IsVisible(OverlayElementType.TripProgressBar);

		OverlayElement? map = Find(OverlayElementType.MapWidget);

		MapZoomBox.Value = map?.MapZoom ?? 16;

		var mapDynamicZoom = map?.MapDynamicZoom ?? false;
		MapDynamicZoomCheck.IsChecked = mapDynamicZoom;
		MapZoomOutMaxBox.Value = (decimal)(map?.MapDynamicZoomMaxFactor ?? OverlayRenderer.MapDynamicZoomMaxFactorDefault);
		MapZoomOutMaxLabel.IsVisible = mapDynamicZoom;
		MapZoomOutMaxBox.IsVisible = mapDynamicZoom;
		MapZoomOutMaxHint.IsVisible = mapDynamicZoom;

		OverlayElement? dateTime = Find(OverlayElementType.DateTimeText);
		DateTimeFormatCombo.SelectedItem =
			DateFormatOptions.FirstOrDefault(o => o.Format == dateTime?.DateFormat) ?? DateFormatOptions[0];
		DateTimeLocaleCombo.SelectedItem =
			LocaleOptions.FirstOrDefault(o => o.CultureName == dateTime?.Locale) ?? LocaleOptions[0];

		// Shown whenever the widget is actually usable but only through the container-time fallback
		// (see OverlayRenderer.DrawTimeText) - not real GPS-recorded time, so a driving log synced
		// against this against other GPS-timestamped data could be off by however stale the camera's
		// own clock is.
		var usingTimeFallback = !_hasGpsTimestamp && _hasContainerTime;
		DateTimeFallbackWarningIcon.IsVisible = usingTimeFallback;
		UtcTimeFallbackWarningIcon.IsVisible = usingTimeFallback;
		var timeFallbackTip = "This recording has no GPS timestamp - showing the file's own recording-start " +
		                      "time instead (from the camera's clock, not GPS-synced).";
		ToolTip.SetTip(DateTimeFallbackWarningIcon, timeFallbackTip);
		ToolTip.SetTip(UtcTimeFallbackWarningIcon, timeFallbackTip);

		OverlayElement? utcTime = Find(OverlayElementType.UtcTimeText);
		UtcTimeFormatCombo.SelectedItem =
			DateFormatOptions.FirstOrDefault(o => o.Format == utcTime?.DateFormat) ?? DateFormatOptions[0];
		UtcTimeLocaleCombo.SelectedItem =
			LocaleOptions.FirstOrDefault(o => o.CultureName == utcTime?.Locale) ?? LocaleOptions[0];

		OverlayElement? elevation = Find(OverlayElementType.Elevation);
		ElevationLabelBox.Text = elevation?.Label;
		SetUnitsRadio(ElevationMetricRadio, ElevationImperialRadio, elevation?.Units ?? UnitSystem.Metric);

		GradientLabelBox.Text = Find(OverlayElementType.Gradient)?.Label;

		CameraInfoLabelBox.Text = Find(OverlayElementType.CameraInfo)?.Label;

		OverlayElement? distance = Find(OverlayElementType.Distance);
		DistanceLabelBox.Text = distance?.Label;
		SetUnitsRadio(DistanceMetricRadio, DistanceImperialRadio, distance?.Units ?? UnitSystem.Metric);

		SetUnitsRadio(SpeedMetricRadio, SpeedImperialRadio, Find(OverlayElementType.SpeedGauge)?.Units ?? UnitSystem.Metric);

		OverlayElement? tripProgress = Find(OverlayElementType.TripProgressBar);
		SetUnitsRadio(TripProgressMetricRadio, TripProgressImperialRadio, tripProgress?.Units ?? UnitSystem.Metric);
		TripProgressToleranceBox.Value =
			(decimal)(tripProgress?.TripArrivedToleranceMeters ?? OverlayRenderer.TripArrivedToleranceMetersDefault);
		TripProgressLabelBox.Text = tripProgress?.TripArrivedLabel ?? OverlayRenderer.TripArrivedLabelDefault;

		PopulateTrailControls(CompassTrailColorBox, CompassTrailColorSwatch, CompassTrailWidthBox,
			CompassTrailArrowRadio, CompassTrailDotRadio, Find(OverlayElementType.Compass));
		PopulateTrailControls(MapTrailColorBox, MapTrailColorSwatch, MapTrailWidthBox,
			MapTrailArrowRadio, MapTrailDotRadio, map);

		PopulateTiming(DateTimeAppearAtBox, DateTimeDisappearAtBox, DateTimeAnimationCombo, DateTimeAnimationDurationBox, DateTimeAnimationDurationPanel, dateTime);
		PopulateTiming(UtcTimeAppearAtBox, UtcTimeDisappearAtBox, UtcTimeAnimationCombo, UtcTimeAnimationDurationBox, UtcTimeAnimationDurationPanel, utcTime);
		PopulateTiming(ElevationAppearAtBox, ElevationDisappearAtBox, ElevationAnimationCombo, ElevationAnimationDurationBox, ElevationAnimationDurationPanel, elevation);
		PopulateTiming(GradientAppearAtBox, GradientDisappearAtBox, GradientAnimationCombo, GradientAnimationDurationBox, GradientAnimationDurationPanel, Find(OverlayElementType.Gradient));
		PopulateTiming(DistanceAppearAtBox, DistanceDisappearAtBox, DistanceAnimationCombo, DistanceAnimationDurationBox, DistanceAnimationDurationPanel, distance);
		PopulateTiming(CameraInfoAppearAtBox, CameraInfoDisappearAtBox, CameraInfoAnimationCombo, CameraInfoAnimationDurationBox, CameraInfoAnimationDurationPanel, Find(OverlayElementType.CameraInfo));
		PopulateTiming(CompassAppearAtBox, CompassDisappearAtBox, CompassAnimationCombo, CompassAnimationDurationBox, CompassAnimationDurationPanel, Find(OverlayElementType.Compass));
		PopulateTiming(SunAppearAtBox, SunDisappearAtBox, SunAnimationCombo, SunAnimationDurationBox, SunAnimationDurationPanel, Find(OverlayElementType.SunWidget));
		PopulateTiming(PitchAppearAtBox, PitchDisappearAtBox, PitchAnimationCombo, PitchAnimationDurationBox, PitchAnimationDurationPanel, Find(OverlayElementType.PitchGauge));
		PopulateTiming(GMeterAppearAtBox, GMeterDisappearAtBox, GMeterAnimationCombo, GMeterAnimationDurationBox, GMeterAnimationDurationPanel, Find(OverlayElementType.GMeter));
		PopulateTiming(ElapsedTimeAppearAtBox, ElapsedTimeDisappearAtBox, ElapsedTimeAnimationCombo, ElapsedTimeAnimationDurationBox, ElapsedTimeAnimationDurationPanel, Find(OverlayElementType.ElapsedTimeText));
		PopulateTiming(CameraModelAppearAtBox, CameraModelDisappearAtBox, CameraModelAnimationCombo, CameraModelAnimationDurationBox, CameraModelAnimationDurationPanel, Find(OverlayElementType.CameraModelText));
		PopulateTiming(SpeedAppearAtBox, SpeedDisappearAtBox, SpeedAnimationCombo, SpeedAnimationDurationBox, SpeedAnimationDurationPanel, Find(OverlayElementType.SpeedGauge));
		PopulateTiming(MapAppearAtBox, MapDisappearAtBox, MapAnimationCombo, MapAnimationDurationBox, MapAnimationDurationPanel, map);
		PopulateTiming(TripProgressBarAppearAtBox, TripProgressBarDisappearAtBox, TripProgressBarAnimationCombo, TripProgressBarAnimationDurationBox, TripProgressBarAnimationDurationPanel, tripProgress);

		var editable = !IsActivePresetDefault;
		RenamePresetButton.IsEnabled = editable;
		DeletePresetButton.IsEnabled = editable;
		ResetPresetButton.IsEnabled = editable;
		DefaultPresetLockedHint.IsVisible = !editable;

		SetWidgetAvailability(DateTimeVisibleCheck, DateTimeGearButton, OverlayElementType.DateTimeText, editable);
		SetWidgetAvailability(UtcTimeVisibleCheck, UtcTimeGearButton, OverlayElementType.UtcTimeText, editable);
		SetWidgetAvailability(ElevationVisibleCheck, ElevationGearButton, OverlayElementType.Elevation, editable);
		SetWidgetAvailability(GradientVisibleCheck, GradientGearButton, OverlayElementType.Gradient, editable);
		SetWidgetAvailability(DistanceVisibleCheck, DistanceGearButton, OverlayElementType.Distance, editable);
		SetWidgetAvailability(CameraInfoVisibleCheck, CameraInfoGearButton, OverlayElementType.CameraInfo, editable);
		SetWidgetAvailability(CompassVisibleCheck, CompassGearButton, OverlayElementType.Compass, editable);
		SetWidgetAvailability(SunVisibleCheck, SunGearButton, OverlayElementType.SunWidget, editable);
		SetWidgetAvailability(PitchVisibleCheck, PitchGearButton, OverlayElementType.PitchGauge, editable);
		SetWidgetAvailability(GMeterVisibleCheck, GMeterGearButton, OverlayElementType.GMeter, editable);
		SetWidgetAvailability(ElapsedTimeVisibleCheck, ElapsedTimeGearButton, OverlayElementType.ElapsedTimeText, editable);
		SetWidgetAvailability(CameraModelVisibleCheck, CameraModelGearButton, OverlayElementType.CameraModelText, editable);
		SetWidgetAvailability(SpeedVisibleCheck, SpeedGearButton, OverlayElementType.SpeedGauge, editable);
		SetWidgetAvailability(MapVisibleCheck, MapGearButton, OverlayElementType.MapWidget, editable);
		SetWidgetAvailability(TripProgressBarVisibleCheck, TripProgressBarGearButton, OverlayElementType.TripProgressBar, editable);

		_suppressOverlayEvents = false;
		return;

		bool IsVisible(OverlayElementType type)
		{
			return elements.FirstOrDefault(el => el.Type == type)?.Visible ?? false;
		}

		// Greys out (and disables the gear flyout for) a widget this file's telemetry can never fill
		// in - e.g. Map/Compass/Elevation with no GPS fix at all, Date&time with no GPS timestamp -
		// instead of leaving it toggleable to a widget that would just render "--"/0/a placeholder.
		void SetWidgetAvailability(CheckBox check, Button? gear, OverlayElementType type, bool presetEditable)
		{
			var dataOk = OverlayDataRequirements.IsSupported(type, _hasGpsFix, _hasGpsTimestamp, _hasContainerTime);
			check.IsEnabled = presetEditable && dataOk;
			gear?.IsEnabled = presetEditable && dataOk;

			ToolTip.SetTip(check, dataOk
				? null
				: type is OverlayElementType.DateTimeText or OverlayElementType.UtcTimeText
					? "This file has no GPS timestamp and no usable recording-start time, so this widget can't show a time."
					: "This file has no GPS fix, so this widget has nothing to show.");
		}

		OverlayElement? Find(OverlayElementType type)
		{
			return elements.FirstOrDefault(el => el.Type == type);
		}

		static void SetUnitsRadio(RadioButton metric, RadioButton imperial, UnitSystem units)
		{
			metric.IsChecked = units == UnitSystem.Metric;
			imperial.IsChecked = units == UnitSystem.Imperial;
		}

		static void PopulateTrailControls(TextBox colorBox, Border swatch, NumericUpDown widthBox,
			RadioButton arrowRadio, RadioButton dotRadio, OverlayElement? element)
		{
			colorBox.Text = element?.TrailColor;
			UpdateTrailColorSwatch(swatch, element?.TrailColor);
			widthBox.Value = (decimal)(element?.TrailWidth ?? 4.5f);
			var useArrow = element?.TrailUseArrow ?? true;
			arrowRadio.IsChecked = useArrow;
			dotRadio.IsChecked = !useArrow;
		}

		static void PopulateTiming(NumericUpDown appearBox, NumericUpDown disappearBox, ComboBox animationCombo,
			NumericUpDown durationBox, StackPanel durationPanel, OverlayElement? element)
		{
			var animation = element?.AnimationType ?? OverlayAnimationType.None;

			appearBox.Value = (decimal?)element?.AppearAtSeconds;
			disappearBox.Value = (decimal?)element?.DisappearAtSeconds;
			animationCombo.SelectedItem = AnimationOptions.FirstOrDefault(o => o.Value == animation) ?? AnimationOptions[0];
			durationBox.Value = (decimal)(element?.AnimationDurationSeconds ?? OverlayRenderer.AnimationDurationSecondsDefault);
			// Set explicitly (not left to SelectionChanged above) - picking the same AnimationOption
			// instance as already selected (e.g. switching between two None widgets) doesn't raise that
			// event, which would otherwise leave a stale visibility from whichever widget was shown before.
			durationPanel.IsVisible = animation != OverlayAnimationType.None;
		}
	}

	/// <summary>
	///     Swaps in a brand-new elements list rather than mutating the one already handed to
	///     PreviewPlayer - that list may be mid-enumeration on the playback thread's Render() call
	///     right now, and mutating it in place races with that enumeration.
	/// </summary>
	private void ReplaceActiveElements(List<OverlayElement> elements)
	{
		var index = _overlayPresets.FindIndex(p => p.Id == _activePresetId);
		if (index >= 0) _overlayPresets[index] = _overlayPresets[index] with { Elements = elements };
	}

	private void UpdateElement(OverlayElementType type, Func<OverlayElement, OverlayElement> update)
	{
		if (_suppressOverlayEvents || IsActivePresetDefault) return;

		List<OverlayElement> elements = [.. ActiveElements];
		var index = elements.FindIndex(el => el.Type == type);
		if (index < 0) return;

		elements[index] = update(elements[index]);
		ReplaceActiveElements(elements);
		_previewPlayer.SetLayout(elements);
		SaveOverlayPresets();
	}

	private void SetElementVisible(OverlayElementType type, bool visible)
	{
		UpdateElement(type, el => el with { Visible = visible });
	}

	private void SetElementUnits(OverlayElementType type, UnitSystem units)
	{
		UpdateElement(type, el => el with { Units = units });
	}

	private void SetElementLabel(OverlayElementType type, string? label)
	{
		var trimmed = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
		UpdateElement(type, el => el with { Label = trimmed });
	}

	private void SetElementDateFormat(OverlayElementType type, string? format)
	{
		UpdateElement(type, el => el with { DateFormat = format });
	}

	private void SetElementLocale(OverlayElementType type, string? locale)
	{
		UpdateElement(type, el => el with { Locale = locale });
	}

	private void SetElementTrailColor(OverlayElementType type, string? hex)
	{
		var trimmed = string.IsNullOrWhiteSpace(hex) ? null : hex.Trim();
		UpdateElement(type, el => el with { TrailColor = trimmed });
	}

	private void SetElementTrailWidth(OverlayElementType type, float width)
	{
		UpdateElement(type, el => el with { TrailWidth = width });
	}

	private void SetElementTrailUseArrow(OverlayElementType type, bool useArrow)
	{
		UpdateElement(type, el => el with { TrailUseArrow = useArrow });
	}

	/// <summary>
	///     Restores one widget's own settings (format, units, label, map source, etc.) to factory
	///     values - unlike "Reset to default" for the whole preset, position/visibility are untouched.
	///     Pulled from a freshly computed CreateDefault so it can't drift from the real defaults.
	/// </summary>
	private void ResetElementToFactoryDefaults(OverlayElementType type)
	{
		if (_summary is null) return;

		var factory = OverlayPreset.CreateDefault("factory", "Factory", _summary.Video.Width, _summary.Video.Height);
		if (factory.Elements.FirstOrDefault(e => e.Type == type) is not { } defaults) return;

		UpdateElement(type, el => defaults with { X = el.X, Y = el.Y, Visible = el.Visible });
		RefreshElementCheckboxes();
	}

	/// <summary>
	///     Wires a widget's Timing/Animation controls (Appear at/Disappear at/Animation/Duration) to
	///     OverlayElement - same 4 fields for every widget type, so this is called once per widget from
	///     the constructor instead of duplicating a handler per widget the way the type-specific settings
	///     above do. ValueChanged/SelectionChanged also fire when RefreshElementCheckboxes populates these
	///     controls programmatically, but UpdateElement itself already no-ops while _suppressOverlayEvents
	///     is set, so that's harmless - durationPanel's visibility still needs to update in that case
	///     though (switching preset/element shouldn't leave a stale duration field showing for an
	///     animation that isn't None anymore), hence it's set outside the UpdateElement call.
	/// </summary>
	private void WireTiming(OverlayElementType type, NumericUpDown appearBox, NumericUpDown disappearBox,
		ComboBox animationCombo, NumericUpDown durationBox, StackPanel durationPanel)
	{
		void Apply()
		{
			var animation = (animationCombo.SelectedItem as AnimationOption)?.Value ?? OverlayAnimationType.None;
			durationPanel.IsVisible = animation != OverlayAnimationType.None;

			UpdateElement(type, el => el with
			{
				AppearAtSeconds = (double?)appearBox.Value,
				DisappearAtSeconds = (double?)disappearBox.Value,
				AnimationType = animation,
				AnimationDurationSeconds = durationBox.Value is { } d
					? (double)d
					: OverlayRenderer.AnimationDurationSecondsDefault
			});
		}

		appearBox.ValueChanged += (_, _) => Apply();
		disappearBox.ValueChanged += (_, _) => Apply();
		animationCombo.SelectionChanged += (_, _) => Apply();
		durationBox.ValueChanged += (_, _) => Apply();
	}

	private void OnPitchResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.PitchGauge);
	}

	private void OnSunResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.SunWidget);
	}

	private void OnElapsedTimeResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.ElapsedTimeText);
	}

	private void OnCameraModelResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.CameraModelText);
	}

	private void OnDateTimeVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.DateTimeText, DateTimeVisibleCheck.IsChecked == true);
	}

	private void OnUtcTimeVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.UtcTimeText, UtcTimeVisibleCheck.IsChecked == true);
	}

	private void OnElevationVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.Elevation, ElevationVisibleCheck.IsChecked == true);
	}

	private void OnGradientVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.Gradient, GradientVisibleCheck.IsChecked == true);
	}

	private void OnDistanceVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.Distance, DistanceVisibleCheck.IsChecked == true);
	}

	private void OnCompassVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.Compass, CompassVisibleCheck.IsChecked == true);
	}

	private void OnCompassTrailColorChanged(object? sender, RoutedEventArgs e)
	{
		SetElementTrailColor(OverlayElementType.Compass, CompassTrailColorBox.Text);
		UpdateTrailColorSwatch(CompassTrailColorSwatch, CompassTrailColorBox.Text);
	}

	private void OnCompassTrailWidthChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || CompassTrailWidthBox.Value is not { } width) return;
		SetElementTrailWidth(OverlayElementType.Compass, (float)width);
	}

	private void OnCompassTrailMarkerChanged(object? sender, RoutedEventArgs e)
	{
		SetElementTrailUseArrow(OverlayElementType.Compass, CompassTrailArrowRadio.IsChecked == true);
	}

	private void OnCompassResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.Compass);
	}

	private void OnCameraInfoVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.CameraInfo, CameraInfoVisibleCheck.IsChecked == true);
	}

	private void OnCameraInfoLabelChanged(object? sender, RoutedEventArgs e)
	{
		SetElementLabel(OverlayElementType.CameraInfo, CameraInfoLabelBox.Text);
	}

	private void OnCameraInfoResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.CameraInfo);
	}

	private void OnSunVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.SunWidget, SunVisibleCheck.IsChecked == true);
	}

	private void OnPitchVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.PitchGauge, PitchVisibleCheck.IsChecked == true);
	}

	private void OnGMeterVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.GMeter, GMeterVisibleCheck.IsChecked == true);
	}

	private void OnGMeterFullScaleChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || GMeterFullScaleBox.Value is not { } fullScale) return;
		UpdateElement(OverlayElementType.GMeter, el => el with { GMeterFullScaleG = (double)fullScale });
	}

	private void OnGMeterResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.GMeter);
	}

	private void OnElapsedTimeVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.ElapsedTimeText, ElapsedTimeVisibleCheck.IsChecked == true);
	}

	private void OnCameraModelVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.CameraModelText, CameraModelVisibleCheck.IsChecked == true);
	}

	private void OnSpeedVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.SpeedGauge, SpeedVisibleCheck.IsChecked == true);
	}

	private void OnDateTimeFormatChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || DateTimeFormatCombo.SelectedItem is not DateFormatOption option) return;
		SetElementDateFormat(OverlayElementType.DateTimeText, option.Format);
	}

	private void OnDateTimeLocaleChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || DateTimeLocaleCombo.SelectedItem is not LocaleOption option) return;
		SetElementLocale(OverlayElementType.DateTimeText, option.CultureName);
	}

	private void OnUtcTimeFormatChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || UtcTimeFormatCombo.SelectedItem is not DateFormatOption option) return;
		SetElementDateFormat(OverlayElementType.UtcTimeText, option.Format);
	}

	private void OnUtcTimeLocaleChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || UtcTimeLocaleCombo.SelectedItem is not LocaleOption option) return;
		SetElementLocale(OverlayElementType.UtcTimeText, option.CultureName);
	}

	private void OnElevationLabelChanged(object? sender, RoutedEventArgs e)
	{
		SetElementLabel(OverlayElementType.Elevation, ElevationLabelBox.Text);
	}

	private void OnElevationUnitsChanged(object? sender, RoutedEventArgs e)
	{
		SetElementUnits(OverlayElementType.Elevation, ElevationImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric);
	}

	private void OnGradientLabelChanged(object? sender, RoutedEventArgs e)
	{
		SetElementLabel(OverlayElementType.Gradient, GradientLabelBox.Text);
	}

	private void OnDistanceLabelChanged(object? sender, RoutedEventArgs e)
	{
		SetElementLabel(OverlayElementType.Distance, DistanceLabelBox.Text);
	}

	private void OnDistanceUnitsChanged(object? sender, RoutedEventArgs e)
	{
		SetElementUnits(OverlayElementType.Distance, DistanceImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric);
	}

	private void OnSpeedUnitsChanged(object? sender, RoutedEventArgs e)
	{
		SetElementUnits(OverlayElementType.SpeedGauge, SpeedImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric);
	}

	private void OnTripProgressUnitsChanged(object? sender, RoutedEventArgs e)
	{
		SetElementUnits(OverlayElementType.TripProgressBar,
			TripProgressImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric);
	}

	private void OnTripProgressToleranceChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || TripProgressToleranceBox.Value is not { } tolerance) return;
		UpdateElement(OverlayElementType.TripProgressBar, el => el with { TripArrivedToleranceMeters = (double)tolerance });
	}

	private void OnTripProgressLabelChanged(object? sender, RoutedEventArgs e)
	{
		var label = string.IsNullOrWhiteSpace(TripProgressLabelBox.Text)
			? OverlayRenderer.TripArrivedLabelDefault
			: TripProgressLabelBox.Text.Trim();
		UpdateElement(OverlayElementType.TripProgressBar, el => el with { TripArrivedLabel = label });
	}

	private void OnMapVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.MapWidget, MapVisibleCheck.IsChecked == true);
	}

	private void OnTripProgressBarVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.TripProgressBar, TripProgressBarVisibleCheck.IsChecked == true);
	}

	private void OnMapZoomChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || MapZoomBox.Value is not { } zoom) return;
		UpdateElement(OverlayElementType.MapWidget, el => el with { MapZoom = (int)zoom });
	}

	private void OnMapDynamicZoomChanged(object? sender, RoutedEventArgs e)
	{
		var enabled = MapDynamicZoomCheck.IsChecked == true;
		MapZoomOutMaxLabel.IsVisible = enabled;
		MapZoomOutMaxBox.IsVisible = enabled;
		MapZoomOutMaxHint.IsVisible = enabled;
		UpdateElement(OverlayElementType.MapWidget, el => el with { MapDynamicZoom = enabled });
	}

	private void OnMapZoomOutMaxChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || MapZoomOutMaxBox.Value is not { } factor) return;
		UpdateElement(OverlayElementType.MapWidget, el => el with { MapDynamicZoomMaxFactor = (double)factor });
	}

	private void OnDateTimeResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.DateTimeText);
	}

	private void OnUtcTimeResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.UtcTimeText);
	}

	private void OnElevationResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.Elevation);
	}

	private void OnGradientResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.Gradient);
	}

	private void OnDistanceResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.Distance);
	}

	private void OnSpeedResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.SpeedGauge);
	}

	private void OnTripProgressResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.TripProgressBar);
	}

	private void OnMapResetClick(object? sender, RoutedEventArgs e)
	{
		ResetElementToFactoryDefaults(OverlayElementType.MapWidget);
	}

	private void OnMapTrailColorChanged(object? sender, RoutedEventArgs e)
	{
		SetElementTrailColor(OverlayElementType.MapWidget, MapTrailColorBox.Text);
		UpdateTrailColorSwatch(MapTrailColorSwatch, MapTrailColorBox.Text);
	}

	private void OnMapTrailWidthChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || MapTrailWidthBox.Value is not { } width) return;
		SetElementTrailWidth(OverlayElementType.MapWidget, (float)width);
	}

	private void OnMapTrailMarkerChanged(object? sender, RoutedEventArgs e)
	{
		SetElementTrailUseArrow(OverlayElementType.MapWidget, MapTrailArrowRadio.IsChecked == true);
	}

	/// <summary>
	///     Resolves a hand-typed hex string (e.g. "#46DC6E") to a swatch preview color, falling back to
	///     the renderer's own built-in trail green when the text is empty or doesn't parse - matches
	///     OverlayRenderer.ResolveTrailColor's fail-soft policy, so what the swatch shows is exactly
	///     what the render will actually use.
	/// </summary>
	private static void UpdateTrailColorSwatch(Border swatch, string? hex)
	{
		Color color = !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex.Trim(), out Color parsed)
			? parsed
			: Color.Parse("#46DC6E");
		swatch.Background = new SolidColorBrush(color);
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

	private void OnPresetSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || PresetComboBox.SelectedIndex < 0) return;

		_activePresetId = _overlayPresets[PresetComboBox.SelectedIndex].Id;
		RefreshElementCheckboxes();
		_previewPlayer.SetLayout(ActiveElements);
		SaveOverlayPresets();
	}

	private void OnNewPresetClick(object? sender, RoutedEventArgs e)
	{
		if (_summary is null) return;

		var id = Guid.NewGuid().ToString("N");
		var preset = OverlayPreset.CreateDefault(id, $"Preset {_overlayPresets.Count + 1}",
			_summary.Video.Width, _summary.Video.Height);
		_overlayPresets.Add(preset);
		_activePresetId = id;

		RefreshPresetComboBox();
		RefreshElementCheckboxes();
		_previewPlayer.SetLayout(ActiveElements);
		SaveOverlayPresets();
	}

	private void OnDuplicatePresetClick(object? sender, RoutedEventArgs e)
	{
		OverlayPreset source = _overlayPresets.First(p => p.Id == _activePresetId);
		var id = Guid.NewGuid().ToString("N");
		var copy = new OverlayPreset(id, $"{source.Name} copy", [.. source.Elements]);
		_overlayPresets.Add(copy);
		_activePresetId = id;

		RefreshPresetComboBox();
		RefreshElementCheckboxes();
		_previewPlayer.SetLayout(ActiveElements);
		SaveOverlayPresets();
	}

	private async void OnDeletePresetClick(object? sender, RoutedEventArgs e)
	{
		if (_overlayPresets.Count <= 1)
		{
			AppendLog("Cannot delete the only remaining preset.");
			return;
		}

		var presetName = _overlayPresets.FirstOrDefault(p => p.Id == _activePresetId)?.Name ?? "this preset";
		var confirmed = await ConfirmDialog.AskAsync(this, "Delete preset",
			$"Delete \"{presetName}\"? This can't be undone.", "Delete", DialogKind.Danger);
		if (!confirmed) return;

		_overlayPresets.RemoveAll(p => p.Id == _activePresetId);
		_activePresetId = _overlayPresets[0].Id;

		RefreshPresetComboBox();
		RefreshElementCheckboxes();
		_previewPlayer.SetLayout(ActiveElements);
		SaveOverlayPresets();
	}

	private async void OnResetPresetClick(object? sender, RoutedEventArgs e)
	{
		if (_summary is null) return;

		var index = _overlayPresets.FindIndex(p => p.Id == _activePresetId);
		if (index < 0) return;

		var presetName = _overlayPresets[index].Name;
		var confirmed = await ConfirmDialog.AskAsync(this, "Reset preset",
			$"Reset \"{presetName}\" to the default layout? Your widget positions and settings for it will be lost.",
			"Reset", DialogKind.Danger);
		if (!confirmed) return;

		_overlayPresets[index] = OverlayPreset.CreateDefault(_activePresetId, presetName,
			_summary.Video.Width, _summary.Video.Height);

		RefreshElementCheckboxes();
		_previewPlayer.SetLayout(ActiveElements);
		SaveOverlayPresets();
	}

	private async void OnExportPresetClick(object? sender, RoutedEventArgs e)
	{
		if (_overlayPresets.Count == 0) return;
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		OverlayPreset preset = _overlayPresets.First(p => p.Id == _activePresetId);

		IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Export overlay preset",
			SuggestedFileName = preset.Name,
			DefaultExtension = "json",
			FileTypeChoices = [new FilePickerFileType("Overlay preset") { Patterns = ["*.json"] }]
		});
		if (file is null) return;

		OverlayPresetStore.ExportToFile(preset, file.Path.LocalPath);
		AppendLog($"Exported preset \"{preset.Name}\" to {file.Path.LocalPath}");
	}

	/// <summary>Same shape as OnDuplicatePresetClick - a new id avoids colliding with a preset already on this machine, WithMissingDefaultsFilled backfills any widget type added since the file was exported.</summary>
	private async void OnImportPresetClick(object? sender, RoutedEventArgs e)
	{
		if (_summary is null) return;
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Import overlay preset",
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType("Overlay preset") { Patterns = ["*.json"] }]
		});
		if (files.Count == 0) return;

		OverlayPreset? imported = OverlayPresetStore.ImportFromFile(files[0].Path.LocalPath);
		if (imported is null)
		{
			AppendLog($"Failed to import preset from {files[0].Path.LocalPath} - see the log for details.");
			return;
		}

		var id = Guid.NewGuid().ToString("N");
		OverlayPreset preset = (imported with { Id = id }).WithMissingDefaultsFilled(_summary.Video.Width, _summary.Video.Height);
		_overlayPresets.Add(preset);
		_activePresetId = id;

		RefreshPresetComboBox();
		RefreshElementCheckboxes();
		_previewPlayer.SetLayout(ActiveElements);
		SaveOverlayPresets();
		AppendLog($"Imported preset \"{preset.Name}\".");
	}

	private void OnRenamePresetClick(object? sender, RoutedEventArgs e)
	{
		if (_overlayPresets.Count == 0) return;

		PresetRenameBox.Text = _overlayPresets.First(p => p.Id == _activePresetId).Name;
		PresetComboBox.IsVisible = false;
		PresetRenameBox.IsVisible = true;
		PresetRenameBox.Focus();
		PresetRenameBox.SelectAll();
	}

	private void OnPresetRenameBoxKeyDown(object? sender, KeyEventArgs e)
	{
		switch (e.Key)
		{
			case Key.Enter:
				CommitPresetRename();
				break;
			case Key.Escape:
				CancelPresetRename();
				break;
		}
	}

	private void OnPresetRenameBoxLostFocus(object? sender, RoutedEventArgs e)
	{
		if (PresetRenameBox.IsVisible) CommitPresetRename();
	}

	private void CommitPresetRename()
	{
		var newName = PresetRenameBox.Text?.Trim();
		if (!string.IsNullOrWhiteSpace(newName))
		{
			var index = _overlayPresets.FindIndex(p => p.Id == _activePresetId);
			if (index >= 0)
			{
				_overlayPresets[index] = _overlayPresets[index] with { Name = newName };
				SaveOverlayPresets();
			}
		}

		CancelPresetRename();
	}

	private void CancelPresetRename()
	{
		PresetRenameBox.IsVisible = false;
		PresetComboBox.IsVisible = true;
		RefreshPresetComboBox();
	}

	private Point? MapCanvasPointToFullRes(Point canvasPoint)
	{
		if (_summary is null || _previewBitmap is null) return null;

		var controlWidth = OverlayDragCanvas.Bounds.Width;
		var controlHeight = OverlayDragCanvas.Bounds.Height;
		var bitmapWidth = _previewBitmap.PixelSize.Width;
		var bitmapHeight = _previewBitmap.PixelSize.Height;
		if (controlWidth <= 0 || controlHeight <= 0 || bitmapWidth <= 0 || bitmapHeight <= 0) return null;

		// PreviewImage uses Stretch="Uniform", which letterboxes the bitmap inside the control -
		// replicate that math to turn a click on the control into a pixel in the preview bitmap.
		var scale = Math.Min(controlWidth / bitmapWidth, controlHeight / bitmapHeight);
		var renderedWidth = bitmapWidth * scale;
		var renderedHeight = bitmapHeight * scale;
		var offsetX = (controlWidth - renderedWidth) / 2;
		var offsetY = (controlHeight - renderedHeight) / 2;

		var localX = canvasPoint.X - offsetX;
		var localY = canvasPoint.Y - offsetY;
		if (localX < 0 || localY < 0 || localX > renderedWidth || localY > renderedHeight) return null;

		// The preview bitmap is a uniformly downscaled copy of the full render resolution.
		var fullResScale = _summary.Video.Width / (double)bitmapWidth;
		return new Point(localX / scale * fullResScale, localY / scale * fullResScale);
	}

	/// <summary>Topmost visible element whose bounds contain `pos`, or null - shared by the drag hit-test and the hover cursor.</summary>
	private OverlayElement? FindElementAt(Point pos)
	{
		if (_summary is null) return null;

		var scale = OverlayElementBounds.GetScale(_summary.Video.Width, _summary.Video.Height);
		List<OverlayElement> elements = ActiveElements;
		for (var i = elements.Count - 1; i >= 0; i--)
		{
			OverlayElement el = elements[i];
			if (!el.Visible) continue;

			SKRect bounds = OverlayElementBounds.GetBounds(el.Type, el.X, el.Y, scale);
			if (pos.X >= bounds.Left && pos.X <= bounds.Right && pos.Y >= bounds.Top && pos.Y <= bounds.Bottom) return el;
		}

		return null;
	}

	private void OnOverlayCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (_summary is null || IsActivePresetDefault) return;
		if (MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas)) is not { } pos) return;
		if (FindElementAt(pos) is not { } el) return;

		// Dragging needs a stable frame to align against, and it eliminates a real race:
		// without pausing, the playback thread keeps calling Render() on the same elements
		// list this drag is about to replace concurrently.
		if (_previewPlayer.IsPlaying)
		{
			_previewPlayer.Pause();
			PlayPauseButton.Content = "Play";
		}

		_draggingElementType = el.Type;
		_dragAnchorOffset = new Point(pos.X - el.X, pos.Y - el.Y);
		e.Pointer.Capture(OverlayDragCanvas);
		OverlayDragCanvas.Cursor = SizeAllCursor;
	}

	private void OnOverlayCanvasPointerMoved(object? sender, PointerEventArgs e)
	{
		if (_draggingElementType is not { } type)
		{
			UpdateHoverCursor(e);
			return;
		}

		if (_summary is null) return;
		if (MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas)) is not { } pos) return;

		var newX = (float)Math.Clamp(pos.X - _dragAnchorOffset.X, 0, _summary.Video.Width);
		var newY = (float)Math.Clamp(pos.Y - _dragAnchorOffset.Y, 0, _summary.Video.Height);

		List<OverlayElement> elements = [.. ActiveElements];
		var index = elements.FindIndex(el => el.Type == type);
		if (index < 0) return;

		elements[index] = elements[index] with { X = newX, Y = newY };
		ReplaceActiveElements(elements);
		_previewPlayer.SetLayout(elements);
	}

	private void OnOverlayCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (_draggingElementType is null) return;

		_draggingElementType = null;
		e.Pointer.Capture(null);
		UpdateHoverCursor(e);
		SaveOverlayPresets();
	}

	private void UpdateHoverCursor(PointerEventArgs e)
	{
		if (_summary is null || IsActivePresetDefault)
		{
			OverlayDragCanvas.Cursor = null;
			return;
		}

		Point? pos = MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas));
		OverlayDragCanvas.Cursor = pos is { } p && FindElementAt(p) is not null ? HandCursor : null;
	}

	private sealed record DateFormatOption(string Display, string? Format)
	{
		public override string ToString()
		{
			return Display;
		}
	}

	private sealed record LocaleOption(string Display, string? CultureName)
	{
		public override string ToString()
		{
			return Display;
		}
	}

	private sealed record AnimationOption(string Display, OverlayAnimationType Value)
	{
		public override string ToString()
		{
			return Display;
		}
	}
}
