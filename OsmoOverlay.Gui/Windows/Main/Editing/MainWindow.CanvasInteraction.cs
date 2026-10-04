using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OsmoOverlay.Core.Overlay;
using SkiaSharp;

namespace OsmoOverlay.Gui;

/// <summary>
///     Working with widgets on the preview canvas: finding the one under the pointer, dragging from the palette onto the
///     canvas, dragging, resizing and hovering placed widgets (and the gear/remove buttons that appear on hover).
/// </summary>
public partial class MainWindow
{
	/// <summary>
	///     Topmost drawn element whose bounds contain `pos`, or null - shared by the drag hit-test and the hover
	///     state. Skips widgets this file has no data for: the preview doesn't draw them (ApplyAvailability),
	///     so they mustn't be draggable or open settings from an empty spot on the canvas.
	/// </summary>
	private OverlayElement? FindElementAt(Point pos)
	{
		if (_summary is null) return null;

		List<OverlayElement> elements = ActiveElements;
		// A muted (or unsoloed) layer's widgets aren't drawn, so they can't be picked either.
		HashSet<string> silenced = OverlayLayers.Silenced(ActiveLayers);
		for (int i = elements.Count - 1; i >= 0; i--)
		{
			OverlayElement el = elements[i];
			if (!el.Visible || !IsTypeSupported(el.Type) || silenced.Contains(OverlayLayers.Key(el))) continue;

			SKRect bounds = GetElementBounds(el);
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
			OverlayElementType.RollGauge => RollSettingsPanel,
			OverlayElementType.PitchGauge => PitchSettingsPanel,
			OverlayElementType.GMeter => GMeterSettingsPanel,
			OverlayElementType.ElapsedTimeText => ElapsedTimeSettingsPanel,
			OverlayElementType.CameraModelText => CameraModelSettingsPanel,
			OverlayElementType.SpeedGauge => SpeedSettingsPanel,
			OverlayElementType.MapWidget => MapSettingsPanel,
			OverlayElementType.TripProgressBar => TripProgressBarSettingsPanel,
			OverlayElementType.ProfileChart => ProfileChartSettingsPanel,
			OverlayElementType.TripStat => TripStatSettingsPanel,
			OverlayElementType.Text => TextSettingsPanel,
			OverlayElementType.Image => ImageSettingsPanel,
			OverlayElementType.MapAttribution => MapCreditSettingsPanel,
			_ => null
		};
	}

	/// <summary>
	///     Starts an OS-level drag from a palette row - the counterpart to OnOverlayCanvasDrop,
	///     which turns the drop into a brand-new widget instance (see AddElementInstance). Only once the pointer
	///     has moved past the system drag threshold with the left button held: a bare click must not open a drag
	///     session, whose ghost image can be left on screen.
	/// </summary>
	private void OnWidgetItemPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Border { Tag: OverlayElementType type } item || !item.IsEnabled) return;
		if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;

		_widgetDragStart = (type, e);
	}

	private async void OnWidgetItemPointerMoved(object? sender, PointerEventArgs e)
	{
		if (_widgetDragStart is not var (type, pressed) || sender is not Border item) return;
		if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
		{
			_widgetDragStart = null;
			return;
		}

		Point delta = e.GetPosition(item) - pressed.GetPosition(item);
		if (Math.Abs(delta.X) < DragDropThreshold && Math.Abs(delta.Y) < DragDropThreshold) return;

		_widgetDragStart = null;
		var data = new DataTransfer();
		data.Add(DataTransferItem.Create(WidgetDragFormat, type.ToString()));
		await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move);
	}

	private void OnWidgetItemPointerEnded(object? sender, RoutedEventArgs e)
	{
		_widgetDragStart = null;
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

		float x = (float)Math.Clamp(pos.X, 0, _summary!.Video.Width);
		float y = (float)Math.Clamp(pos.Y, 0, _summary.Video.Height);
		AddElementInstance(type, x, y);
	}

	private void OnOverlayCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (TryStartPreviewPan(e) || TryStartReframeDrag(e)) return;
		if (_summary is null || !e.GetCurrentPoint(OverlayDragCanvas).Properties.IsLeftButtonPressed) return;
		if (MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas)) is not { } pos) return;

		// A click on the video itself lets go of the selection.
		if (FindElementAt(pos) is not { } el)
		{
			SelectElement(null);
			return;
		}

		// The built-in preset's widgets, and a locked layer's, can be picked (to see and open their settings), not moved.
		if (IsActivePresetDefault || IsLayerLocked(el))
		{
			SelectElement(el.Id);
			return;
		}

		// Dragging needs a stable frame to align against, and it eliminates a real race:
		// without pausing, the compose thread keeps calling RenderOnto on the same elements
		// list this drag is about to replace concurrently.
		if (_previewPlayer.IsPlaying)
		{
			_previewPlayer.Pause();
			ShowPlayingState(false);
		}

		_draggingElementId = el.Id;
		(float anchorX, float anchorY) = FullResAnchor(el);
		_dragAnchorOffset = new Point(pos.X - anchorX, pos.Y - anchorY);
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
		if (ContinuePreviewPan(e) || ContinueReframeDrag(e)) return;
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

		float newX = (float)Math.Clamp(pos.X - _dragAnchorOffset.X, 0, _summary.Video.Width);
		float newY = (float)Math.Clamp(pos.Y - _dragAnchorOffset.Y, 0, _summary.Video.Height);

		List<OverlayElement> elements = [.. ActiveElements];
		int index = elements.FindIndex(el => el.Id == id);
		if (index < 0) return;

		(newX, newY) = SnapToGuides(elements[index], newX, newY);

		(float refX, float refY) = OverlayElementBounds.ToReference(newX, newY, _summary.Video.Width, _summary.Video.Height);
		elements[index] = elements[index] with { X = refX, Y = refY };
		ReplaceActiveElements(elements, null, "drag:" + id);
		ShowLayout();
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
		(float anchorX, float anchorY) = FullResAnchor(el);
		_resizeStartDistance = Math.Max(Point.Distance(pos, new Point(anchorX, anchorY)), 1);
		e.Pointer.Capture(OverlayDragCanvas);
		e.Handled = true;
		HideHoverIcons();
	}

	private void UpdateResize(string id, PointerEventArgs e)
	{
		if (_summary is null) return;
		if (MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas)) is not { } pos) return;

		List<OverlayElement> elements = [.. ActiveElements];
		int index = elements.FindIndex(el => el.Id == id);
		if (index < 0) return;

		OverlayElement el = elements[index];
		(float anchorX, float anchorY) = FullResAnchor(el);
		double distance = Point.Distance(pos, new Point(anchorX, anchorY));
		float newScale = (float)Math.Clamp(_resizeStartScale * (distance / _resizeStartDistance), OverlayElementBounds.MinElementScale, OverlayElementBounds.MaxElementScale);

		elements[index] = el with { Scale = newScale };
		ReplaceActiveElements(elements, null, "resize:" + id);
		ShowLayout();
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
		_reframeDragStart = null;
		if (_resizingElementId is null && _draggingElementId is null) return;

		_resizingElementId = null;
		_draggingElementId = null;
		OverlayDragCanvas.Cursor = null;
		SealHistory();
		SaveOverlayPresets();
	}

	private void OnOverlayCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (EndPreviewPan(e) || EndReframeDrag(e)) return;
		if (_resizingElementId is not null)
		{
			_resizingElementId = null;
			e.Pointer.Capture(null);
			UpdateHoverState(e);
			SealHistory();
			SaveOverlayPresets();
			return;
		}

		if (_draggingElementId is null) return;

		_draggingElementId = null;
		e.Pointer.Capture(null);
		UpdateHoverState(e);
		SealHistory();
		SaveOverlayPresets();
	}

	/// <summary>
	///     The widget under the pointer gets its outline and name - also on the read-only built-in preset, to see what's
	///     where - and, where it can be edited, the settings/remove buttons.
	/// </summary>
	private void UpdateHoverState(PointerEventArgs e)
	{
		if (_summary is null)
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
		OverlayDragCanvas.Cursor = hovered is not null && !IsActivePresetDefault ? HandCursor : null;

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

	/// <summary>
	///     Frames the hovered widget: a dashed outline round its box (left to the selection's solid one when it's the
	///     selected widget), its name above the top-left corner (below the box when there's no room above), and the
	///     gear/remove pair on the top-right corner, gear to the left of remove.
	/// </summary>
	private void ShowHoverIconsFor(OverlayElement element)
	{
		SKRect bounds = GetElementBounds(element);
		if (MapFullResPointToCanvas(bounds.Left, bounds.Top) is not { } topLeft ||
		    MapFullResPointToCanvas(bounds.Right, bounds.Bottom) is not { } bottomRight)
		{
			HideHoverIcons();
			return;
		}

		if (_hoveredElementId != element.Id)
		{
			_hoveredElementId = element.Id;
			HoverLabelText.Text = VisibleWidgetNames().FirstOrDefault(w => w.Element.Id == element.Id).Name ?? GetWidgetLabel(element.Type);
		}

		double width = Math.Max(0, bottomRight.X - topLeft.X);
		Canvas.SetLeft(HoverOutline, topLeft.X);
		Canvas.SetTop(HoverOutline, topLeft.Y);
		HoverOutline.Width = width;
		HoverOutline.Height = Math.Max(0, bottomRight.Y - topLeft.Y);
		HoverOutline.Classes.Set("shown", element.Id != _selectedElementId);

		const double labelGap = 4;
		bool editable = !IsActivePresetDefault;
		// Clear of the buttons on the right corner.
		HoverLabel.MaxWidth = Math.Max(60, width - (editable ? RemoveWidgetButton.Width + WidgetGearHoverButton.Width : 0));
		double labelTop = topLeft.Y - HoverLabel.Height - labelGap;
		Canvas.SetLeft(HoverLabel, topLeft.X);
		Canvas.SetTop(HoverLabel, labelTop >= 0 ? labelTop : bottomRight.Y + labelGap);
		HoverLabel.Classes.Set("shown", true);

		RemoveWidgetButton.IsVisible = editable;
		WidgetGearHoverButton.IsVisible = editable;
		if (!editable) return;

		const double gap = 4;
		Canvas.SetLeft(RemoveWidgetButton, bottomRight.X - RemoveWidgetButton.Width / 2);
		Canvas.SetTop(RemoveWidgetButton, topLeft.Y - RemoveWidgetButton.Height / 2);
		Canvas.SetLeft(WidgetGearHoverButton, bottomRight.X - RemoveWidgetButton.Width / 2 - gap - WidgetGearHoverButton.Width);
		Canvas.SetTop(WidgetGearHoverButton, topLeft.Y - WidgetGearHoverButton.Height / 2);
	}

	private void HideHoverIcons()
	{
		_hoveredElementId = null;
		HoverOutline.Classes.Set("shown", false);
		HoverLabel.Classes.Set("shown", false);
		RemoveWidgetButton.IsVisible = false;
		WidgetGearHoverButton.IsVisible = false;
	}

	/// <summary>The gear/remove buttons are the canvas's own children, so moving onto them doesn't count as leaving.</summary>
	private void OnOverlayCanvasPointerExited(object? sender, PointerEventArgs e)
	{
		if (_draggingElementId is null && _resizingElementId is null) HideHoverIcons();
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
}
