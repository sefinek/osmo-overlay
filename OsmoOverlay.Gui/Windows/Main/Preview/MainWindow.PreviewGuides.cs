using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OsmoOverlay.Core.Overlay;
using SkiaSharp;

namespace OsmoOverlay.Gui;

/// <summary>
///     The preview canvas's geometry and guides: mapping canvas points to video pixels and back, the thirds/margin grid and
///     snapping a dragged widget to it.
/// </summary>
public partial class MainWindow
{
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
		if (_summary is null || _previewFrameSize is not { } frameSize) return null;

		double controlWidth = OverlayDragCanvas.Bounds.Width;
		double controlHeight = OverlayDragCanvas.Bounds.Height;
		int bitmapWidth = frameSize.Width;
		int bitmapHeight = frameSize.Height;
		if (controlWidth <= 0 || controlHeight <= 0 || bitmapWidth <= 0 || bitmapHeight <= 0) return null;

		// The preview bitmap is a uniformly downscaled copy of the full render resolution.
		double fullResScale = _summary.Video.Width / (double)bitmapWidth;
		double scale = _previewZoom is { } zoom
			? zoom * fullResScale / UiScale.DeviceScaling(this)
			: Math.Min(controlWidth / bitmapWidth, controlHeight / bitmapHeight);
		double renderedWidth = bitmapWidth * scale;
		double renderedHeight = bitmapHeight * scale;
		double offsetX = (controlWidth - renderedWidth) / 2 + _previewPan.X;
		double offsetY = (controlHeight - renderedHeight) / 2 + _previewPan.Y;

		return (scale, offsetX, offsetY, renderedWidth, renderedHeight, fullResScale);
	}

	private Point? MapCanvasPointToFullRes(Point canvasPoint)
	{
		if (GetPreviewTransform() is not { } t) return null;

		double localX = canvasPoint.X - t.OffsetX;
		double localY = canvasPoint.Y - t.OffsetY;
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

	/// <summary>
	///     Rule-of-thirds + safe-margin guide lines over the preview, editor-only - purely a positioning
	///     aid, never baked into the actual render (OverlayRenderer never draws these). The margin box
	///     uses the exact same OverlayElementBounds.Margin OverlayPreset.CreateDefault positions widgets
	///     within, so it visibly matches where widgets land by default.
	/// </summary>
	private void UpdatePreviewGuides()
	{
		bool showThirds = _gridMode is PreviewGridMode.Thirds or PreviewGridMode.Both;
		bool showMargin = _gridMode is PreviewGridMode.Margin or PreviewGridMode.Both;

		if (_previewFullscreen || (!showThirds && !showMargin) || GetPreviewTransform() is null || _summary is null)
		{
			PreviewGridVLine1.IsVisible = false;
			PreviewGridVLine2.IsVisible = false;
			PreviewGridHLine1.IsVisible = false;
			PreviewGridHLine2.IsVisible = false;
			PreviewSafeMarginBox.IsVisible = false;
			return;
		}

		float scale = OverlayElementBounds.GetScale(_summary.Video.Width, _summary.Video.Height);
		float margin = OverlayElementBounds.Margin * scale;
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
		double right = bottomRight.X;
		double bottom = bottomRight.Y;
		double width = right - left;
		double height = bottom - top;

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
		float scale = OverlayElementBounds.GetScale(_summary!.Video.Width, _summary.Video.Height);
		float margin = OverlayElementBounds.Margin * scale;
		float width = _summary.Video.Width - margin * 2;
		float height = _summary.Video.Height - margin * 2;

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

		float scale = OverlayElementBounds.GetScale(_summary.Video.Width, _summary.Video.Height);
		SKRect bounds = GetElementBounds(element, x, y, scale * element.Scale);
		(float[] xTargets, float[] yTargets) = GetGuideTargets();
		float threshold = (float)(SnapThresholdCanvasPixels * t.FullResScale / t.Scale);

		float[] xOffsets = bounds.IsEmpty ? [0f] : [bounds.Left - x, bounds.MidX - x, bounds.Right - x];
		float[] yOffsets = bounds.IsEmpty ? [0f] : [bounds.Top - y, bounds.MidY - y, bounds.Bottom - y];

		return (SnapAxis(x, xOffsets, xTargets, threshold), SnapAxis(y, yOffsets, yTargets, threshold));
	}

	private static float SnapAxis(float anchor, float[] edgeOffsets, float[] targets, float threshold)
	{
		float best = anchor;
		float bestDistance = threshold;
		foreach (float offset in edgeOffsets)
		{
			float edge = anchor + offset;
			foreach (float target in targets)
			{
				float distance = Math.Abs(edge - target);
				if (distance >= bestDistance) continue;

				bestDistance = distance;
				best = target - offset;
			}
		}

		return best;
	}
}
