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

/// <summary>
///     The Overlay card: preset management, the widget palette (drag onto the preview canvas to add -
///     several instances of the same type are allowed, see AddElementInstance), the "on overlay" list of
///     what's currently placed, and dragging/removing/configuring elements on the preview canvas itself.
///     A widget's settings open only from the canvas (the gear shown on hover, next to the remove button) - the
///     palette and "on overlay" list are for adding/reviewing/selecting, not editing.
/// </summary>
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

	// Trail swatch fallback when the box is empty/unparsable - matches OverlayRenderer's own built-in trail
	// color, so what the swatch shows before you've typed anything is exactly what the render already uses.
	private const string DefaultTrailColorHex = "#46DC6E";

	// Shown in the Label box in place of a null Label - matches the fallback caption OverlayRenderer.
	// TextWidgets.cs itself draws (case-insensitively; the renderer uppercases whatever it gets).
	private const string DefaultElevationLabel = "Elevation";
	private const string DefaultGradientLabel = "Gradient";
	private const string DefaultDistanceLabel = "Total distance";
	private const string DefaultCameraInfoLabel = "Camera";

	private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
	private static readonly Cursor SizeAllCursor = new(StandardCursorType.SizeAll);

	/// <summary>
	///     Custom drag-and-drop format used to carry an OverlayElementType through a widget-list-to-canvas
	///     drag. DataFormat&lt;T&gt; requires a reference type, so the enum travels as its name string.
	/// </summary>
	private static readonly DataFormat<string> WidgetDragFormat =
		DataFormat.CreateInProcessFormat<string>("OsmoOverlay.OverlayElementType");

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
		RefreshWidgetList();
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

	/// <summary>
	///     Refreshes everything driven by the active preset's element list except a widget's own settings
	///     panel (the left column's inline widget-settings view), which is populated on demand instead
	///     (see PopulateElementSettings) - with several instances of a type possibly on the canvas at
	///     once, there's no single "the" instance left to eagerly bind every panel's fields to.
	/// </summary>
	private void RefreshWidgetList()
	{
		List<OverlayElement> elements = ActiveElements;
		var editable = !IsActivePresetDefault;

		HideHoverIcons();
		if (_selectedElementId is not null && !elements.Any(e => e.Id == _selectedElementId && IsTypeSupported(e.Type)))
			_selectedElementId = null;
		if (_editingElementId is not null && !elements.Any(e => e.Id == _editingElementId && IsTypeSupported(e.Type)))
			CloseElementSettings();

		foreach (OverlayElementType type in Enum.GetValues<OverlayElementType>())
		{
			if (GetPaletteItem(type) is not { } item) continue;

			item.Classes.Set("active", elements.Any(el => el.Type == type && el.Visible));
			SetWidgetAvailability(item, type, editable);
		}

		DateTimeFallbackWarningIcon.IsVisible = UsesTimeFallback(OverlayElementType.DateTimeText);
		UtcTimeFallbackWarningIcon.IsVisible = UsesTimeFallback(OverlayElementType.UtcTimeText);
		ToolTip.SetTip(DateTimeFallbackWarningIcon, TimeFallbackTip);
		ToolTip.SetTip(UtcTimeFallbackWarningIcon, TimeFallbackTip);

		RebuildAddedWidgetsList();
		RefreshSelectionHighlight();
		UpdatePreviewGuides();

		RenamePresetButton.IsEnabled = editable;
		DeletePresetButton.IsEnabled = editable;
		ResetPresetButton.IsEnabled = editable;
		DefaultPresetLockedHint.IsVisible = !editable;

		return;

		// Greys out a widget this file's telemetry can never fill in - e.g. Map/Compass/Elevation with
		// no GPS fix at all, Date&time with no GPS timestamp - instead of leaving it draggable onto the
		// canvas as a widget that would just render "--"/0/a placeholder.
		void SetWidgetAvailability(Border listItem, OverlayElementType type, bool presetEditable)
		{
			var dataOk = IsTypeSupported(type);
			listItem.IsEnabled = presetEditable && dataOk;
			ToolTip.SetTip(listItem, dataOk ? null : UnsupportedReason(type));
		}
	}

	// Shown whenever the widget is actually usable but only through the container-time fallback
	// (see OverlayRenderer.DrawTimeText) - not real GPS-recorded time, so a driving log synced
	// against this against other GPS-timestamped data could be off by however stale the camera's
	// own clock is.
	private const string TimeFallbackTip = "This recording has no GPS timestamp - showing the file's own recording-start " +
	                                       "time instead (from the camera's clock, not GPS-synced)";

	private bool IsTypeSupported(OverlayElementType type)
	{
		return OverlayDataRequirements.IsSupported(type, _hasGpsFix, _hasGpsTimestamp, _hasContainerTime);
	}

	private bool UsesTimeFallback(OverlayElementType type)
	{
		return type is (OverlayElementType.DateTimeText or OverlayElementType.UtcTimeText) && !_hasGpsTimestamp && _hasContainerTime;
	}

	private static string UnsupportedReason(OverlayElementType type)
	{
		return type switch
		{
			OverlayElementType.DateTimeText or OverlayElementType.UtcTimeText =>
				"This file has no GPS timestamp and no usable recording-start time, so this widget can't show a time",
			OverlayElementType.SunWidget => "This file has no GPS fix or no GPS timestamp, so the sun's position can't be computed",
			_ => "This file has no GPS fix, so this widget has nothing to show"
		};
	}

	/// <summary>
	///     Maps a widget type to its palette row (see the XAML) - the single source for that
	///     type's display label and drag source, shared by RefreshWidgetList's availability pass and
	///     GetWidgetLabel's lookup for the "on overlay" list.
	/// </summary>
	private Border? GetPaletteItem(OverlayElementType type)
	{
		return type switch
		{
			OverlayElementType.DateTimeText => DateTimeListItem,
			OverlayElementType.UtcTimeText => UtcTimeListItem,
			OverlayElementType.Elevation => ElevationListItem,
			OverlayElementType.Gradient => GradientListItem,
			OverlayElementType.Distance => DistanceListItem,
			OverlayElementType.CameraInfo => CameraInfoListItem,
			OverlayElementType.Compass => CompassListItem,
			OverlayElementType.SunWidget => SunListItem,
			OverlayElementType.PitchGauge => PitchListItem,
			OverlayElementType.GMeter => GMeterListItem,
			OverlayElementType.ElapsedTimeText => ElapsedTimeListItem,
			OverlayElementType.CameraModelText => CameraModelListItem,
			OverlayElementType.SpeedGauge => SpeedListItem,
			OverlayElementType.MapWidget => MapListItem,
			OverlayElementType.TripProgressBar => TripProgressBarListItem,
			_ => null
		};
	}

	private string GetWidgetLabel(OverlayElementType type)
	{
		// A palette item's child is either the label itself or a Grid holding it (plus e.g. the fallback warning icon).
		Control? child = GetPaletteItem(type)?.Child;
		TextBlock? label = child as TextBlock ??
		                   (child as Panel)?.Children.OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("overlayListItemText"));
		return label?.Text ?? type.ToString();
	}

	/// <summary>
	///     Rebuilds the "on overlay" list from scratch on every call (cheap - at most a couple dozen
	///     rows) instead of diffing, matching the copy-on-write style already used for the element list
	///     itself. Multiple instances of the same type are numbered "(2)", "(3)", ... in drop order so
	///     they can be told apart without needing a per-instance custom name.
	/// </summary>
	private void RebuildAddedWidgetsList()
	{
		AddedWidgetsList.Children.Clear();
		List<OverlayElement> visible = [.. ActiveElements.Where(e => e.Visible)];
		var editable = !IsActivePresetDefault;

		Dictionary<OverlayElementType, int> totalByType = [];
		foreach (OverlayElement el in visible) totalByType[el.Type] = totalByType.GetValueOrDefault(el.Type) + 1;

		Dictionary<OverlayElementType, int> seenByType = [];
		foreach (OverlayElement el in visible)
		{
			var index = seenByType[el.Type] = seenByType.GetValueOrDefault(el.Type) + 1;
			var name = GetWidgetLabel(el.Type) + (totalByType[el.Type] > 1 ? $" ({index})" : "");
			AddedWidgetsList.Children.Add(BuildAddedWidgetRow(el, name, editable));
		}

		AddedWidgetsEmptyHint.IsVisible = visible.Count == 0;
	}

	/// <summary>
	///     Clicking a row only selects it (highlights it on the canvas) - it does not open
	///     settings, which stays a canvas-only action (see the class doc) so there's one consistent place
	///     to configure a widget regardless of how many instances of its type exist. A widget this file
	///     has no data for stays in the preset (it comes back with a file that has the data) but isn't
	///     drawn, so its row is dimmed, flagged with the warning icon and not selectable - only removable.
	/// </summary>
	private Border BuildAddedWidgetRow(OverlayElement element, string name, bool editable)
	{
		var id = element.Id;
		var supported = IsTypeSupported(element.Type);
		var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

		var text = new TextBlock { Text = name, Classes = { "overlayListItemText" }, Opacity = supported ? 1 : 0.5 };
		Grid.SetColumn(text, 0);
		grid.Children.Add(text);

		var warningTip = !supported
			? "Not shown with this file - " + UnsupportedReason(element.Type)
			: UsesTimeFallback(element.Type) ? TimeFallbackTip : null;
		if (warningTip is not null)
		{
			var warning = new Border { Classes = { "fallbackWarning" }, Child = new IconView { Data = Icons.Warning } };
			ToolTip.SetTip(warning, warningTip);
			Grid.SetColumn(warning, 1);
			grid.Children.Add(warning);
		}

		if (editable)
		{
			var removeButton = new Button
			{
				Content = new IconView { Data = Icons.Close, Width = 10, Height = 10 },
				Classes = { "addedWidgetRemove" },
				Margin = new Thickness(8, 0, 0, 0)
			};
			removeButton.Click += (_, _) => RemoveElementInstance(id);
			Grid.SetColumn(removeButton, 2);
			grid.Children.Add(removeButton);
		}

		var row = new Border { Classes = { "addedWidgetRow" }, Child = grid };
		row.Classes.Set("selected", id == _selectedElementId);
		if (supported) row.PointerPressed += (_, _) => ToggleSelection(id);
		else row.Cursor = Cursor.Default;
		return row;
	}

	private void ToggleSelection(string id)
	{
		_selectedElementId = _selectedElementId == id ? null : id;
		RefreshSelectionHighlight();
		RebuildAddedWidgetsList();
	}

	/// <summary>
	///     Draws (or hides) the blue box - and its bottom-right resize handle - around whichever element
	///     is selected via the "on overlay" list, kept in sync with drag/resize moves in
	///     OnOverlayCanvasPointerMoved and with list rebuilds here.
	/// </summary>
	private void RefreshSelectionHighlight()
	{
		if (_selectedElementId is not { } id || ActiveElements.FirstOrDefault(e => e.Id == id) is not { Visible: true } el ||
		    !IsTypeSupported(el.Type))
		{
			SelectionHighlightBox.IsVisible = false;
			ResizeHandle.IsVisible = false;
			return;
		}

		var scale = OverlayElementBounds.GetScale(_summary!.Video.Width, _summary.Video.Height);
		SKRect bounds = GetElementBounds(el, el.X, el.Y, scale * el.Scale);
		if (MapFullResPointToCanvas(bounds.Left, bounds.Top) is not { } topLeft ||
		    MapFullResPointToCanvas(bounds.Right, bounds.Bottom) is not { } bottomRight)
		{
			SelectionHighlightBox.IsVisible = false;
			ResizeHandle.IsVisible = false;
			return;
		}

		Canvas.SetLeft(SelectionHighlightBox, topLeft.X);
		Canvas.SetTop(SelectionHighlightBox, topLeft.Y);
		SelectionHighlightBox.Width = Math.Max(0, bottomRight.X - topLeft.X);
		SelectionHighlightBox.Height = Math.Max(0, bottomRight.Y - topLeft.Y);
		SelectionHighlightBox.IsVisible = true;

		Canvas.SetLeft(ResizeHandle, bottomRight.X - ResizeHandle.Width / 2);
		Canvas.SetTop(ResizeHandle, bottomRight.Y - ResizeHandle.Height / 2);
		ResizeHandle.IsVisible = !IsActivePresetDefault;
	}

	/// <summary>
	///     OverlayElementBounds.GetBounds with this element's own DateFormat/Locale/Label/FontFamily and
	///     (for CameraModelText) the loaded file's actual camera model, instead of the generic placeholder
	///     text/font GetBounds falls back to - keeps every hit-test/selection call site in this file
	///     measuring against what that specific instance will really render, without repeating the same
	///     five extra arguments at each one.
	/// </summary>
	private SKRect GetElementBounds(OverlayElement element, float x, float y, float scale)
	{
		return OverlayElementBounds.GetBounds(element.Type, x, y, scale,
			(element as TimeTextElementBase)?.DateFormat, (element as TimeTextElementBase)?.Locale,
			(element as LabeledStatElement)?.Label, _summary?.CameraModel, (element as StyledOverlayElement)?.FontFamily);
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

	private void UpdateElement(string id, Func<OverlayElement, OverlayElement> update)
	{
		if (_suppressOverlayEvents || IsActivePresetDefault) return;

		List<OverlayElement> elements = [.. ActiveElements];
		var index = elements.FindIndex(el => el.Id == id);
		if (index < 0) return;

		elements[index] = update(elements[index]);
		ReplaceActiveElements(elements);
		_previewPlayer.SetLayout(elements);
		SaveOverlayPresets();
	}

	/// <summary>Units has no single shared abstract ancestor across the 4 types that use it (unlike Label/DateFormat below), so this stays an explicit switch.</summary>
	private void SetElementUnits(string id, UnitSystem units)
	{
		UpdateElement(id, el => el switch
		{
			ElevationElement e => e with { Units = units },
			DistanceElement e => e with { Units = units },
			SpeedGaugeElement e => e with { Units = units },
			TripProgressBarElement e => e with { Units = units },
			_ => el
		});
	}

	private void SetElementLabel(string id, string? label)
	{
		var trimmed = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
		UpdateElement(id, el => el is LabeledStatElement stat ? stat with { Label = trimmed } : el);
	}

	private void SetElementDateFormat(string id, string? format)
	{
		UpdateElement(id, el => el is TimeTextElementBase t ? t with { DateFormat = format } : el);
	}

	private void SetElementLocale(string id, string? locale)
	{
		UpdateElement(id, el => el is TimeTextElementBase t ? t with { Locale = locale } : el);
	}

	private void SetElementTrailColor(string id, string? hex)
	{
		var trimmed = string.IsNullOrWhiteSpace(hex) ? null : hex.Trim();
		UpdateElement(id, el => el is TrailOverlayElement t ? t with { TrailColor = trimmed } : el);
	}

	private void SetElementTrailColorBySpeed(string id, bool bySpeed)
	{
		UpdateElement(id, el => el is TrailOverlayElement t ? t with { TrailColorBySpeed = bySpeed } : el);
	}

	private void SetElementTrailWidth(string id, float width)
	{
		UpdateElement(id, el => el is TrailOverlayElement t ? t with { TrailWidth = width } : el);
	}

	private void SetElementTrailUseArrow(string id, bool useArrow)
	{
		UpdateElement(id, el => el is TrailOverlayElement t ? t with { TrailUseArrow = useArrow } : el);
	}

	/// <summary>
	///     Always creates a brand-new OverlayElement rather than reusing/revealing an existing one of the
	///     same type - dragging a widget from the palette repeatedly is how several instances of the same
	///     type end up on the canvas at once. Settings start at that type's factory defaults, the same
	///     baseline ResetElementToFactoryDefaults resets an existing instance back to.
	/// </summary>
	private void AddElementInstance(OverlayElementType type, float x, float y)
	{
		if (_summary is null || IsActivePresetDefault) return;

		var factory = OverlayPreset.CreateDefault("factory", "Factory", _summary.Video.Width, _summary.Video.Height);
		if (factory.Elements.FirstOrDefault(e => e.Type == type) is not { } template) return;

		OverlayElement instance = template with { Id = Guid.NewGuid().ToString("N"), X = x, Y = y, Visible = true };

		List<OverlayElement> elements = [.. ActiveElements, instance];
		ReplaceActiveElements(elements);
		_previewPlayer.SetLayout(elements);
		SaveOverlayPresets();

		_selectedElementId = instance.Id;
		RefreshWidgetList();
	}

	/// <summary>
	///     Deletes this one instance outright rather than just hiding it (Visible=false) - with
	///     palette drops always creating a new instance instead of revealing a hidden one, an unreachable
	///     hidden leftover would just be permanent clutter in the saved preset.
	/// </summary>
	private void RemoveElementInstance(string id)
	{
		if (IsActivePresetDefault) return;

		List<OverlayElement> elements = [.. ActiveElements];
		if (elements.RemoveAll(el => el.Id == id) == 0) return;

		if (_selectedElementId == id) _selectedElementId = null;
		if (_editingElementId == id) _editingElementId = null;

		ReplaceActiveElements(elements);
		_previewPlayer.SetLayout(elements);
		SaveOverlayPresets();
		RefreshWidgetList();
	}

	/// <summary>
	///     Restores one widget instance's own settings (format, units, label, map source, etc.) to
	///     factory values - unlike "Reset to default" for the whole preset, position/visibility are
	///     untouched. Pulled from a freshly computed CreateDefault so it can't drift from the real
	///     defaults.
	/// </summary>
	private void ResetElementToFactoryDefaults(string id)
	{
		if (_summary is null) return;

		OverlayElement? existing = ActiveElements.FirstOrDefault(e => e.Id == id);
		if (existing is null) return;

		var factory = OverlayPreset.CreateDefault("factory", "Factory", _summary.Video.Width, _summary.Video.Height);
		if (factory.Elements.FirstOrDefault(e => e.Type == existing.Type) is not { } defaults) return;

		UpdateElement(id, el => defaults with { Id = el.Id, X = el.X, Y = el.Y, Visible = el.Visible });

		if (ActiveElements.FirstOrDefault(e => e.Id == id) is { } updated) PopulateElementSettings(updated);
		RefreshWidgetList();
	}

	private void OnResetElementClick(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is { } id) ResetElementToFactoryDefaults(id);
	}

	/// <summary>
	///     Shared by every widget's ElementTimingEditor - writes to whichever instance's settings panel is
	///     open. _suppressOverlayEvents covers PopulateElementSettings running with a different element's
	///     values still in flight.
	/// </summary>
	private void OnElementTimingChanged(ElementTiming timing)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id) return;

		UpdateElement(id, el => el with
		{
			AppearAtSeconds = timing.AppearAtSeconds,
			DisappearAtSeconds = timing.DisappearAtSeconds,
			AnimationType = timing.AnimationType,
			AnimationDurationSeconds = timing.AnimationDurationSeconds
		});
	}

	/// <summary>
	///     Populates one widget instance's settings panel and swaps the left column over to show it (in
	///     place of the SOURCE/ACTION/summary cards) - the single entry point for opening settings,
	///     whether reached from a canvas click or (in the future) anywhere else that identifies a specific
	///     element. Not a separate window: an earlier popup-window version made it impossible to see the
	///     SOURCE/preview area at the same time as the settings, which is exactly what editing a widget
	///     needs.
	/// </summary>
	private void OpenElementSettings(OverlayElement element)
	{
		if (!IsTypeSupported(element.Type)) return;

		_editingElementId = element.Id;
		PopulateElementSettings(element);

		if (GetSettingsPanel(element.Type) is not { } panel) return;

		if (panel.Parent is Panel oldParent) oldParent.Children.Remove(panel);
		panel.IsVisible = true;
		WidgetSettingsPanelHost.Content = panel;
		WidgetSettingsTitleRun.Text = GetWidgetLabel(element.Type);

		CutsColumnScroll.IsVisible = false;
		UpdateCutsButton();
		SourceColumnScroll.IsVisible = false;
		WidgetSettingsColumnScroll.IsVisible = true;
		WidgetSettingsColumnScroll.Offset = default;
	}

	private void OnWidgetSettingsBackClick(object? sender, RoutedEventArgs e)
	{
		CloseElementSettings();
	}

	/// <summary>
	///     Switches the left column back to its normal SOURCE/ACTION/summary view - the settings panel
	///     itself is left alone (still parented to WidgetSettingsPanelHost, still populated) so
	///     re-opening the same instance doesn't need to rebuild anything.
	/// </summary>
	private void CloseElementSettings()
	{
		_editingElementId = null;
		WidgetSettingsColumnScroll.IsVisible = false;
		SourceColumnScroll.IsVisible = !CutsColumnScroll.IsVisible;
	}

	/// <summary>
	///     Fills one widget type's settings panel controls from a specific instance's data - the
	///     counterpart to the type-specific field-changed handlers below, which write back to whichever
	///     instance's id is currently in _editingElementId.
	/// </summary>
	private void PopulateElementSettings(OverlayElement el)
	{
		_suppressOverlayEvents = true;

		switch (el.Type)
		{
			case OverlayElementType.DateTimeText:
			{
				var x = (DateTimeTextElement)el;
				DateTimeFormatCombo.SelectedItem = DateFormatOptions.FirstOrDefault(o => o.Format == x.DateFormat) ?? DateFormatOptions[0];
				DateTimeLocaleCombo.SelectedItem = LocaleOptions.FirstOrDefault(o => o.CultureName == x.Locale) ?? LocaleOptions[0];
				DateTimeStyle.Populate(x);
				DateTimeTiming.Populate(x);
				break;
			}

			case OverlayElementType.UtcTimeText:
			{
				var x = (UtcTimeTextElement)el;
				UtcTimeFormatCombo.SelectedItem = DateFormatOptions.FirstOrDefault(o => o.Format == x.DateFormat) ?? DateFormatOptions[0];
				UtcTimeLocaleCombo.SelectedItem = LocaleOptions.FirstOrDefault(o => o.CultureName == x.Locale) ?? LocaleOptions[0];
				UtcTimeStyle.Populate(x);
				UtcTimeTiming.Populate(x);
				break;
			}

			case OverlayElementType.Elevation:
			{
				var x = (ElevationElement)el;
				ElevationLabelBox.Text = x.Label ?? DefaultElevationLabel;
				SetUnitsRadio(ElevationMetricRadio, ElevationImperialRadio, x.Units);
				ElevationStyle.Populate(x);
				ElevationTiming.Populate(x);
				break;
			}

			case OverlayElementType.Gradient:
			{
				var x = (GradientElement)el;
				GradientLabelBox.Text = x.Label ?? DefaultGradientLabel;
				GradientStyle.Populate(x);
				GradientTiming.Populate(x);
				break;
			}

			case OverlayElementType.Distance:
			{
				var x = (DistanceElement)el;
				DistanceLabelBox.Text = x.Label ?? DefaultDistanceLabel;
				SetUnitsRadio(DistanceMetricRadio, DistanceImperialRadio, x.Units);
				DistanceStyle.Populate(x);
				DistanceTiming.Populate(x);
				break;
			}

			case OverlayElementType.CameraInfo:
			{
				var x = (CameraInfoElement)el;
				CameraInfoLabelBox.Text = x.Label ?? DefaultCameraInfoLabel;
				CameraInfoStyle.Populate(x);
				CameraInfoTiming.Populate(x);
				break;
			}

			case OverlayElementType.Compass:
			{
				var x = (CompassElement)el;
				PopulateTrailControls(CompassTrailColorBox, CompassTrailColorSwatch, CompassTrailBySpeedCheck, CompassTrailWidthBox, CompassTrailArrowRadio, CompassTrailDotRadio, x);
				CompassTiming.Populate(x);
				break;
			}

			case OverlayElementType.SunWidget:
			{
				var x = (SunWidgetElement)el;
				SunStyle.Populate(x);
				SunTiming.Populate(x);
				break;
			}

			case OverlayElementType.PitchGauge:
			{
				var x = (PitchGaugeElement)el;
				PitchStyle.Populate(x);
				PitchTiming.Populate(x);
				break;
			}

			case OverlayElementType.GMeter:
			{
				var x = (GMeterElement)el;
				GMeterFullScaleBox.Value = (decimal)x.GMeterFullScaleG;
				GMeterStyle.Populate(x);
				GMeterTiming.Populate(x);
				break;
			}

			case OverlayElementType.SpeedGauge:
			{
				var x = (SpeedGaugeElement)el;
				SetUnitsRadio(SpeedMetricRadio, SpeedImperialRadio, x.Units);
				SpeedStyle.Populate(x);
				SpeedTiming.Populate(x);
				break;
			}

			case OverlayElementType.MapWidget:
			{
				var x = (MapWidgetElement)el;
				MapZoomBox.Value = x.MapZoom;
				MapDynamicZoomCheck.IsChecked = x.MapDynamicZoom;
				MapZoomOutMaxBox.Value = (decimal)x.MapDynamicZoomMaxFactor;
				MapZoomOutMaxLabel.IsVisible = x.MapDynamicZoom;
				MapZoomOutMaxBox.IsVisible = x.MapDynamicZoom;
				MapZoomOutMaxHint.IsVisible = x.MapDynamicZoom;
				PopulateTrailControls(MapTrailColorBox, MapTrailColorSwatch, MapTrailBySpeedCheck, MapTrailWidthBox, MapTrailArrowRadio, MapTrailDotRadio, x);
				MapTiming.Populate(x);
				break;
			}

			case OverlayElementType.ElapsedTimeText:
			{
				var x = (ElapsedTimeTextElement)el;
				ElapsedTimeStyle.Populate(x);
				ElapsedTimeTiming.Populate(x);
				break;
			}

			case OverlayElementType.CameraModelText:
			{
				var x = (CameraModelTextElement)el;
				CameraModelStyle.Populate(x);
				CameraModelTiming.Populate(x);
				break;
			}

			case OverlayElementType.TripProgressBar:
			{
				var x = (TripProgressBarElement)el;
				SetUnitsRadio(TripProgressMetricRadio, TripProgressImperialRadio, x.Units);
				TripProgressToleranceBox.Value = (decimal)x.TripArrivedToleranceMeters;
				TripProgressLabelBox.Text = x.TripArrivedLabel;
				TripProgressBarTiming.Populate(x);
				break;
			}
		}

		_suppressOverlayEvents = false;
	}

	private static void SetUnitsRadio(RadioButton metric, RadioButton imperial, UnitSystem units)
	{
		metric.IsChecked = units == UnitSystem.Metric;
		imperial.IsChecked = units == UnitSystem.Imperial;
	}

	private static void PopulateTrailControls(TextBox colorBox, Border swatch, CheckBox bySpeedCheck, NumericUpDown widthBox,
		RadioButton arrowRadio, RadioButton dotRadio, TrailOverlayElement element)
	{
		colorBox.Text = element.TrailColor ?? DefaultTrailColorHex;
		ColorSwatch.Update(swatch, element.TrailColor, DefaultTrailColorHex);
		bySpeedCheck.IsChecked = element.TrailColorBySpeed;
		widthBox.Value = (decimal)element.TrailWidth;
		arrowRadio.IsChecked = element.TrailUseArrow;
		dotRadio.IsChecked = !element.TrailUseArrow;
	}

	/// <summary>
	///     Shared by every widget's ElementStyleEditor - writes to whichever instance's settings panel is open.
	///     AccentColor only exists on LabeledStatElement, so it's only written there.
	/// </summary>
	private void OnElementStyleChanged(ElementStyle style)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id) return;

		UpdateElement(id, el => el switch
		{
			LabeledStatElement stat => ApplyStyle(stat) with { AccentColor = style.AccentColor },
			StyledOverlayElement styled => ApplyStyle(styled),
			_ => el
		});

		T ApplyStyle<T>(T element) where T : StyledOverlayElement
		{
			return element with
			{
				FontFamily = style.FontFamily,
				Scale = style.Scale is { } scale
					? Math.Clamp(scale, OverlayElementBounds.MinElementScale, OverlayElementBounds.MaxElementScale)
					: element.Scale,
				TextColor = style.TextColor,
				OutlineColor = style.OutlineColor,
				OutlineWidth = style.OutlineWidth ?? element.OutlineWidth
			};
		}
	}

	private void OnCompassTrailColorChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementTrailColor(id, CompassTrailColorBox.Text);
		ColorSwatch.Update(CompassTrailColorSwatch, CompassTrailColorBox.Text, DefaultTrailColorHex);
	}

	private void OnCompassTrailBySpeedChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementTrailColorBySpeed(id, CompassTrailBySpeedCheck.IsChecked == true);
	}

	private void OnCompassTrailWidthChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || CompassTrailWidthBox.Value is not { } width) return;
		SetElementTrailWidth(id, (float)width);
	}

	private void OnCompassTrailMarkerChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementTrailUseArrow(id, CompassTrailArrowRadio.IsChecked == true);
	}

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
		var label = string.IsNullOrWhiteSpace(TripProgressLabelBox.Text)
			? OverlayRenderer.TripArrivedLabelDefault
			: TripProgressLabelBox.Text.Trim();
		UpdateElement(id, el => el is TripProgressBarElement t ? t with { TripArrivedLabel = label } : el);
	}

	private void OnMapZoomChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || MapZoomBox.Value is not { } zoom) return;
		UpdateElement(id, el => el is MapWidgetElement m ? m with { MapZoom = (int)zoom } : el);
	}

	private void OnMapDynamicZoomChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;

		var enabled = MapDynamicZoomCheck.IsChecked == true;
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

	private void OnMapTrailColorChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementTrailColor(id, MapTrailColorBox.Text);
		ColorSwatch.Update(MapTrailColorSwatch, MapTrailColorBox.Text, DefaultTrailColorHex);
	}

	private void OnMapTrailBySpeedChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementTrailColorBySpeed(id, MapTrailBySpeedCheck.IsChecked == true);
	}

	private void OnMapTrailWidthChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id || MapTrailWidthBox.Value is not { } width) return;
		SetElementTrailWidth(id, (float)width);
	}

	private void OnMapTrailMarkerChanged(object? sender, RoutedEventArgs e)
	{
		if (_editingElementId is not { } id) return;
		SetElementTrailUseArrow(id, MapTrailArrowRadio.IsChecked == true);
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
		RefreshWidgetList();
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
		RefreshWidgetList();
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
		RefreshWidgetList();
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
		RefreshWidgetList();
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

		RefreshWidgetList();
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
		RefreshWidgetList();
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

	/// <summary>
	///     Where the preview image sits in the canvas - fitted (letterboxed) or zoomed and panned
	///     (MainWindow.PreviewZoom.cs; ApplyPreviewLayout places the image by this) - and the shared
	///     scale/offset math for converting between a point on the canvas control and a pixel
	///     in the full-res video, used both directions: mapping a click/drag/drop to a render position
	///     (MapCanvasPointToFullRes) and placing UI - the hover icons, selection box, guide lines - back
	///     over a position in the full-res frame (MapFullResPointToCanvas).
	/// </summary>
	private (double Scale, double OffsetX, double OffsetY, double RenderedWidth, double RenderedHeight, double FullResScale)?
		GetPreviewTransform()
	{
		if (_summary is null || _previewBitmap is null) return null;

		var controlWidth = OverlayDragCanvas.Bounds.Width;
		var controlHeight = OverlayDragCanvas.Bounds.Height;
		var bitmapWidth = _previewBitmap.PixelSize.Width;
		var bitmapHeight = _previewBitmap.PixelSize.Height;
		if (controlWidth <= 0 || controlHeight <= 0 || bitmapWidth <= 0 || bitmapHeight <= 0) return null;

		// The preview bitmap is a uniformly downscaled copy of the full render resolution.
		var fullResScale = _summary.Video.Width / (double)bitmapWidth;
		var scale = _previewZoom is { } zoom
			? zoom * fullResScale / RenderScaling
			: Math.Min(controlWidth / bitmapWidth, controlHeight / bitmapHeight);
		var renderedWidth = bitmapWidth * scale;
		var renderedHeight = bitmapHeight * scale;
		var offsetX = (controlWidth - renderedWidth) / 2 + _previewPan.X;
		var offsetY = (controlHeight - renderedHeight) / 2 + _previewPan.Y;

		return (scale, offsetX, offsetY, renderedWidth, renderedHeight, fullResScale);
	}

	private Point? MapCanvasPointToFullRes(Point canvasPoint)
	{
		if (GetPreviewTransform() is not { } t) return null;

		var localX = canvasPoint.X - t.OffsetX;
		var localY = canvasPoint.Y - t.OffsetY;
		if (localX < 0 || localY < 0 || localX > t.RenderedWidth || localY > t.RenderedHeight) return null;

		return new Point(localX / t.Scale * t.FullResScale, localY / t.Scale * t.FullResScale);
	}

	/// <summary>Like MapCanvasPointToFullRes, also for a point outside the image (the zoom's pivot).</summary>
	private Point? MapCanvasPointToFullResUnclamped(Point canvasPoint)
	{
		if (GetPreviewTransform() is not { } t) return null;

		return new Point((canvasPoint.X - t.OffsetX) / t.Scale * t.FullResScale, (canvasPoint.Y - t.OffsetY) / t.Scale * t.FullResScale);
	}

	private Point? MapFullResPointToCanvas(double fullResX, double fullResY)
	{
		if (GetPreviewTransform() is not { } t) return null;

		return new Point(t.OffsetX + fullResX / t.FullResScale * t.Scale, t.OffsetY + fullResY / t.FullResScale * t.Scale);
	}

	/// <summary>
	///     Rule-of-thirds + safe-margin guide lines over the preview, editor-only - purely a positioning
	///     aid, never baked into the actual render (OverlayRenderer never draws these). The margin box
	///     uses the exact same OverlayElementBounds.Margin OverlayPreset.CreateDefault positions widgets
	///     within, so it visibly matches where widgets land by default.
	/// </summary>
	/// <summary>
	///     Entries in the grid button's Flyout (GridModeOffButton/GridModeThirdsButton/
	///     GridModeMarginButton/GridModeBothButton, matched by Name) - picks which of UpdatePreviewGuides' two
	///     guide layers are drawn. Independent of _snapToGuides (OnToggleSnapClick).
	/// </summary>
	private void OnGridModeClick(object? sender, RoutedEventArgs e)
	{
		if (sender is not Button button) return;

		_gridMode = button.Name switch
		{
			nameof(GridModeThirdsButton) => PreviewGridMode.Thirds,
			nameof(GridModeMarginButton) => PreviewGridMode.Margin,
			nameof(GridModeBothButton) => PreviewGridMode.Both,
			_ => PreviewGridMode.Off
		};

		ApplyGridModeButtonClasses();
		ToggleGridButton.Flyout?.Hide();
		UpdatePreviewGuides();
		OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { PreviewGridMode = _gridMode.ToString() });
	}

	private void ApplyGridModeButtonClasses()
	{
		GridModeOffButton.Classes.Set("active", _gridMode == PreviewGridMode.Off);
		GridModeThirdsButton.Classes.Set("active", _gridMode == PreviewGridMode.Thirds);
		GridModeMarginButton.Classes.Set("active", _gridMode == PreviewGridMode.Margin);
		GridModeBothButton.Classes.Set("active", _gridMode == PreviewGridMode.Both);
		ToggleGridButton.Classes.Set("active", _gridMode != PreviewGridMode.Off);
	}

	/// <summary>
	///     Toolbar toggle above the preview - whether a dragged widget snaps to the guide lines
	///     (OnOverlayCanvasPointerMoved / SnapToGuides), independent of whether those lines are even shown
	///     (_gridMode) - matches "Show Grid" vs. "Snap to Grid" being separate settings in editing software.
	/// </summary>
	private void OnToggleSnapClick(object? sender, RoutedEventArgs e)
	{
		_snapToGuides = !_snapToGuides;
		ToggleSnapButton.Classes.Set("active", _snapToGuides);
		OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { PreviewSnapToGrid = _snapToGuides });
	}

	private void UpdatePreviewGuides()
	{
		var showThirds = _gridMode is PreviewGridMode.Thirds or PreviewGridMode.Both;
		var showMargin = _gridMode is PreviewGridMode.Margin or PreviewGridMode.Both;

		if ((!showThirds && !showMargin) || GetPreviewTransform() is null || _summary is null)
		{
			PreviewGridVLine1.IsVisible = false;
			PreviewGridVLine2.IsVisible = false;
			PreviewGridHLine1.IsVisible = false;
			PreviewGridHLine2.IsVisible = false;
			PreviewSafeMarginBox.IsVisible = false;
			return;
		}

		var scale = OverlayElementBounds.GetScale(_summary.Video.Width, _summary.Video.Height);
		var margin = OverlayElementBounds.Margin * scale;
		if (MapFullResPointToCanvas(margin, margin) is not var (left, top) ||
		    MapFullResPointToCanvas(_summary.Video.Width - margin, _summary.Video.Height - margin) is not { } bottomRight)
		{
			PreviewGridVLine1.IsVisible = false;
			PreviewGridVLine2.IsVisible = false;
			PreviewGridHLine1.IsVisible = false;
			PreviewGridHLine2.IsVisible = false;
			PreviewSafeMarginBox.IsVisible = false;
			return;
		}

		// The rule-of-thirds lines are drawn within the same safe-margin box (not the full video frame) so
		// both guides share one set of bounds - otherwise the thirds lines poke past the margin box's own
		// top/bottom border out to the true video edge, which looked broken.
		var right = bottomRight.X;
		var bottom = bottomRight.Y;
		var width = right - left;
		var height = bottom - top;

		PreviewGridVLine1.StartPoint = new Point(left + width / 3, top);
		PreviewGridVLine1.EndPoint = new Point(left + width / 3, bottom);
		PreviewGridVLine2.StartPoint = new Point(left + width * 2 / 3, top);
		PreviewGridVLine2.EndPoint = new Point(left + width * 2 / 3, bottom);
		PreviewGridHLine1.StartPoint = new Point(left, top + height / 3);
		PreviewGridHLine1.EndPoint = new Point(right, top + height / 3);
		PreviewGridHLine2.StartPoint = new Point(left, top + height * 2 / 3);
		PreviewGridHLine2.EndPoint = new Point(right, top + height * 2 / 3);
		PreviewGridVLine1.IsVisible = showThirds;
		PreviewGridVLine2.IsVisible = showThirds;
		PreviewGridHLine1.IsVisible = showThirds;
		PreviewGridHLine2.IsVisible = showThirds;

		Canvas.SetLeft(PreviewSafeMarginBox, left);
		Canvas.SetTop(PreviewSafeMarginBox, top);
		PreviewSafeMarginBox.Width = Math.Max(0, width);
		PreviewSafeMarginBox.Height = Math.Max(0, height);
		PreviewSafeMarginBox.IsVisible = showMargin;
	}

	private const double SnapThresholdCanvasPixels = 8;

	/// <summary>
	///     Full-res-space X/Y positions of the guide lines UpdatePreviewGuides draws (margin box edges
	///     + rule-of-thirds), shared with SnapToGuides so a dragged widget aligns to the same lines it sees.
	/// </summary>
	private (float[] X, float[] Y) GetGuideTargets()
	{
		var scale = OverlayElementBounds.GetScale(_summary!.Video.Width, _summary.Video.Height);
		var margin = OverlayElementBounds.Margin * scale;
		var width = _summary.Video.Width - margin * 2;
		var height = _summary.Video.Height - margin * 2;

		float[] x = [margin, margin + width / 3, margin + width * 2 / 3, margin + width];
		float[] y = [margin, margin + height / 3, margin + height * 2 / 3, margin + height];
		return (x, y);
	}

	/// <summary>
	///     Snaps a dragged widget to the nearest guide line when it's within a small on-screen distance
	///     - purely an editor convenience on top of OnOverlayCanvasPointerMoved. Checks the widget's actual
	///     bounding-box edges and center (via OverlayElementBounds.GetBounds) against the guides, not just the
	///     raw anchor X/Y - most widget types anchor at a corner, not their visual center, so snapping the raw
	///     anchor alone only ever lined up center-anchored types (gauges, the progress bar) by coincidence.
	/// </summary>
	private (float X, float Y) SnapToGuides(OverlayElement element, float x, float y)
	{
		if (!_snapToGuides || _summary is null || GetPreviewTransform() is not { } t) return (x, y);

		var scale = OverlayElementBounds.GetScale(_summary.Video.Width, _summary.Video.Height);
		SKRect bounds = GetElementBounds(element, x, y, scale * element.Scale);
		var (xTargets, yTargets) = GetGuideTargets();
		var threshold = (float)(SnapThresholdCanvasPixels * t.FullResScale / t.Scale);

		float[] xOffsets = bounds.IsEmpty ? [0f] : [bounds.Left - x, bounds.MidX - x, bounds.Right - x];
		float[] yOffsets = bounds.IsEmpty ? [0f] : [bounds.Top - y, bounds.MidY - y, bounds.Bottom - y];

		return (SnapAxis(x, xOffsets, xTargets, threshold), SnapAxis(y, yOffsets, yTargets, threshold));
	}

	private static float SnapAxis(float anchor, float[] edgeOffsets, float[] targets, float threshold)
	{
		var best = anchor;
		var bestDistance = threshold;
		foreach (var offset in edgeOffsets)
		{
			var edge = anchor + offset;
			foreach (var target in targets)
			{
				var distance = Math.Abs(edge - target);
				if (distance >= bestDistance) continue;

				bestDistance = distance;
				best = target - offset;
			}
		}

		return best;
	}

	/// <summary>
	///     Topmost drawn element whose bounds contain `pos`, or null - shared by the drag hit-test and the hover
	///     state. Skips widgets this file has no data for: the preview doesn't draw them (ApplyAvailability),
	///     so they mustn't be draggable or open settings from an empty spot on the canvas.
	/// </summary>
	private OverlayElement? FindElementAt(Point pos)
	{
		if (_summary is null) return null;

		var scale = OverlayElementBounds.GetScale(_summary.Video.Width, _summary.Video.Height);
		List<OverlayElement> elements = ActiveElements;
		for (var i = elements.Count - 1; i >= 0; i--)
		{
			OverlayElement el = elements[i];
			if (!el.Visible || !IsTypeSupported(el.Type)) continue;

			SKRect bounds = GetElementBounds(el, el.X, el.Y, scale * el.Scale);
			if (pos.X >= bounds.Left && pos.X <= bounds.Right && pos.Y >= bounds.Top && pos.Y <= bounds.Bottom) return el;
		}

		return null;
	}

	/// <summary>
	///     Maps a widget type to its hidden settings panel (see the XAML - each XxxSettingsPanel Border is
	///     IsVisible="False" in its original spot in the widget palette, kept only so OpenElementSettings
	///     has something to adopt and show in WidgetSettingsPanelHost).
	/// </summary>
	private Control? GetSettingsPanel(OverlayElementType type)
	{
		return type switch
		{
			OverlayElementType.DateTimeText => DateTimeSettingsPanel,
			OverlayElementType.UtcTimeText => UtcTimeSettingsPanel,
			OverlayElementType.Elevation => ElevationSettingsPanel,
			OverlayElementType.Gradient => GradientSettingsPanel,
			OverlayElementType.Distance => DistanceSettingsPanel,
			OverlayElementType.CameraInfo => CameraInfoSettingsPanel,
			OverlayElementType.Compass => CompassSettingsPanel,
			OverlayElementType.SunWidget => SunSettingsPanel,
			OverlayElementType.PitchGauge => PitchSettingsPanel,
			OverlayElementType.GMeter => GMeterSettingsPanel,
			OverlayElementType.ElapsedTimeText => ElapsedTimeSettingsPanel,
			OverlayElementType.CameraModelText => CameraModelSettingsPanel,
			OverlayElementType.SpeedGauge => SpeedSettingsPanel,
			OverlayElementType.MapWidget => MapSettingsPanel,
			OverlayElementType.TripProgressBar => TripProgressBarSettingsPanel,
			_ => null
		};
	}

	/// <summary>
	///     Starts an OS-level drag from a palette row - the counterpart to OnOverlayCanvasDrop,
	///     which turns the drop into a brand-new widget instance (see AddElementInstance).
	/// </summary>
	private async void OnWidgetItemPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Border { Tag: OverlayElementType type } item || !item.IsEnabled) return;

		var data = new DataTransfer();
		data.Add(DataTransferItem.Create(WidgetDragFormat, type.ToString()));
		await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
	}

	private bool CanAcceptWidgetDrop(DragEventArgs e, out OverlayElementType type)
	{
		type = default;
		if (_summary is null || IsActivePresetDefault) return false;
		if (e.DataTransfer.TryGetValue(WidgetDragFormat) is not { } name || !Enum.TryParse(name, out type)) return false;

		return IsTypeSupported(type);
	}

	private void OnOverlayCanvasDragOver(object? sender, DragEventArgs e)
	{
		e.DragEffects = CanAcceptWidgetDrop(e, out _) ? DragDropEffects.Move : DragDropEffects.None;
	}

	private void OnOverlayCanvasDrop(object? sender, DragEventArgs e)
	{
		if (!CanAcceptWidgetDrop(e, out OverlayElementType type)) return;
		if (MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas)) is not { } pos) return;

		var x = (float)Math.Clamp(pos.X, 0, _summary!.Video.Width);
		var y = (float)Math.Clamp(pos.Y, 0, _summary.Video.Height);
		AddElementInstance(type, x, y);
	}

	private void OnOverlayCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (TryStartPreviewPan(e)) return;
		if (_summary is null || IsActivePresetDefault) return;
		if (!e.GetCurrentPoint(OverlayDragCanvas).Properties.IsLeftButtonPressed) return;
		if (MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas)) is not { } pos) return;
		if (FindElementAt(pos) is not { } el) return;

		// Dragging needs a stable frame to align against, and it eliminates a real race:
		// without pausing, the playback thread keeps calling Render() on the same elements
		// list this drag is about to replace concurrently.
		if (_previewPlayer.IsPlaying)
		{
			_previewPlayer.Pause();
			ShowPlayingState(false);
		}

		_draggingElementId = el.Id;
		_dragAnchorOffset = new Point(pos.X - el.X, pos.Y - el.Y);
		e.Pointer.Capture(OverlayDragCanvas);
		OverlayDragCanvas.Cursor = SizeAllCursor;
		HideHoverIcons();

		if (_selectedElementId != el.Id)
		{
			_selectedElementId = el.Id;
			RebuildAddedWidgetsList();
		}

		RefreshSelectionHighlight();
	}

	private void OnOverlayCanvasPointerMoved(object? sender, PointerEventArgs e)
	{
		if (ContinuePreviewPan(e)) return;
		if (_resizingElementId is { } resizingId)
		{
			UpdateResize(resizingId, e);
			return;
		}

		if (_draggingElementId is not { } id)
		{
			UpdateHoverState(e);
			return;
		}

		if (_summary is null) return;
		if (MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas)) is not { } pos) return;

		var newX = (float)Math.Clamp(pos.X - _dragAnchorOffset.X, 0, _summary.Video.Width);
		var newY = (float)Math.Clamp(pos.Y - _dragAnchorOffset.Y, 0, _summary.Video.Height);

		List<OverlayElement> elements = [.. ActiveElements];
		var index = elements.FindIndex(el => el.Id == id);
		if (index < 0) return;

		(newX, newY) = SnapToGuides(elements[index], newX, newY);

		elements[index] = elements[index] with { X = newX, Y = newY };
		ReplaceActiveElements(elements);
		_previewPlayer.SetLayout(elements);
		if (_selectedElementId == id) RefreshSelectionHighlight();
	}

	/// <summary>
	///     Starts a resize drag from ResizeHandle - a corner grab at the selected widget's own
	///     bottom-right bound, sized by the ratio of the pointer's current vs. starting distance from the
	///     widget's anchor (element.X/Y) to its own OverlayElement.Scale at press time, so dragging away from
	///     the anchor grows it and dragging toward it shrinks it, uniformly (both axes, one multiplier).
	/// </summary>
	private void OnResizeHandlePointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (_summary is null || IsActivePresetDefault) return;
		if (_selectedElementId is not { } id) return;
		if (ActiveElements.FirstOrDefault(el => el.Id == id) is not { Visible: true } el) return;
		if (MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas)) is not { } pos) return;

		if (_previewPlayer.IsPlaying)
		{
			_previewPlayer.Pause();
			ShowPlayingState(false);
		}

		_resizingElementId = id;
		_resizeStartScale = el.Scale;
		_resizeStartDistance = Math.Max(Point.Distance(pos, new Point(el.X, el.Y)), 1);
		e.Pointer.Capture(OverlayDragCanvas);
		e.Handled = true;
		HideHoverIcons();
	}

	private void UpdateResize(string id, PointerEventArgs e)
	{
		if (_summary is null) return;
		if (MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas)) is not { } pos) return;

		List<OverlayElement> elements = [.. ActiveElements];
		var index = elements.FindIndex(el => el.Id == id);
		if (index < 0) return;

		OverlayElement el = elements[index];
		var distance = Point.Distance(pos, new Point(el.X, el.Y));
		var newScale = (float)Math.Clamp(_resizeStartScale * (distance / _resizeStartDistance), OverlayElementBounds.MinElementScale, OverlayElementBounds.MaxElementScale);

		elements[index] = el with { Scale = newScale };
		ReplaceActiveElements(elements);
		_previewPlayer.SetLayout(elements);
		if (_selectedElementId == id) RefreshSelectionHighlight();
	}

	/// <summary>
	///     Capture can be lost without a PointerReleased ever arriving (Alt+Tab, a system dialog mid-drag) -
	///     without this the drag/resize would stay "stuck" to the pointer and the final position never saved.
	///     The Released handler clears its state before releasing capture, so it doesn't end up here twice.
	/// </summary>
	private void OnOverlayCanvasPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
	{
		_panning = false;
		if (_resizingElementId is null && _draggingElementId is null) return;

		_resizingElementId = null;
		_draggingElementId = null;
		OverlayDragCanvas.Cursor = null;
		SaveOverlayPresets();
	}

	private void OnOverlayCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (EndPreviewPan(e)) return;
		if (_resizingElementId is not null)
		{
			_resizingElementId = null;
			e.Pointer.Capture(null);
			UpdateHoverState(e);
			SaveOverlayPresets();
			return;
		}

		if (_draggingElementId is null) return;

		_draggingElementId = null;
		e.Pointer.Capture(null);
		UpdateHoverState(e);
		SaveOverlayPresets();
	}

	private void UpdateHoverState(PointerEventArgs e)
	{
		if (_summary is null || IsActivePresetDefault)
		{
			OverlayDragCanvas.Cursor = null;
			HideHoverIcons();
			return;
		}

		Point canvasPos = e.GetPosition(OverlayDragCanvas);

		// Ignore while the pointer is over one of the hover icons themselves - they sit at a widget's
		// corner, partly outside the widget's own hit-test bounds, so re-running the hit test below
		// would otherwise hide them out from under the pointer before a click can land.
		if (IsPointerOverHoverIcon(canvasPos)) return;

		Point? pos = MapCanvasPointToFullRes(canvasPos);
		OverlayElement? hovered = pos is { } p ? FindElementAt(p) : null;
		OverlayDragCanvas.Cursor = hovered is not null ? HandCursor : null;

		if (hovered is null)
		{
			HideHoverIcons();
			return;
		}

		ShowHoverIconsFor(hovered);
	}

	private bool IsPointerOverHoverIcon(Point canvasPos)
	{
		return (WidgetGearHoverButton.IsVisible && GetCanvasChildRect(WidgetGearHoverButton).Contains(canvasPos))
		       || (RemoveWidgetButton.IsVisible && GetCanvasChildRect(RemoveWidgetButton).Contains(canvasPos));
	}

	private static Rect GetCanvasChildRect(Control control)
	{
		return new Rect(Canvas.GetLeft(control), Canvas.GetTop(control), control.Bounds.Width, control.Bounds.Height);
	}

	/// <summary>Positions the gear/remove pair at a widget's top-right corner, gear to the left of remove.</summary>
	private void ShowHoverIconsFor(OverlayElement element)
	{
		var scale = OverlayElementBounds.GetScale(_summary!.Video.Width, _summary.Video.Height);
		SKRect bounds = GetElementBounds(element, element.X, element.Y, scale * element.Scale);
		if (MapFullResPointToCanvas(bounds.Right, bounds.Top) is not { } corner)
		{
			HideHoverIcons();
			return;
		}

		_hoveredElementId = element.Id;

		const double gap = 4;
		Canvas.SetLeft(RemoveWidgetButton, corner.X - RemoveWidgetButton.Width / 2);
		Canvas.SetTop(RemoveWidgetButton, corner.Y - RemoveWidgetButton.Height / 2);
		Canvas.SetLeft(WidgetGearHoverButton, corner.X - RemoveWidgetButton.Width / 2 - gap - WidgetGearHoverButton.Width);
		Canvas.SetTop(WidgetGearHoverButton, corner.Y - WidgetGearHoverButton.Height / 2);

		RemoveWidgetButton.IsVisible = true;
		WidgetGearHoverButton.IsVisible = true;
	}

	private void HideHoverIcons()
	{
		_hoveredElementId = null;
		RemoveWidgetButton.IsVisible = false;
		WidgetGearHoverButton.IsVisible = false;
	}

	private void OnWidgetGearHoverButtonClick(object? sender, RoutedEventArgs e)
	{
		if (_hoveredElementId is not { } id || ActiveElements.FirstOrDefault(el => el.Id == id) is not { } element) return;
		OpenElementSettings(element);
	}

	private void OnRemoveWidgetButtonClick(object? sender, RoutedEventArgs e)
	{
		if (_hoveredElementId is not { } id) return;
		HideHoverIcons();
		RemoveElementInstance(id);
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

}
