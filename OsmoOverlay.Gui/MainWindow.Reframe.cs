using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Reframe;

namespace OsmoOverlay.Gui;

/// <summary>
///     Where a 360 recording's flat picture looks (FileSummary.Fisheye, ReframeView). With the toolbar's globe on, the
///     preview's canvas frames the view instead of editing widgets: a drag turns it (the picture follows the pointer),
///     Shift+drag rolls it, the wheel widens or narrows it, a double-click resets it; the horizon button next to it turns
///     the leveling (HorizonLeveling) off and on. The preview shows each change live
///     (PreviewPlayer.SetReframe - the frame on screen is converted again, not decoded) and the render uses the view;
///     the timeline's filmstrip is generated again for it once a drag or a wheel turn is done.
/// </summary>
public partial class MainWindow
{
	private const double WheelFovStep = 1.1;

	private ReframeView _reframe = new();
	private bool _reframing;
	private Point? _reframeDragStart;
	private ReframeView _reframeDragFrom = new();
	private readonly DispatcherTimer _reframeThumbnailDelay = new() { Interval = TimeSpan.FromMilliseconds(600) };

	private bool Is360 => _summary?.Fisheye is not null;

	/// <summary>A recording's lenses with `view` - leveled from its telemetry; null for a flat recording.</summary>
	private static Reframer? ReframerFor(FileSummary summary, ReframeView view)
	{
		return Reframer.For(summary.Fisheye, summary.TelemetryFrames, summary.CameraFormat, view);
	}

	private void WireReframe()
	{
		OverlayDragCanvas.PointerWheelChanged += OnReframeWheel;
		_reframeThumbnailDelay.Tick += async (_, _) =>
		{
			_reframeThumbnailDelay.Stop();
			await ReloadTimelineThumbnailsAsync();
		};
	}

	/// <summary>A newly loaded recording starts from the default view, the mode off; the globe only shows for a 360 one.</summary>
	private void ResetReframe()
	{
		_reframe = new ReframeView();
		_previewPlayer.SetReframe(_reframe, TimeSpan.Zero);
		SetReframeMode(false);
	}

	private void UpdateReframeControls()
	{
		bool available = Is360 && _phase == UiPhase.SummaryReady;
		ToggleReframeButton.IsVisible = available;
		ToggleLevelButton.IsVisible = available;
		ToggleLevelButton.Classes.Set("active", _reframe.Level);
		StatusReframePanel.IsVisible = available;
		if (!available) SetReframeMode(false);
		StatusReframeText.Text = string.Create(CultureInfo.InvariantCulture,
			$"{_reframe.Yaw:0}° {_reframe.Pitch:+0;-0;0}° roll {_reframe.Roll:0}°, {_reframe.FovDegrees:0}° wide{(_reframe.Level ? ", level" : "")}");
	}

	private void OnToggleReframeClick(object? sender, RoutedEventArgs e)
	{
		SetReframeMode(!_reframing);
	}

	private void OnToggleLevelClick(object? sender, RoutedEventArgs e)
	{
		ApplyReframe(_reframe with { Level = !_reframe.Level });
		ScheduleThumbnailReload();
	}

	private void SetReframeMode(bool on)
	{
		_reframing = on && Is360;
		ToggleReframeButton.Classes.Set("active", _reframing);
		OverlayDragCanvas.Cursor = _reframing ? SizeAllCursor : null;
		if (_reframing) HideHoverIcons();
	}

	/// <summary>Called by the canvas's pointer handlers after panning - in the mode, a left-button drag frames the view.</summary>
	private bool TryStartReframeDrag(PointerPressedEventArgs e)
	{
		if (!_reframing || !e.GetCurrentPoint(OverlayDragCanvas).Properties.IsLeftButtonPressed) return false;

		e.Handled = true;
		if (e.ClickCount == 2)
		{
			ApplyReframe(new ReframeView(Level: _reframe.Level));
			ScheduleThumbnailReload();
			return true;
		}

		_reframeDragStart = e.GetPosition(OverlayDragCanvas);
		_reframeDragFrom = _reframe;
		e.Pointer.Capture(OverlayDragCanvas);
		return true;
	}

	private bool ContinueReframeDrag(PointerEventArgs e)
	{
		if (!_reframing) return false;
		if (_reframeDragStart is not { } start || GetPreviewTransform() is not { } t) return true;

		// Degrees per on-screen pixel, so the picture moves with the pointer at any zoom or view width.
		double perPixel = _reframeDragFrom.FovDegrees / t.RenderedWidth;
		Vector moved = e.GetPosition(OverlayDragCanvas) - start;
		ApplyReframe(e.KeyModifiers.HasFlag(KeyModifiers.Shift)
			? _reframeDragFrom with { Roll = _reframeDragFrom.Roll + moved.X * perPixel }
			: _reframeDragFrom with { Yaw = _reframeDragFrom.Yaw - moved.X * perPixel, Pitch = _reframeDragFrom.Pitch + moved.Y * perPixel });
		return true;
	}

	private bool EndReframeDrag(PointerReleasedEventArgs e)
	{
		if (_reframeDragStart is null) return false;

		_reframeDragStart = null;
		e.Pointer.Capture(null);
		ScheduleThumbnailReload();
		return true;
	}

	private void OnReframeWheel(object? sender, PointerWheelEventArgs e)
	{
		// Ctrl+wheel stays the preview's zoom.
		if (!_reframing || e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

		ApplyReframe(_reframe with { FovDegrees = _reframe.FovDegrees * Math.Pow(WheelFovStep, -e.Delta.Y) });
		ScheduleThumbnailReload();
		e.Handled = true;
	}

	private void ApplyReframe(ReframeView view)
	{
		view = view.Normalized();
		if (view == _reframe) return;

		_reframe = view;
		_previewPlayer.SetReframe(view, _previewPosition);
		UpdateReframeControls();
	}

	/// <summary>The filmstrip follows the view once it stays put for a moment - not on every step of a drag.</summary>
	private void ScheduleThumbnailReload()
	{
		_reframeThumbnailDelay.Stop();
		_reframeThumbnailDelay.Start();
	}
}
