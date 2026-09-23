using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Preview;
using OsmoOverlay.Core.Telemetry;
using AvaloniaRectangle = Avalonia.Controls.Shapes.Rectangle;

namespace OsmoOverlay.Gui;

/// <summary>Live preview: opening/closing PreviewPlayer for a loaded file, the GPS-loss scrubber strip, and the play/pause/seek transport controls.</summary>
public partial class MainWindow
{
	// Same red used elsewhere in this window for error/invalid states (e.g. the API key hint), reused
	// here so a GPS-loss mark reads as the same "something's wrong here" signal.
	private static readonly IBrush GpsLossBrush = Palette.Danger;

	private async Task OpenPreviewAsync(FileSummary summary)
	{
		ClosePreview();

		if (summary.DerivedFrames is not { Count: > 0 }) return;

		try
		{
			var scale = Math.Min(1.0, (double)_previewMaxWidth / summary.Video.Width);
			var previewWidth = (int)(summary.Video.Width * scale) & ~1;
			var previewHeight = (int)(summary.Video.Height * scale) & ~1;

			_previewBitmap = new WriteableBitmap(new PixelSize(previewWidth, previewHeight), new Vector(96, 96),
				PixelFormat.Bgra8888, AlphaFormat.Opaque);
			PreviewImage.Source = _previewBitmap;

			await _previewPlayer.OpenAsync(summary, previewWidth, previewHeight);
			LoadOverlayPresets(summary.Video.Width, summary.Video.Height);

			PreviewSlider.Maximum = _previewPlayer.Duration.TotalSeconds;
			PreviewPlaceholder.IsVisible = false;
			PlayPauseButton.IsEnabled = true;
			SetRangeStartButton.IsEnabled = true;
			SetRangeEndButton.IsEnabled = true;
			PreviewSlider.IsEnabled = true;

			// _hasGpsFix false means the recording never had a fix at all - not an anomaly worth
			// flagging red on the scrubber, just this file's normal state (see RunGetSummaryAsync).
			_gpsLossRanges = _hasGpsFix && summary.TelemetryFrames is { Count: > 0 } rawFrames
				? TelemetryProcessor.FindGpsLossRanges(rawFrames)
				: [];
			DrawGpsLossMarks();
			DrawRangeMarks();
		}
		catch (Exception ex)
		{
			AppendLog($"Preview unavailable: {ex.Message}");
			ClosePreview();
		}
	}

	private void ClosePreview()
	{
		_previewPlayer.Close();
		PlayPauseButton.Content = "Play";
		PlayPauseButton.IsEnabled = false;
		SetRangeStartButton.IsEnabled = false;
		SetRangeEndButton.IsEnabled = false;
		PreviewSlider.IsEnabled = false;

		_previewBitmap = null;
		PreviewImage.Source = null;
		PreviewPlaceholder.IsVisible = true;
		_overlayPresetsLoaded = false;

		_gpsLossRanges = [];
		GpsLossCanvas.Children.Clear();
	}

	/// <summary>
	///     Redraws the GPS-loss strip below PreviewSlider from _gpsLossRanges - called both when a new
	///     file's ranges are computed and whenever GpsLossCanvas is resized (its width, needed to turn a
	///     [Start,End] seconds range into pixels, isn't known until layout runs).
	/// </summary>
	private void DrawGpsLossMarks()
	{
		GpsLossCanvas.Children.Clear();

		var width = GpsLossCanvas.Bounds.Width;
		var duration = PreviewSlider.Maximum;
		if (width <= 0 || duration <= 0 || _gpsLossRanges.Count == 0) return;

		foreach (var (start, end) in _gpsLossRanges)
		{
			var x1 = width * Math.Clamp(start / duration, 0, 1);
			var x2 = width * Math.Clamp(end / duration, 0, 1);
			var rect = new AvaloniaRectangle
			{
				Width = Math.Max(2, x2 - x1), Height = 4, Fill = GpsLossBrush, RadiusX = 1, RadiusY = 1
			};
			Canvas.SetLeft(rect, x1);
			Canvas.SetTop(rect, 0);
			GpsLossCanvas.Children.Add(rect);
		}
	}

	private void OnPreviewFrameReady(ComposedPreviewFrame frame)
	{
		if (_previewBitmap is null) return;

		using (ILockedFramebuffer fb = _previewBitmap.Lock()) Marshal.Copy(frame.Bgra, 0, fb.Address, frame.Bgra.Length);

		PreviewImage.InvalidateVisual();

		if (!_sliderDragInProgress)
		{
			_suppressSliderEvent = true;
			PreviewSlider.Value = frame.Position.TotalSeconds;
			_suppressSliderEvent = false;
		}

		PreviewTimeText.Text = $"{FormatTime(frame.Position)} / {FormatTime(_previewPlayer.Duration)}";
	}

	private void OnPreviewPlaybackStopped()
	{
		PlayPauseButton.Content = "Play";
	}

	private static string FormatTime(TimeSpan t)
	{
		return t.ToString(@"mm\:ss");
	}

	private void OnPreviewSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
	{
		if (_suppressSliderEvent) return;

		_ = _previewPlayer.RequestSeekAsync(TimeSpan.FromSeconds(e.NewValue));
	}

	private void OnPreviewSliderPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		_sliderDragInProgress = true;
		_previewPlayer.BeginScrubDrag();
	}

	private void OnPreviewSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		_sliderDragInProgress = false;
		_previewPlayer.EndScrubDrag(TimeSpan.FromSeconds(PreviewSlider.Value));
	}

	private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
	{
		var wasPlaying = _previewPlayer.IsPlaying;
		_previewPlayer.TogglePlayPause(TimeSpan.FromSeconds(PreviewSlider.Value));
		if (!wasPlaying) PlayPauseButton.Content = "Pause";
	}
}
