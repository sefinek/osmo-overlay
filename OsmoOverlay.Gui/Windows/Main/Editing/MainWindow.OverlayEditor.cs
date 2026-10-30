using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using OsmoOverlay.Core.Overlay;
using SkiaSharp;

namespace OsmoOverlay.Gui;

/// <summary>
///     The Overlay card: preset management, the widget palette (drag onto the preview canvas to add -
///     several instances of the same type are allowed, see AddElementInstance), the "on overlay" list of
///     what's currently placed, and dragging/removing/configuring elements on the preview canvas itself.
///     A widget's settings open from its gear - on the canvas (shown on hover, next to the remove button) and on its
///     "on overlay" row - or from its clip on the layer timeline; the palette is for adding only.
/// </summary>
public partial class MainWindow
{
	private static readonly List<DateFormatOption> DateFormatOptions =
	[
		new(Strings.Editor_DateFormatDefault, null),
		new("yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd  HH:mm:ss"),
		new("MM/dd/yyyy hh:mm:ss tt", "MM/dd/yyyy  hh:mm:ss tt"),
		new("yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd  HH:mm:ss"),
		new("dd MMM yyyy HH:mm", "dd MMM yyyy  HH:mm"),
		new("HH:mm:ss", "HH:mm:ss")
	];

	private static readonly List<LocaleOption> LocaleOptions = BuildLocaleOptions();

	private static readonly List<GpsAlertMessageOption> GpsAlertMessageOptions =
	[
		new(Strings.GpsAlert_MessageSearching, GpsAlertMessage.Searching),
		new(Strings.GpsAlert_MessageLost, GpsAlertMessage.Lost),
		new(Strings.GpsAlert_MessageFound, GpsAlertMessage.Found),
		new(Strings.GpsAlert_MessageBack, GpsAlertMessage.Back),
		new(Strings.GpsAlert_MessagePositionCorrected, GpsAlertMessage.PositionCorrected),
		new(Strings.GpsAlert_MessageSpeedCorrected, GpsAlertMessage.SpeedCorrected),
		new(Strings.GpsAlert_MessagePositionAndSpeedCorrected, GpsAlertMessage.PositionAndSpeedCorrected)
	];

	private static readonly List<TripStatOption> TripStatOptions =
	[
		new(Strings.Common_MaxSpeed, TripStatKind.MaxSpeed),
		new(Strings.Editor_StatAverageSpeed, TripStatKind.AverageSpeed),
		new(Strings.Settings_ElevationGain, TripStatKind.ElevationGain),
		new(Strings.Editor_StatElevationLoss, TripStatKind.ElevationLoss),
		new(Strings.Editor_StatMovingTime, TripStatKind.MovingTime),
		new(Strings.Editor_StatMaxHeight, TripStatKind.MaxHeight),
		new(Strings.Editor_StatMaxHomeDistance, TripStatKind.MaxHomeDistance)
	];

	private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
	private static readonly Cursor SizeAllCursor = new(StandardCursorType.SizeAll);
	private static readonly Cursor ResizeCursor = new(StandardCursorType.BottomRightCorner);

	/// <summary>
	///     Custom drag-and-drop format used to carry an OverlayElementType through a widget-list-to-canvas
	///     drag. DataFormat&lt;T&gt; requires a reference type, so the enum travels as its name string.
	/// </summary>
	// A save writes every preset's file - typing goes through this rather than saving on every key.
	private readonly DispatcherTimer _presetSaveDelay = new() { Interval = TimeSpan.FromMilliseconds(500) };

	private static readonly DataFormat<string> WidgetDragFormat =
		DataFormat.CreateInProcessFormat<string>("OsmoOverlay.OverlayElementType");

	private const double DragDropThreshold = 4;
	private (OverlayElementType Type, PointerPressedEventArgs Pressed)? _widgetDragStart;

	// FirstOrDefault, not First: there's a narrow window right after picking a file where
	// _summary is already set but LoadOverlayPresets (an earlier await) hasn't finished yet, so a
	// pointer click on the drag canvas in that gap must not crash on an empty/stale preset list.
	private List<OverlayElement> ActiveElements =>
		_overlayPresets.FirstOrDefault(p => p.Id == _activePresetId)?.Elements ?? [];

	/// <summary>
	///     The built-in presets (Default and the drone one) are read-only, so there's always one untouched baseline layout
	///     to fall back to or Duplicate from - editing it directly would mean losing that baseline the
	///     first time someone drags a widget.
	/// </summary>
	private bool IsActivePresetDefault => OverlayPresetStore.IsBuiltIn(_activePresetId);

	private void LoadOverlayPresets()
	{
		(_overlayPresets, _activePresetId) = OverlayPresetStore.Load();
		_overlayPresetsLoaded = true;
		RefreshPresetComboBox();
		RefreshWidgetList();
		ShowLayout();
		// Re-evaluate SetPhase's visibility now that _overlayPresetsLoaded flipped - the Overlay
		// panel (New/Duplicate/etc.) only actually appears from this point on, see the field's doc.
		SetPhase(_phase);
	}

	private void SaveOverlayPresets()
	{
		_presetSaveDelay.Stop();
		OverlayPresetStore.Save(_overlayPresets, _activePresetId);
	}

	/// <summary>For edits arriving key by key: saved once they pause, at the latest when the window closes (OnClosed).</summary>
	private void SaveOverlayPresetsSoon()
	{
		_presetSaveDelay.Stop();
		_presetSaveDelay.Start();
	}

	private void RefreshPresetComboBox()
	{
		_suppressOverlayEvents = true;
		PresetComboBox.ItemsSource = _overlayPresets.Select(p => new PresetOption(p.Id, p.Name)).ToList();
		PresetComboBox.SelectedIndex = _overlayPresets.FindIndex(p => p.Id == _activePresetId);
		_suppressOverlayEvents = false;
		RefreshCompareChoices();
	}

	/// <summary>
	///     Refreshes everything driven by the active preset's element list except a widget's own settings
	///     panel (the left column's inline widget-settings view), which is populated on demand instead
	///     (see PopulateElementSettings) - with several instances of a type possibly on the canvas at
	///     once, there's no single "the" instance left to eagerly bind every panel's fields to.
	/// </summary>
	private void RefreshWidgetList()
	{
		// A preset whose layers were never set up (loaded, switched to, reset, imported) gets them here, in its draw order.
		if (ActivePreset is { Layers: null }) NormalizeActivePreset(ActiveElements, null);

		List<OverlayElement> elements = ActiveElements;
		bool editable = !IsActivePresetDefault;

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

		SortPaletteByAvailability();

		DateTimeFallbackWarningIcon.IsVisible = UsesTimeFallback(OverlayElementType.DateTimeText);
		UtcTimeFallbackWarningIcon.IsVisible = UsesTimeFallback(OverlayElementType.UtcTimeText);
		ToolTip.SetTip(DateTimeFallbackWarningIcon, TimeFallbackTip);
		ToolTip.SetTip(UtcTimeFallbackWarningIcon, TimeFallbackTip);

		RebuildAddedWidgetsList();
		RefreshSelectionHighlight();
		UpdatePreviewGuides();
		RefreshLayers();

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
			bool dataOk = IsTypeSupported(type);
			listItem.IsEnabled = presetEditable && dataOk;
			ToolTip.SetTip(listItem, dataOk ? null : UnsupportedReason(type));
		}
	}

	private StackPanel? _paletteRows;
	private List<Control>? _paletteRowOrder;

	/// <summary>
	///     The palette keeps its designed order, except that the widgets this recording has no data for (no GPS, another camera's
	///     sensors) go to the bottom - in the same relative order - so the ones that can be used are at the top.
	/// </summary>
	private void SortPaletteByAvailability()
	{
		if (_paletteRows is null)
		{
			if (DateTimeListItem.Parent?.Parent is not StackPanel rows) return;

			_paletteRows = rows;
			_paletteRowOrder = [.. rows.Children];
		}

		HashSet<Control> unavailable = [];
		foreach (OverlayElementType type in Enum.GetValues<OverlayElementType>())
		{
			if (GetPaletteItem(type) is not { } item || IsTypeSupported(type)) continue;

			Control row = item;
			while (row.Parent is not null && row.Parent != _paletteRows) row = (Control)row.Parent;
			if (row.Parent == _paletteRows) unavailable.Add(row);
		}

		Control[] sorted = [.. _paletteRowOrder!.Where(r => !unavailable.Contains(r)), .. _paletteRowOrder!.Where(unavailable.Contains)];
		if (_paletteRows.Children.SequenceEqual(sorted)) return;

		_paletteRows.Children.Clear();
		_paletteRows.Children.AddRange(sorted);
	}

	// Shown whenever the widget is actually usable but only through the container-time fallback
	// (see OverlayRenderer.DrawTimeText) - not real GPS-recorded time, so a driving log synced
	// against this against other GPS-timestamped data could be off by however stale the camera's
	// own clock is.
	private static string TimeFallbackTip => Strings.Editor_TimeFallbackTip;

	private bool IsTypeSupported(OverlayElementType type)
	{
		return OverlayDataRequirements.IsSupported(type, _availability);
	}

	private bool UsesTimeFallback(OverlayElementType type)
	{
		return type is OverlayElementType.DateTimeText or OverlayElementType.UtcTimeText && _availability is { GpsTimestamp: false, ContainerTime: true };
	}

	private static string UnsupportedReason(OverlayElementType type)
	{
		return type switch
		{
			OverlayElementType.DateTimeText or OverlayElementType.UtcTimeText =>
				Strings.Editor_UnavailableNoTime,
			OverlayElementType.SunWidget => Strings.Editor_UnavailableSun,
			OverlayElementType.RollGauge or OverlayElementType.PitchGauge or OverlayElementType.GMeter =>
				Strings.Editor_UnavailableAxes,
			OverlayElementType.CameraInfo => Strings.Editor_UnavailableCameraInfo,
			OverlayElementType.AmbientTemperature => Strings.Editor_UnavailableAmbientTemperature,
			OverlayElementType.ImuTemperature => Strings.Editor_UnavailableImuTemperature,
			OverlayElementType.DeviceDistance => Strings.Editor_UnavailableDeviceDistance,
			OverlayElementType.DeviceBattery => Strings.Editor_UnavailableDeviceBattery,
			OverlayElementType.AudioLevel => Strings.Editor_UnavailableAudioLevel,
			OverlayElementType.WindProcessing => Strings.Editor_UnavailableWindProcessing,
			OverlayElementType.ImageLuma => Strings.Editor_UnavailableImageLuma,
			OverlayElementType.Height or OverlayElementType.HomeDistance or OverlayElementType.VerticalSpeed or OverlayElementType.Satellites or OverlayElementType.DroneBattery
				or OverlayElementType.FlightAlerts or OverlayElementType.Signal or OverlayElementType.FlightMode or OverlayElementType.RcSticks
				or OverlayElementType.AttitudeIndicator or OverlayElementType.Orientation3D => Strings.Editor_UnavailableDroneLog,
			_ => Strings.Editor_UnavailableGps
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
			OverlayElementType.AmbientTemperature => AmbientTemperatureListItem,
			OverlayElementType.ImuTemperature => ImuTemperatureListItem,
			OverlayElementType.DeviceDistance => DeviceDistanceListItem,
			OverlayElementType.DeviceBattery => DeviceBatteryListItem,
			OverlayElementType.AudioLevel => AudioLevelListItem,
			OverlayElementType.WindProcessing => WindProcessingListItem,
			OverlayElementType.ImageLuma => ImageLumaListItem,
			OverlayElementType.Height => HeightListItem,
			OverlayElementType.HomeDistance => HomeDistanceListItem,
			OverlayElementType.VerticalSpeed => VerticalSpeedListItem,
			OverlayElementType.FlightMode => FlightModeListItem,
			OverlayElementType.Satellites => SatellitesListItem,
			OverlayElementType.Compass => CompassListItem,
			OverlayElementType.SunWidget => SunListItem,
			OverlayElementType.RollGauge => RollListItem,
			OverlayElementType.PitchGauge => PitchListItem,
			OverlayElementType.GMeter => GMeterListItem,
			OverlayElementType.ElapsedTimeText => ElapsedTimeListItem,
			OverlayElementType.CameraModelText => CameraModelListItem,
			OverlayElementType.SpeedGauge => SpeedListItem,
			OverlayElementType.MapWidget => MapListItem,
			OverlayElementType.TripProgressBar => TripProgressBarListItem,
			OverlayElementType.ProfileChart => ProfileChartListItem,
			OverlayElementType.TripStat => TripStatListItem,
			OverlayElementType.Text => TextListItem,
			OverlayElementType.Image => ImageListItem,
			OverlayElementType.MapAttribution => MapCreditListItem,
			OverlayElementType.Watermark => WatermarkListItem,
			OverlayElementType.GpsAlert => GpsAlertListItem,
			OverlayElementType.DroneBattery => DroneBatteryListItem,
			OverlayElementType.FlightAlerts => FlightAlertsListItem,
			OverlayElementType.Signal => SignalListItem,
			OverlayElementType.RcSticks => RcSticksListItem,
			OverlayElementType.AttitudeIndicator => AttitudeListItem,
			OverlayElementType.Orientation3D => Orientation3DListItem,
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
		List<(OverlayElement Element, string Name)> visible = VisibleWidgetNames();
		bool editable = !IsActivePresetDefault;

		foreach ((OverlayElement element, string name) in visible) AddedWidgetsList.Children.Add(BuildAddedWidgetRow(element, name, editable));

		AddedWidgetsEmptyHint.IsVisible = visible.Count == 0;
	}

	/// <summary>
	///     A widget's name before any numbering: its palette name, and for a statistic what it counts - "Trip stat: Average speed" - so several of
	///     them are told apart by what they show instead of by "(2)" and "(3)".
	/// </summary>
	private string BaseWidgetName(OverlayElement element)
	{
		string name = GetWidgetLabel(element.Type);
		return element is TripStatElement stat ? $"{name}: {stat.Label ?? OverlayRenderer.DefaultTripStatLabel(stat.Stat)}" : name;
	}

	/// <summary>The widgets on the overlay in layout (draw) order, named as the "on overlay" list and the layers show them.</summary>
	private List<(OverlayElement Element, string Name)> VisibleWidgetNames()
	{
		List<OverlayElement> visible = [.. ActiveElements.Where(e => e.Visible)];

		string[] baseNames = [.. visible.Select(BaseWidgetName)];
		Dictionary<string, int> total = [];
		foreach (string name in baseNames) total[name] = total.GetValueOrDefault(name) + 1;

		Dictionary<string, int> seen = [];
		List<(OverlayElement, string)> named = [];
		for (int i = 0; i < visible.Count; i++)
		{
			int index = seen[baseNames[i]] = seen.GetValueOrDefault(baseNames[i]) + 1;
			named.Add((visible[i], baseNames[i] + (total[baseNames[i]] > 1 ? $" ({index})" : "")));
		}

		return named;
	}

	/// <summary>
	///     Clicking a row selects it (highlights it on the canvas); its gear opens the settings and its X removes it, as the
	///     canvas's do. A widget this file has no data for stays in the preset (it comes back with a file that has the data)
	///     but isn't drawn, so its row is dimmed, flagged with the warning icon and not selectable - only removable.
	/// </summary>
	private Border BuildAddedWidgetRow(OverlayElement element, string name, bool editable)
	{
		string id = element.Id;
		bool supported = IsTypeSupported(element.Type);
		var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

		void OpenSettings()
		{
			if (ActiveElements.FirstOrDefault(e => e.Id == id) is { } current) OpenElementSettings(current);
		}

		var text = new TextBlock { Text = name, Classes = { "overlayListItemText" }, Opacity = supported ? 1 : 0.5 };
		Grid.SetColumn(text, 0);
		grid.Children.Add(text);

		string? warningTip = !supported
			? UnsupportedReason(element.Type)
			: UsesTimeFallback(element.Type)
				? TimeFallbackTip
				: null;
		if (warningTip is not null)
		{
			var warning = new Border { Classes = { "fallbackWarning" }, Child = new IconView { Data = Icons.Warning } };
			ToolTip.SetTip(warning, warningTip);
			Grid.SetColumn(warning, 1);
			grid.Children.Add(warning);
		}

		if (editable)
		{
			var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 0, 0, 0) };
			if (supported)
			{
				var settingsButton = new Button { Content = new IconView { Data = Icons.Gear, Width = 12, Height = 12 }, Classes = { "addedWidgetAction" } };
				ToolTip.SetTip(settingsButton, Strings.Main_WidgetSettings);
				settingsButton.Click += (_, _) => OpenSettings();
				buttons.Children.Add(settingsButton);
			}

			var removeButton = new Button { Content = new IconView { Data = Icons.Close, Width = 10, Height = 10 }, Classes = { "addedWidgetAction", "remove" } };
			ToolTip.SetTip(removeButton, Strings.Main_RemoveFromOverlay);
			removeButton.Click += (_, _) => RemoveElementInstance(id);
			buttons.Children.Add(removeButton);

			Grid.SetColumn(buttons, 2);
			grid.Children.Add(buttons);
		}

		var row = new Border { Classes = { "addedWidgetRow" }, Child = grid };
		row.Classes.Set("selected", id == _selectedElementId);
		if (supported) row.PointerPressed += (_, _) => ToggleSelection(id);
		else row.Cursor = Cursor.Default;
		return row;
	}

	private void SelectElement(string? id)
	{
		if (id is not null && _musicActive)
		{
			_musicActive = false;
			MusicTracks.SelectedIndex = null;
		}

		if (_selectedElementId == id) return;

		_selectedElementId = id;
		RebuildAddedWidgetsList();
		RefreshSelectionHighlight();
	}

	/// <summary>A widget's drawing changes with the frame (a distance growing a digit) - the boxes framing it follow.</summary>
	private void RefreshWidgetFrames()
	{
		if (_selectedElementId is not null) RefreshSelectionHighlight();
		if (_hoveredElementId is { } id && ActiveElements.FirstOrDefault(e => e.Id == id) is { } hovered) ShowHoverIconsFor(hovered);
	}

	private void ToggleSelection(string id)
	{
		SelectElement(_selectedElementId == id ? null : id);
	}

	/// <summary>
	///     Draws (or hides) the blue box - and its bottom-right resize handle - around whichever element
	///     is selected via the "on overlay" list, kept in sync with drag/resize moves in
	///     OnOverlayCanvasPointerMoved and with list rebuilds here.
	/// </summary>
	private void RefreshSelectionHighlight()
	{
		LayerTracks.SelectedId = _selectedElementId;
		// The selection's solid box replaces the hover's dashed one on the same widget.
		HoverOutline.Classes.Set("shown", _hoveredElementId is not null && _hoveredElementId != _selectedElementId);
		// Nothing to frame for a widget that isn't drawn - no data for it, or its layer muted/unsoloed.
		if (_previewFullscreen || _selectedElementId is not { } id || ActiveElements.FirstOrDefault(e => e.Id == id) is not { Visible: true } el ||
		    !IsTypeSupported(el.Type) || OverlayLayers.Silenced(ActiveLayers).Contains(OverlayLayers.Key(el)))
		{
			SelectionHighlightBox.IsVisible = false;
			ResizeHandle.IsVisible = false;
			return;
		}

		SKRect bounds = GetElementBounds(el);
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
		ResizeHandle.IsVisible = !IsActivePresetDefault && !IsLayerLocked(el);
	}

	/// <summary>
	///     The box a widget takes at (x, y) and `scale`, in video pixels, for every hit-test, outline and snap in the editor:
	///     measured from what the preview's renderer draws for it at the frame shown (PreviewPlayer.MeasureElement - text
	///     above a bar, a value growing a digit), so it frames the widget as it is. Without a preview, or for a widget that
	///     draws nothing yet (an Image without a file), OverlayElementBounds' estimate for the type.
	/// </summary>
	private SKRect GetElementBounds(OverlayElement element)
	{
		(float x, float y) = FullResAnchor(element);
		return GetElementBounds(element, x, y, OverlayElementBounds.GetScale(_summary!.Video.Width, _summary.Video.Height) * element.Scale);
	}

	/// <summary>A widget's anchor in the video's own pixels (its X/Y are in the reference space - OverlayElementBounds.ToPixels), the space the editor works in.</summary>
	private (float X, float Y) FullResAnchor(OverlayElement element)
	{
		return OverlayElementBounds.ToPixels(element.X, element.Y, _summary!.Video.Width, _summary.Video.Height);
	}

	private SKRect GetElementBounds(OverlayElement element, float x, float y, float scale)
	{
		if (_previewPlayer.MeasureElement(element, _previewPosition) is { } drawn)
			return new SKRect(x + drawn.Left * scale, y + drawn.Top * scale, x + drawn.Right * scale, y + drawn.Bottom * scale);

		return OverlayElementBounds.GetBounds(element.Type, x, y, scale,
			(element as TimeTextElementBase)?.DateFormat, (element as TimeTextElementBase)?.Locale,
			(element as LabeledStatElement)?.Label ?? (element as ElapsedTimeTextElement)?.Label, _summary?.CameraModel, (element as StyledOverlayElement)?.FontFamily,
			(element as TextElement)?.Text, (element as ImageElement)?.ImagePath, (element as GpsAlertElement)?.Texts, element);
	}

	/// <summary>
	///     Swaps in a brand-new elements list rather than mutating the one already handed to
	///     PreviewPlayer - that list may be mid-enumeration in the compose thread's RenderOnto call
	///     right now, and mutating it in place races with that enumeration.
	/// </summary>
	/// <param name="layers">The preset's layers changed too - null keeps its own. Either way the two are brought in step (NormalizeActivePreset).</param>
	/// <param name="gestureKey">Set for the many small changes of one drag or typing run, so Undo takes them back together (EditHistory).</param>
	private void ReplaceActiveElements(List<OverlayElement> elements, IReadOnlyList<OverlayLayer>? layers = null, string? gestureKey = null)
	{
		StopCompare();
		NormalizeActivePreset(elements, layers);
		RefreshLayers();
		CommitHistory(gestureKey);
	}

	private void UpdateElement(string id, Func<OverlayElement, OverlayElement> update)
	{
		if (_suppressOverlayEvents || IsActivePresetDefault) return;

		List<OverlayElement> elements = [.. ActiveElements];
		int index = elements.FindIndex(el => el.Id == id);
		if (index < 0) return;

		elements[index] = update(elements[index]);
		ReplaceActiveElements(elements);
		ShowLayout();
		SaveOverlayPresets();
	}

	/// <summary>Units has no single shared abstract ancestor across the types that use it (unlike Label/DateFormat below), so this stays an explicit switch.</summary>
	private void SetElementUnits(string id, UnitSystem units)
	{
		UpdateElement(id, el => el switch
		{
			ElevationElement e => e with { Units = units },
			DistanceElement e => e with { Units = units },
			TemperatureElementBase e => e with { Units = units },
			DeviceDistanceElement e => e with { Units = units },
			HeightElement e => e with { Units = units },
			HomeDistanceElement e => e with { Units = units },
			VerticalSpeedElement e => e with { Units = units },
			DroneBatteryElement e => e with { Units = units },
			TripProgressBarElement e => e with { Units = units },
			ProfileChartElement e => e with { Units = units },
			TripStatElement e => e with { Units = units },
			_ => el
		});
	}

	private void SetElementLabel(string id, string? label)
	{
		string? trimmed = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
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

	/// <summary>
	///     Always creates a brand-new OverlayElement rather than reusing/revealing an existing one of the
	///     same type - dragging a widget from the palette repeatedly is how several instances of the same
	///     type end up on the canvas at once. Settings start at that type's factory defaults, the same
	///     baseline ResetElementToFactoryDefaults resets an existing instance back to.
	/// </summary>
	private void AddElementInstance(OverlayElementType type, float x, float y)
	{
		if (_summary is null || IsActivePresetDefault) return;

		var factory = OverlayPreset.CreateDefault("factory", "Factory");
		if (factory.Elements.FirstOrDefault(e => e.Type == type) is not { } template) return;

		(float refX, float refY) = OverlayElementBounds.ToReference(x, y, _summary.Video.Width, _summary.Video.Height);
		OverlayElement instance = template with { Id = Guid.NewGuid().ToString("N"), X = refX, Y = refY, Visible = true };

		List<OverlayElement> elements = [.. ActiveElements, instance];
		ReplaceActiveElements(elements);
		ShowLayout();
		SaveOverlayPresets();

		_selectedElementId = instance.Id;
		RefreshWidgetList();

		// An image widget draws nothing until a file is picked - straight to where it's picked.
		if (instance is ImageElement) OpenElementSettings(instance);
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
		ShowLayout();
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

		var factory = OverlayPreset.CreateDefault("factory", "Factory");
		if (factory.Elements.FirstOrDefault(e => e.Type == existing.Type) is not { } defaults) return;

		UpdateElement(id, el => defaults with { Id = el.Id, X = el.X, Y = el.Y, Visible = el.Visible, LayerId = el.LayerId });

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
			AnimationDurationSeconds = timing.AnimationDurationSeconds,
			OutAnimationType = timing.OutAnimationType,
			OutAnimationDurationSeconds = timing.OutAnimationDurationSeconds
		});
	}

	/// <summary>
	///     Populates one widget instance's settings panel and swaps the left column over to show it (in
	///     place of the SOURCE/ACTION/summary cards) - the single entry point for opening settings. Not a
	///     separate window: editing a widget needs the preview visible next to its settings. The widget whose settings are
	///     open is the selected one too - framed on the preview.
	/// </summary>
	private void OpenElementSettings(OverlayElement element)
	{
		if (!IsTypeSupported(element.Type)) return;

		SelectElement(element.Id);
		_editingElementId = element.Id;
		PopulateElementSettings(element);

		if (GetSettingsPanel(element.Type) is not { } panel) return;

		if (panel.Parent is Panel oldParent) oldParent.Children.Remove(panel);
		panel.IsVisible = true;
		WidgetSettingsPanelHost.Content = panel;
		WidgetSettingsTitleRun.Text = GetWidgetLabel(element.Type);

		CutsColumnScroll.IsVisible = false;
		UpdateCutsButton();
		MomentsColumnScroll.IsVisible = false;
		UpdateMomentsButton();
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
		SourceColumnScroll.IsVisible = !CutsColumnScroll.IsVisible && !MomentsColumnScroll.IsVisible;
	}

	/// <summary>
	///     Fills one widget type's settings panel controls from a specific instance's data - the
	///     counterpart to the type-specific field-changed handlers below, which write back to whichever
	///     instance's id is currently in _editingElementId.
	/// </summary>
	private void PopulateElementSettings(OverlayElement el)
	{
		_suppressOverlayEvents = true;

		foreach (ElementTimingEditor timing in _timingEditors) timing.VisibleFromSeconds = RouteIntroEnd;

		switch (el.Type)
		{
			case OverlayElementType.DateTimeText:
			{
				var x = (DateTimeTextElement)el;
				DateTimeFormatCombo.SelectedItem = DateFormatOptions.FirstOrDefault(o => o.Format == x.DateFormat) ?? DateFormatOptions[0];
				DateTimeLocaleCombo.SelectedItem = LocaleOptions.FirstOrDefault(o => o.CultureName == x.Locale) ?? LocaleOptions[0];
				DateTimeStyle.Populate(x);
				DateTimeTiming.Populate(x);
				DateTimeShadow.Populate(x);
				break;
			}

			case OverlayElementType.UtcTimeText:
			{
				var x = (UtcTimeTextElement)el;
				UtcTimeFormatCombo.SelectedItem = DateFormatOptions.FirstOrDefault(o => o.Format == x.DateFormat) ?? DateFormatOptions[0];
				UtcTimeLocaleCombo.SelectedItem = LocaleOptions.FirstOrDefault(o => o.CultureName == x.Locale) ?? LocaleOptions[0];
				UtcTimeStyle.Populate(x);
				UtcTimeTiming.Populate(x);
				UtcTimeShadow.Populate(x);
				break;
			}

			case OverlayElementType.Elevation:
			{
				var x = (ElevationElement)el;
				ElevationLabelBox.Text = x.Label;
				ElevationLabelBox.PlaceholderText = OverlayRenderer.DefaultLabel(x);
				SetUnitsRadio(ElevationMetricRadio, ElevationImperialRadio, x.Units);
				ElevationFromStartRadio.IsChecked = x.Reference == ElevationReference.Start;
				ElevationSeaLevelRadio.IsChecked = x.Reference == ElevationReference.SeaLevel;
				ElevationStyle.Populate(x);
				ElevationTiming.Populate(x);
				ElevationShadow.Populate(x);
				break;
			}

			case OverlayElementType.Gradient:
			{
				var x = (GradientElement)el;
				GradientLabelBox.Text = x.Label;
				GradientLabelBox.PlaceholderText = OverlayRenderer.DefaultLabel(x);
				GradientStyle.Populate(x);
				GradientTiming.Populate(x);
				GradientShadow.Populate(x);
				break;
			}

			case OverlayElementType.Distance:
			{
				var x = (DistanceElement)el;
				DistanceLabelBox.Text = x.Label;
				DistanceLabelBox.PlaceholderText = OverlayRenderer.DefaultLabel(x);
				SetUnitsRadio(DistanceMetricRadio, DistanceImperialRadio, x.Units);
				DistanceStyle.Populate(x);
				DistanceTiming.Populate(x);
				DistanceShadow.Populate(x);
				break;
			}

			case OverlayElementType.CameraInfo:
			{
				var x = (CameraInfoElement)el;
				CameraInfoLabelBox.Text = x.Label;
				CameraInfoLabelBox.PlaceholderText = OverlayRenderer.DefaultLabel(x);
				CameraInfoStyle.Populate(x);
				CameraInfoTiming.Populate(x);
				CameraInfoShadow.Populate(x);
				break;
			}

			case OverlayElementType.AmbientTemperature:
			case OverlayElementType.ImuTemperature:
			case OverlayElementType.DeviceDistance:
			case OverlayElementType.DeviceBattery:
			case OverlayElementType.AudioLevel:
			case OverlayElementType.WindProcessing:
			case OverlayElementType.ImageLuma:
			case OverlayElementType.Height:
			case OverlayElementType.HomeDistance:
			case OverlayElementType.VerticalSpeed:
			case OverlayElementType.Satellites:
			case OverlayElementType.FlightMode:
			{
				var x = (LabeledStatElement)el;
				SensorLabelBox.Text = x.Label;
				SensorLabelBox.PlaceholderText = OverlayRenderer.DefaultLabel(x);
				SensorUnitsPanel.IsVisible = el is TemperatureElementBase or DeviceDistanceElement or HeightElement or HomeDistanceElement or VerticalSpeedElement;
				SensorMetricRadio.Content = el is TemperatureElementBase ? "°C" : Strings.Common_Metric;
				SensorImperialRadio.Content = el is TemperatureElementBase ? "°F" : Strings.Common_Imperial;
				UnitSystem units = el switch
				{
					TemperatureElementBase t => t.Units, DeviceDistanceElement d => d.Units, HeightElement h => h.Units, HomeDistanceElement a => a.Units,
					VerticalSpeedElement v => v.Units, _ => UnitSystem.Metric
				};
				SetUnitsRadio(SensorMetricRadio, SensorImperialRadio, units);
				SensorBarsPanel.IsVisible = el is SatellitesElement;
				SensorLimitsGroup.IsVisible = el is RangeLimitedElement;
				SensorGpsGroup.IsVisible = el is SatellitesElement;
				SensorModeGroup.IsVisible = el is FlightModeElement;
				SensorClimbGroup.IsVisible = el is VerticalSpeedElement;
				SensorStateColorsGroup.IsVisible = el is ThresholdStatElement;
				switch (el)
				{
					case RangeLimitedElement limited:
						SensorWarnAboveBox.Value = (decimal?)limited.WarnAboveMeters;
						SensorAlertAboveBox.Value = (decimal?)limited.AlertAboveMeters;
						break;
					case SatellitesElement sats:
						SensorColorByCountCheck.IsChecked = sats.ColorByCount;
						SensorWarnBelowBox.Value = sats.WarnBelow;
						SensorAlertBelowBox.Value = sats.AlertBelow;
						break;
					case FlightModeElement mode:
						SensorColorByModeCheck.IsChecked = mode.ColorByMode;
						break;
					case VerticalSpeedElement vertical:
						SensorClimbColorBox.Text = vertical.ClimbColor;
						SensorSinkColorBox.Text = vertical.SinkColor;
						break;
				}

				if (el is ThresholdStatElement threshold)
				{
					SensorWarnColorBox.Text = threshold.WarnColor;
					SensorAlertColorBox.Text = threshold.AlertColor;
				}

				if (el is SatellitesElement gps) SatellitesBarsCheck.IsChecked = gps.ShowBars;
				SensorDescription.Text = el switch
				{
					AmbientTemperatureElement => Strings.Widget_AmbientTemperatureDescription,
					ImuTemperatureElement => Strings.Widget_ImuTemperatureDescription,
					DeviceDistanceElement => Strings.Widget_DeviceDistanceDescription,
					AudioLevelElement => Strings.Widget_AudioLevelDescription,
					WindProcessingElement => Strings.Widget_WindProcessingDescription,
					ImageLumaElement => Strings.Widget_ImageLumaDescription,
					HeightElement => Strings.Widget_HeightDescription,
					HomeDistanceElement => Strings.Widget_HomeDistanceDescription,
					VerticalSpeedElement => Strings.Widget_VerticalSpeedDescription,
					SatellitesElement => Strings.Widget_SatellitesDescription,
					FlightModeElement => Strings.Widget_FlightModeDescription,
					_ => Strings.Widget_DeviceBatteryDescription
				};
				SensorStyle.Populate(x);
				SensorTiming.Populate(x);
				SensorShadow.Populate(x);
				break;
			}

			case OverlayElementType.Compass:
			{
				var x = (CompassElement)el;
				CompassCenterCheck.IsChecked = x.CenterOnPosition;
				CompassRotateCheck.IsChecked = x.RotateWithHeading;
				CompassNorthCheck.IsChecked = x.ShowNorthLabel;
				CompassHeadingTextCheck.IsChecked = x.ShowHeadingText;
				CompassTrail.Populate(x);
				CompassMarker.Populate(x);
				CompassPanelFill.Populate(x);
				CompassTiming.Populate(x);
				CompassShadow.Populate(x);
				break;
			}

			case OverlayElementType.SunWidget:
			{
				var x = (SunWidgetElement)el;
				SunStyle.Populate(x);
				SunPanelFill.Populate(x);
				SunTiming.Populate(x);
				SunShadow.Populate(x);
				break;
			}

			case OverlayElementType.RollGauge:
			{
				var x = (RollGaugeElement)el;
				RollStyle.Populate(x);
				RollPanelFill.Populate(x);
				RollTiming.Populate(x);
				RollShadow.Populate(x);
				break;
			}

			case OverlayElementType.PitchGauge:
			{
				var x = (PitchGaugeElement)el;
				PitchStyle.Populate(x);
				PitchPanelFill.Populate(x);
				PitchTiming.Populate(x);
				PitchShadow.Populate(x);
				break;
			}

			case OverlayElementType.RcSticks:
			{
				var x = (RcSticksElement)el;
				RcSticksStyle.Populate(x);
				RcSticksPanelFill.Populate(x);
				RcSticksTiming.Populate(x);
				RcSticksShadow.Populate(x);
				break;
			}

			case OverlayElementType.AttitudeIndicator:
			{
				var x = (AttitudeIndicatorElement)el;
				AttitudeStyle.Populate(x);
				AttitudePanelFill.Populate(x);
				AttitudeTiming.Populate(x);
				AttitudeShadow.Populate(x);
				break;
			}

			case OverlayElementType.Orientation3D:
			{
				var x = (Orientation3DElement)el;
				Orientation3DStyle.Populate(x);
				Orientation3DPanelFill.Populate(x);
				Orientation3DTiming.Populate(x);
				Orientation3DShadow.Populate(x);
				break;
			}

			case OverlayElementType.GMeter:
			{
				var x = (GMeterElement)el;
				GMeterFullScaleBox.Value = (decimal)x.GMeterFullScaleG;
				GMeterStyle.Populate(x);
				GMeterPanelFill.Populate(x);
				GMeterTiming.Populate(x);
				GMeterShadow.Populate(x);
				break;
			}

			case OverlayElementType.SpeedGauge:
			{
				var x = (SpeedGaugeElement)el;
				SetUnitsRadio(SpeedMetricRadio, SpeedImperialRadio, x.Units);
				SpeedThemeDefaultRadio.IsChecked = x.Theme == SpeedGaugeTheme.Default;
				SpeedThemeLegacyRadio.IsChecked = x.Theme == SpeedGaugeTheme.Legacy;
				SpeedThemeRingRadio.IsChecked = x.Theme == SpeedGaugeTheme.Ring;
				SpeedPanelGroup.IsVisible = x.Theme != SpeedGaugeTheme.Ring;
				SpeedMaxBox.Value = (decimal?)x.MaxSpeed;
				UpdateSpeedMaxPlaceholder(x.Units);
				SpeedStyle.Populate(x);
				SpeedPanelFill.Populate(x);
				SpeedTiming.Populate(x);
				SpeedShadow.Populate(x);
				break;
			}

			case OverlayElementType.MapWidget:
			{
				var x = (MapWidgetElement)el;
				MapSourceEditor.Load(x.MapProviderId, OverlaySettingsStore.Load());
				MapZoomBox.Value = x.MapZoom;
				MapDynamicZoomCheck.IsChecked = x.MapDynamicZoom;
				MapZoomOutMaxBox.Value = (decimal)x.MapDynamicZoomMaxFactor;
				MapZoomOutMaxLabel.IsVisible = x.MapDynamicZoom;
				MapZoomOutMaxBox.IsVisible = x.MapDynamicZoom;
				MapZoomOutMaxHint.IsVisible = x.MapDynamicZoom;
				MapRotateCheck.IsChecked = x.RotateWithHeading;
				MapTrail.Populate(x);
				MapMarker.Populate(x);
				MapTiming.Populate(x);
				MapShadow.Populate(x);
				break;
			}

			case OverlayElementType.ElapsedTimeText:
			{
				var x = (ElapsedTimeTextElement)el;
				ElapsedTimeLabelBox.Text = x.Label;
				ElapsedTimeStyle.Populate(x);
				ElapsedTimeTiming.Populate(x);
				ElapsedTimeShadow.Populate(x);
				break;
			}

			case OverlayElementType.CameraModelText:
			{
				var x = (CameraModelTextElement)el;
				CameraModelStyle.Populate(x);
				CameraModelTiming.Populate(x);
				CameraModelShadow.Populate(x);
				break;
			}

			case OverlayElementType.MapAttribution:
			{
				var x = (MapAttributionElement)el;
				MapCreditStyle.Populate(x);
				MapCreditTiming.Populate(x);
				MapCreditShadow.Populate(x);
				UpdateMapCreditPanel();
				break;
			}

			case OverlayElementType.DroneBattery:
			{
				var x = (DroneBatteryElement)el;
				DroneBatteryLabelBox.Text = x.Label;
				DroneBatteryLabelBox.PlaceholderText = OverlayRenderer.DefaultLabel(x);
				DroneBatteryIconCheck.IsChecked = x.ShowIcon;
				DroneBatteryColorCheck.IsChecked = x.ColorByCharge;
				DroneBatteryVoltageCheck.IsChecked = x.ShowVoltage;
				DroneBatteryTemperatureCheck.IsChecked = x.ShowTemperature;
				DroneBatteryCurrentCheck.IsChecked = x.ShowCurrent;
				DroneBatteryCellsCheck.IsChecked = x.ShowCells;
				SetUnitsRadio(DroneBatteryMetricRadio, DroneBatteryImperialRadio, x.Units);
				DroneBatteryLowBox.Value = x.LowPercent;
				DroneBatteryCriticalBox.Value = x.CriticalPercent;
				DroneBatteryLowColorBox.Text = x.LowColor;
				DroneBatteryCriticalColorBox.Text = x.CriticalColor;
				DroneBatteryLowCellBox.Value = (decimal)x.LowCellVolts;
				DroneBatteryCriticalCellBox.Value = (decimal)x.CriticalCellVolts;
				DroneBatteryColdWarnBox.Value = (decimal)x.ColdCautionCelsius;
				DroneBatteryColdCriticalBox.Value = (decimal)x.ColdCriticalCelsius;
				DroneBatteryHotWarnBox.Value = (decimal)x.HotCautionCelsius;
				DroneBatteryHotCriticalBox.Value = (decimal)x.HotCriticalCelsius;
				DroneBatteryStyle.Populate(x);
				DroneBatteryTiming.Populate(x);
				DroneBatteryShadow.Populate(x);
				break;
			}

			case OverlayElementType.Signal:
			{
				var x = (SignalElement)el;
				SignalLabelBox.Text = x.Label;
				SignalLabelBox.PlaceholderText = OverlayRenderer.DefaultLabel(x);
				SignalUplinkCheck.IsChecked = x.ShowUplink;
				SignalDownlinkCheck.IsChecked = x.ShowDownlink;
				SignalBarsCheck.IsChecked = x.ShowBars;
				SignalColorCheck.IsChecked = x.ColorByLevel;
				SignalGoodBox.Value = x.GoodPercent;
				SignalFairBox.Value = x.FairPercent;
				SignalFairColorBox.Text = x.FairColor;
				SignalPoorColorBox.Text = x.PoorColor;
				SignalStyle.Populate(x);
				SignalTiming.Populate(x);
				SignalShadow.Populate(x);
				break;
			}

			case OverlayElementType.FlightAlerts:
			{
				var x = (FlightAlertsElement)el;
				FlightAlertsModesCheck.IsChecked = x.ShowFlightModes;
				FlightAlertsMotorsCheck.IsChecked = x.ShowMotors;
				FlightAlertsBatteryCheck.IsChecked = x.ShowBattery;
				FlightAlertsWarningsCheck.IsChecked = x.ShowWarnings;
				FlightAlertsNoticesCheck.IsChecked = x.ShowTips;
				FlightAlertsSecondsBox.Value = (decimal)x.ShowSeconds;
				FlightAlertsLinesBox.Value = x.MaxLines;
				FlightAlertsFadeBox.Value = (decimal)x.FadeSeconds;
				FlightAlertsSlideCheck.IsChecked = x.SlideIn;
				FlightAlertsStyle.Populate(x);
				FlightAlertsPanelFill.Populate(x);
				FlightAlertsTiming.Populate(x);
				FlightAlertsShadow.Populate(x);
				break;
			}

			case OverlayElementType.GpsAlert:
			{
				var x = (GpsAlertElement)el;
				GpsAlertMessageCombo.SelectedItem ??= GpsAlertMessageOptions[0];
				ShowGpsAlertText(x);
				GpsAlertStyle.Populate(x);
				GpsAlertPanelFill.Populate(x);
				GpsAlertShadow.Populate(x);
				GpsAlertTiming.Populate(x);
				break;
			}

			case OverlayElementType.Watermark:
			{
				var x = (WatermarkElement)el;
				WatermarkStyle.Populate(x);
				WatermarkTiming.Populate(x, _previewPlayer.IntroShiftSeconds(x));
				WatermarkShadow.Populate(x);
				WatermarkOffHint.IsVisible = !_showWatermark;
				WatermarkIntroHint.IsVisible = _previewPlayer.IntroShiftSeconds(x) > 0;
				break;
			}

			case OverlayElementType.TripProgressBar:
			{
				var x = (TripProgressBarElement)el;
				SetUnitsRadio(TripProgressMetricRadio, TripProgressImperialRadio, x.Units);
				TripProgressToleranceBox.Value = (decimal)x.TripArrivedToleranceMeters;
				TripProgressLabelBox.Text = x.TripArrivedLabel;
				TripProgressLabelBox.PlaceholderText = OverlayRenderer.DefaultTripArrivedLabel;
				TripProgressSpeedGateCheck.IsChecked = x.ShowFromSpeedKmh is not null;
				TripProgressSpeedGateBox.Value = (decimal)(x.ShowFromSpeedKmh ?? OverlayRenderer.TripProgressSpeedGateDefaultKmh);
				TripProgressSpeedGateBox.IsEnabled = x.ShowFromSpeedKmh is not null;
				TripProgressHideCheck.IsChecked = x.HideAfterArrivalSeconds is not null;
				TripProgressHideBox.Value = (decimal)(x.HideAfterArrivalSeconds ?? OverlayRenderer.TripProgressHideAfterArrivalDefaultSeconds);
				TripProgressHideBox.IsEnabled = x.HideAfterArrivalSeconds is not null;
				TripProgressBarPanelFill.Populate(x);
				TripProgressBarTiming.Populate(x, _previewPlayer.IntroShiftSeconds(x));
				LockTripProgressTiming(x);
				TripProgressIntroHint.IsVisible = _previewPlayer.IntroShiftSeconds(x) > 0;
				TripProgressBarShadow.Populate(x);
				break;
			}

			case OverlayElementType.ProfileChart:
			{
				var x = (ProfileChartElement)el;
				ProfileElevationRadio.IsChecked = x.Series == ProfileSeries.Elevation;
				ProfileSpeedRadio.IsChecked = x.Series == ProfileSeries.Speed;
				ProfileHeightRadio.IsChecked = x.Series == ProfileSeries.Height;
				ProfileDistanceRadio.IsChecked = x.Axis == ProfileAxis.Distance;
				ProfileTimeRadio.IsChecked = x.Axis == ProfileAxis.Time;
				ProfileLabelBox.Text = x.Label;
				ProfileLabelBox.PlaceholderText = OverlayRenderer.DefaultLabel(x);
				SetUnitsRadio(ProfileMetricRadio, ProfileImperialRadio, x.Units);
				ProfileAutoHeightCheck.IsChecked = x.AutoHeight;
				ProfileMarkerCheck.IsChecked = x.ShowMarker;
				ProfileMinMaxCheck.IsChecked = x.ShowMinMax;
				ProfileLineWidthBox.Value = (decimal)x.LineWidth;
				ProfileChartStyle.Populate(x);
				ProfileChartPanelFill.Populate(x);
				ProfileChartTiming.Populate(x);
				ProfileChartShadow.Populate(x);
				break;
			}

			case OverlayElementType.TripStat:
			{
				var x = (TripStatElement)el;
				TripStatCombo.SelectedItem = TripStatOptions.First(o => o.Stat == x.Stat);
				TripStatLabelBox.Text = x.Label;
				TripStatLabelBox.PlaceholderText = OverlayRenderer.DefaultLabel(x);
				SetUnitsRadio(TripStatMetricRadio, TripStatImperialRadio, x.Units);
				TripStatStyle.Populate(x);
				TripStatTiming.Populate(x);
				TripStatShadow.Populate(x);
				break;
			}

			case OverlayElementType.Text:
			{
				var x = (TextElement)el;
				TextContentBox.Text = x.Text;
				TextStyle.Populate(x);
				TextTiming.Populate(x);
				TextShadow.Populate(x);
				break;
			}

			case OverlayElementType.Image:
			{
				var x = (ImageElement)el;
				ShowImagePath(x.ImagePath);
				ImageOpacityBox.Value = (decimal)Math.Round(x.Opacity * 100);
				ImageTiming.Populate(x);
				ImageShadow.Populate(x);
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

	/// <summary>
	///     Shared by every widget's ElementStyleEditor - writes to whichever instance's settings panel is open.
	///     AccentColor only exists on LabeledStatElement, so it's only written there.
	/// </summary>
	private void OnElementStyleChanged(ElementStyle style)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id) return;

		UpdateElement(id, el => el switch
		{
			LabeledStatElement stat => ApplyStyle(stat) with
			{
				AccentColor = style.AccentColor,
				Decimals = OverlayElementBounds.SupportsDecimals(stat) ? style.Decimals : stat.Decimals,
				ShowLabel = OverlayElementBounds.SupportsStatParts(stat) ? style.ShowLabel : stat.ShowLabel,
				ShowUnit = OverlayElementBounds.SupportsStatParts(stat) ? style.ShowUnit : stat.ShowUnit,
				UppercaseLabel = OverlayElementBounds.SupportsStatParts(stat) ? style.UppercaseLabel : stat.UppercaseLabel
			},
			StyledOverlayElement styled => ApplyStyle(styled),
			_ => el
		});

		T ApplyStyle<T>(T element) where T : StyledOverlayElement
		{
			return element with
			{
				FontFamily = style.FontFamily,
				Scale = style.Scale is { } scale ? OverlayElementBounds.ClampScale(element, scale) : element.Scale,
				TextColor = style.TextColor,
				OutlineColor = style.OutlineColor,
				OutlineWidth = style.OutlineWidth ?? element.OutlineWidth,
				TextAlign = OverlayElementBounds.SupportsTextAlign(element) ? style.Align : element.TextAlign,
				Opacity = style.Opacity ?? element.Opacity
			};
		}
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

	private sealed record GpsAlertMessageOption(string Display, GpsAlertMessage Message)
	{
		public override string ToString()
		{
			return Display;
		}
	}

	private sealed record TripStatOption(string Display, TripStatKind Stat)
	{
		public override string ToString()
		{
			return Display;
		}
	}
}
