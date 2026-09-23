using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Preview;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Gui;

/// <summary>Live preview: opening/closing PreviewPlayer for a loaded file, its timeline (PreviewTimeline: scrubbing, GPS loss, cuts) and the frame display.</summary>
public partial class MainWindow
{
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

			PreviewTimeline.Maximum = _previewPlayer.Duration.TotalSeconds;
			PreviewPlaceholder.IsVisible = false;
			TransportPanel.IsEnabled = true;
			CutsEditor.Attach(summary.Video.Fps, SourceFrames);
			RefreshCutViews();
			PreviewTimeline.IsEnabled = true;

			// _hasGpsFix false means the recording never had a fix at all - not an anomaly worth
			// flagging on the timeline, just this file's normal state (see RunGetSummaryAsync).
			PreviewTimeline.GpsLoss = _hasGpsFix && summary.TelemetryFrames is { Count: > 0 } rawFrames
				? [.. TelemetryProcessor.FindGpsLossRanges(rawFrames).Select(r => new TimeRange(r.Start, r.End))]
				: [];
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
		ShowPlayingState(false);
		TransportPanel.IsEnabled = false;
		CutsEditor.Attach(0, 0);
		PreviewTimeline.IsEnabled = false;

		_previewBitmap = null;
		PreviewImage.Source = null;
		CutScrim.IsVisible = false;
		PreviewPlaceholder.IsVisible = true;
		_overlayPresetsLoaded = false;
		PreviewTimeline.GpsLoss = [];
	}

	private void OnPreviewFrameReady(ComposedPreviewFrame frame)
	{
		if (_previewBitmap is null) return;

		using (ILockedFramebuffer fb = _previewBitmap.Lock()) Marshal.Copy(frame.Bgra, 0, fb.Address, frame.Bgra.Length);

		PreviewImage.InvalidateVisual();

		if (!_timelineScrubbing)
		{
			_suppressTimelineEvent = true;
			PreviewTimeline.Value = frame.Position.TotalSeconds;
			_suppressTimelineEvent = false;
		}

		_previewPosition = frame.Position;
		UpdateCutScrim(frame.Position);
		UpdatePreviewTimeText();
	}

	private void OnPreviewPlaybackStopped()
	{
		ShowPlayingState(false);
		UpdatePreviewTimeText();
	}

	/// <summary>
	///     Whole seconds while playing (milliseconds would just flicker), milliseconds while paused - that's when
	///     frame stepping and setting a range or cut need to see exactly which frame is on screen.
	/// </summary>
	private void UpdatePreviewTimeText()
	{
		var precise = !_previewPlayer.IsPlaying;
		PreviewTimeText.Text = $"{FormatTime(_previewPosition, precise)} / {FormatTime(_previewPlayer.Duration, precise)}";
	}

	private static string FormatTime(TimeSpan t, bool precise)
	{
		if (precise) return TimeText.Format(t.TotalSeconds);
		return t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
	}

	private void WirePreviewTimeline()
	{
		PreviewTimeline.ScrubStarted += () =>
		{
			_timelineScrubbing = true;
			_previewPlayer.BeginScrubDrag();
		};
		PreviewTimeline.ScrubEnded += () =>
		{
			_timelineScrubbing = false;
			_previewPlayer.EndScrubDrag(TimeSpan.FromSeconds(PreviewTimeline.Value));
		};
	}

	private void OnPreviewTimelineValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
	{
		if (_suppressTimelineEvent) return;

		_ = _previewPlayer.RequestSeekAsync(TimeSpan.FromSeconds(e.NewValue));
	}

}
