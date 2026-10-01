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

	private static readonly List<TripStatOption> TripStatOptions =
	[
		new("Max speed", TripStatKind.MaxSpeed),
		new("Average speed (moving)", TripStatKind.AverageSpeed),
		new("Elevation gain", TripStatKind.ElevationGain),
		new("Elevation loss", TripStatKind.ElevationLoss),
		new("Moving time", TripStatKind.MovingTime)
	];

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
	///     The built-in "Default" preset is read-only, so there's always one untouched baseline layout
	///     to fall back to or Duplicate from - editing it directly would mean losing that baseline the
	///     first time someone drags a widget.
	/// </summary>
	private bool IsActivePresetDefault => _activePresetId == OverlayPresetStore.DefaultPresetId;

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

	// Shown whenever the widget is actually usable but only through the container-time fallback
	// (see OverlayRenderer.DrawTimeText) - not real GPS-recorded time, so a driving log synced
	// against this against other GPS-timestamped data could be off by however stale the camera's
	// own clock is.
	private const string TimeFallbackTip = "This file has no GPS time, so the recording start time from the camera's own clock is used instead. It is not GPS-synced.";

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
				"Unavailable: this file has no GPS time and no recording start time.",
			OverlayElementType.SunWidget => "Unavailable: the sun's position needs a GPS fix and GPS time, and this file is missing one of them.",
			OverlayElementType.RollGauge or OverlayElementType.PitchGauge or OverlayElementType.GMeter =>
				"Unavailable: the axes of this camera's accelerometer are unknown, so tilt can't be calculated.",
			OverlayElementType.CameraInfo => "Unavailable: this file has no ISO or color temperature data.",
			_ => "Unavailable: this widget needs GPS, and this file has no GPS fix."
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

	/// <summary>The widgets on the overlay in layout (draw) order, named as the "on overlay" list and the layers show them.</summary>
	private List<(OverlayElement Element, string Name)> VisibleWidgetNames()
	{
		List<OverlayElement> visible = [.. ActiveElements.Where(e => e.Visible)];

		Dictionary<OverlayElementType, int> totalByType = [];
		foreach (OverlayElement el in visible) totalByType[el.Type] = totalByType.GetValueOrDefault(el.Type) + 1;

		Dictionary<OverlayElementType, int> seenByType = [];
		List<(OverlayElement, string)> named = [];
		foreach (OverlayElement el in visible)
		{
			int index = seenByType[el.Type] = seenByType.GetValueOrDefault(el.Type) + 1;
			named.Add((el, GetWidgetLabel(el.Type) + (totalByType[el.Type] > 1 ? $" ({index})" : "")));
		}

		return named;
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
		string id = element.Id;
		bool supported = IsTypeSupported(element.Type);
		var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

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

	private void SelectElement(string? id)
	{
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
			(element as TextElement)?.Text, (element as ImageElement)?.ImagePath);
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
			SpeedGaugeElement e => e with { Units = units },
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
				ElevationFromStartRadio.IsChecked = x.Reference == ElevationReference.Start;
				ElevationSeaLevelRadio.IsChecked = x.Reference == ElevationReference.SeaLevel;
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
				CompassCenterCheck.IsChecked = x.CenterOnPosition;
				CompassRotateCheck.IsChecked = x.RotateWithHeading;
				CompassNorthCheck.IsChecked = x.ShowNorthLabel;
				CompassHeadingTextCheck.IsChecked = x.ShowHeadingText;
				CompassTrail.Populate(x);
				CompassMarker.Populate(x);
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

			case OverlayElementType.RollGauge:
			{
				var x = (RollGaugeElement)el;
				RollStyle.Populate(x);
				RollTiming.Populate(x);
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
				break;
			}

			case OverlayElementType.ElapsedTimeText:
			{
				var x = (ElapsedTimeTextElement)el;
				ElapsedTimeLabelBox.Text = x.Label;
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

			case OverlayElementType.ProfileChart:
			{
				var x = (ProfileChartElement)el;
				ProfileElevationRadio.IsChecked = x.Series == ProfileSeries.Elevation;
				ProfileSpeedRadio.IsChecked = x.Series == ProfileSeries.Speed;
				ProfileDistanceRadio.IsChecked = x.Axis == ProfileAxis.Distance;
				ProfileTimeRadio.IsChecked = x.Axis == ProfileAxis.Time;
				ProfileLabelBox.Text = x.Label;
				ProfileLabelBox.PlaceholderText = SentenceCase(OverlayRenderer.DefaultProfileLabel(x.Series));
				SetUnitsRadio(ProfileMetricRadio, ProfileImperialRadio, x.Units);
				ProfileChartStyle.Populate(x);
				ProfileChartTiming.Populate(x);
				break;
			}

			case OverlayElementType.TripStat:
			{
				var x = (TripStatElement)el;
				TripStatCombo.SelectedItem = TripStatOptions.First(o => o.Stat == x.Stat);
				TripStatLabelBox.Text = x.Label;
				TripStatLabelBox.PlaceholderText = SentenceCase(OverlayRenderer.DefaultTripStatLabel(x.Stat));
				SetUnitsRadio(TripStatMetricRadio, TripStatImperialRadio, x.Units);
				TripStatStyle.Populate(x);
				TripStatTiming.Populate(x);
				break;
			}

			case OverlayElementType.Text:
			{
				var x = (TextElement)el;
				TextContentBox.Text = x.Text;
				TextStyle.Populate(x);
				TextTiming.Populate(x);
				break;
			}

			case OverlayElementType.Image:
			{
				var x = (ImageElement)el;
				ShowImagePath(x.ImagePath);
				ImageOpacityBox.Value = (decimal)Math.Round(x.Opacity * 100);
				ImageTiming.Populate(x);
				break;
			}
		}

		_suppressOverlayEvents = false;
	}

	/// <summary>"ELEVATION GAIN" -> "Elevation gain", the way the other Label boxes show their built-in caption.</summary>
	private static string SentenceCase(string caption)
	{
		return caption.Length == 0 ? caption : caption[..1] + caption[1..].ToLowerInvariant();
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

	private sealed record TripStatOption(string Display, TripStatKind Stat)
	{
		public override string ToString()
		{
			return Display;
		}
	}
}
