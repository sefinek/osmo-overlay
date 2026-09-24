using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;
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
			UpdateAudioPanel();

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
		UpdateAudioPanel();

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

	// Width-based (not "720p" height labels): _previewMaxWidth caps the preview by width (OpenPreviewAsync), and a
	// height would depend on the recording's aspect ratio.
	private static readonly List<PreviewQualityOption> PreviewQualityOptions =
	[
		new("Low · 640 px", 640),
		new("Medium · 960 px", 960),
		new("High · 1280 px", 1280),
		new("Very high · 1920 px", 1920),
		new("Full resolution", int.MaxValue)
	];

	private bool _suppressPreviewQualityEvent;

	private void WirePreviewQuality()
	{
		_suppressPreviewQualityEvent = true;
		PreviewQualityCombo.ItemsSource = PreviewQualityOptions;
		PreviewQualityCombo.SelectedItem = PreviewQualityOptions.FirstOrDefault(o => o.MaxWidth == _previewMaxWidth) ?? PreviewQualityOptions[2];
		_suppressPreviewQualityEvent = false;
	}

	/// <summary>The decoder and the preview bitmap are sized at open, so a new quality reopens the preview - where it was.</summary>
	private async void OnPreviewQualityChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressPreviewQualityEvent || PreviewQualityCombo.SelectedItem is not PreviewQualityOption option ||
		    option.MaxWidth == _previewMaxWidth)
			return;

		_previewMaxWidth = option.MaxWidth;
		OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { PreviewMaxWidth = _previewMaxWidth });
		await ReopenPreviewAsync();
	}

	/// <summary>Reopens the preview for a setting baked in at open (quality, GPS smoothing, map/route intro), back at the same moment of the recording.</summary>
	private async Task ReopenPreviewAsync()
	{
		if (_summary is not { HasTelemetry: true } summary || _phase != UiPhase.SummaryReady) return;

		var position = PreviewTimeline.Value;
		await OpenPreviewAsync(summary);
		if (PreviewTimeline.IsEnabled && position > 0) PreviewTimeline.Value = position;
	}

	private sealed record PreviewQualityOption(string Display, int MaxWidth)
	{
		public override string ToString()
		{
			return Display;
		}
	}

	/// <summary>Toolbar toggle above the preview: the time readout in whole seconds or with milliseconds (the same format the cut editor uses).</summary>
	private void OnTogglePreciseTimeClick(object? sender, RoutedEventArgs e)
	{
		_preciseTime = !_preciseTime;
		TogglePreciseTimeButton.Classes.Set("active", _preciseTime);
		OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { PreviewPreciseTime = _preciseTime });
		UpdatePreviewTimeText();
	}

	/// <summary>The readout's column is Auto next to the timeline's *, so the timeline takes whatever width the text leaves.</summary>
	private void UpdatePreviewTimeText()
	{
		PreviewTimeText.Text = $"{FormatTime(_previewPosition)} / {FormatTime(_previewPlayer.Duration)}";
	}

	private string FormatTime(TimeSpan t)
	{
		if (_preciseTime) return TimeText.Format(t.TotalSeconds);
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

		// Keyframes while dragging keep up with the pointer; the exact frame follows on release (EndScrubDrag).
		_previewPlayer.RequestSeek(TimeSpan.FromSeconds(e.NewValue), _timelineScrubbing ? SeekAccuracy.Keyframe : SeekAccuracy.Exact);
	}

}
