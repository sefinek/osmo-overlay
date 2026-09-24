using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace OsmoOverlay.Gui;

/// <summary>
///     Preview zoom: Fit (the whole frame, the default) or a size relative to the recording's own pixels - 100% is one
///     source pixel per screen pixel (DPI scaling included), for judging the HUD as it will render; sharper than the
///     preview quality allows it can't get, so pixel-peeping wants "Full resolution". Ctrl+wheel zooms around the
///     pointer, the middle mouse button pans. GetPreviewTransform includes zoom and pan, so the image and everything
///     the editor places over it (selection box, handles, guides, hit-testing) follow the same mapping.
/// </summary>
public partial class MainWindow
{
	private const double MaxPreviewZoom = 8;
	private const double WheelZoomStep = 1.25;

	// Null = fit; the combo's presets in order.
	private static readonly double?[] PreviewZoomLevels = [null, 0.5, 1, 2];

	// Source pixels per device pixel; null = fit the frame into the view.
	private double? _previewZoom;
	// How far the image's middle sits from the view's middle, in DIPs - kept within what's needed to see its edges.
	private Vector _previewPan;
	private bool _suppressPreviewZoomEvent;
	private bool _panning;
	private Point _panStartPointer;
	private Vector _panStartOffset;

	private void WirePreviewZoom()
	{
		_suppressPreviewZoomEvent = true;
		PreviewZoomCombo.ItemsSource = PreviewZoomLevels.Select(z => z is { } level ? $"{level:P0}" : "Fit").ToList();
		PreviewZoomCombo.SelectedIndex = 0;
		_suppressPreviewZoomEvent = false;

		OverlayDragCanvas.PointerWheelChanged += OnPreviewPointerWheel;
	}

	private void OnPreviewZoomChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressPreviewZoomEvent || PreviewZoomCombo.SelectedIndex < 0) return;

		SetPreviewZoom(PreviewZoomLevels[PreviewZoomCombo.SelectedIndex], null);
	}

	private void OnPreviewPointerWheel(object? sender, PointerWheelEventArgs e)
	{
		if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || GetPreviewTransform() is not { } t) return;

		var current = t.Scale * RenderScaling / t.FullResScale;
		var zoom = current * Math.Pow(WheelZoomStep, e.Delta.Y);
		SetPreviewZoom(zoom <= FitZoom() ? null : Math.Min(zoom, MaxPreviewZoom), e.GetPosition(OverlayDragCanvas));
		e.Handled = true;
	}

	/// <summary>The zoom Fit shows at the view's current size - zooming out past it snaps back to Fit.</summary>
	private double FitZoom()
	{
		if (_summary is null || _previewBitmap is null) return 0;

		var bitmapWidth = _previewBitmap.PixelSize.Width;
		var fitScale = Math.Min(OverlayDragCanvas.Bounds.Width / bitmapWidth, OverlayDragCanvas.Bounds.Height / _previewBitmap.PixelSize.Height);
		return fitScale * RenderScaling * bitmapWidth / _summary.Video.Width;
	}

	/// <param name="anchor">The canvas point that stays over the same spot of the frame; null = the view's middle.</param>
	private void SetPreviewZoom(double? zoom, Point? anchor)
	{
		Point pivot = anchor ?? new Point(OverlayDragCanvas.Bounds.Width / 2, OverlayDragCanvas.Bounds.Height / 2);
		Point? under = MapCanvasPointToFullResUnclamped(pivot);

		_previewZoom = zoom;
		if (zoom is null) _previewPan = default;
		else if (under is { } fullRes && MapFullResPointToCanvas(fullRes.X, fullRes.Y) is { } moved)
			_previewPan += pivot - moved;

		_suppressPreviewZoomEvent = true;
		var preset = Array.IndexOf(PreviewZoomLevels, zoom);
		PreviewZoomCombo.SelectedIndex = preset;
		PreviewZoomCombo.PlaceholderText = preset < 0 && zoom is { } custom ? $"{custom:P0}" : null;
		_suppressPreviewZoomEvent = false;

		ApplyPreviewLayout();
	}

	/// <summary>Sizes and places the preview image per GetPreviewTransform, then everything the editor draws over it.</summary>
	private void ApplyPreviewLayout()
	{
		ClampPreviewPan();
		if (GetPreviewTransform() is { } t)
		{
			PreviewImage.Width = t.RenderedWidth;
			PreviewImage.Height = t.RenderedHeight;
			Canvas.SetLeft(PreviewImage, t.OffsetX);
			Canvas.SetTop(PreviewImage, t.OffsetY);
		}

		UpdatePreviewGuides();
		RefreshSelectionHighlight();
		WidgetGearHoverButton.IsVisible = false;
		RemoveWidgetButton.IsVisible = false;
	}

	private void ClampPreviewPan()
	{
		Vector pan = _previewPan;
		_previewPan = default;
		if (_previewZoom is null || GetPreviewTransform() is not { } t) return;

		var maxX = Math.Max(0, (t.RenderedWidth - OverlayDragCanvas.Bounds.Width) / 2);
		var maxY = Math.Max(0, (t.RenderedHeight - OverlayDragCanvas.Bounds.Height) / 2);
		_previewPan = new Vector(Math.Clamp(pan.X, -maxX, maxX), Math.Clamp(pan.Y, -maxY, maxY));
	}

	/// <summary>Called first by the canvas's own pointer handlers - a middle-button drag pans instead of editing.</summary>
	private bool TryStartPreviewPan(PointerPressedEventArgs e)
	{
		if (_previewZoom is null || !e.GetCurrentPoint(OverlayDragCanvas).Properties.IsMiddleButtonPressed) return false;

		_panning = true;
		_panStartPointer = e.GetPosition(OverlayDragCanvas);
		_panStartOffset = _previewPan;
		e.Pointer.Capture(OverlayDragCanvas);
		e.Handled = true;
		return true;
	}

	private bool ContinuePreviewPan(PointerEventArgs e)
	{
		if (!_panning) return false;

		_previewPan = _panStartOffset + (e.GetPosition(OverlayDragCanvas) - _panStartPointer);
		ApplyPreviewLayout();
		return true;
	}

	private bool EndPreviewPan(PointerReleasedEventArgs e)
	{
		if (!_panning) return false;

		_panning = false;
		e.Pointer.Capture(null);
		return true;
	}
}
