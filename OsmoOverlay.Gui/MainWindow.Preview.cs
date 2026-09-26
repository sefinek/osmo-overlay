using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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

			_previewFrameSize = new PixelSize(previewWidth, previewHeight);
			ApplyPreviewLayout();

			await _previewPlayer.OpenAsync(summary, previewWidth, previewHeight);
			LoadOverlayPresets(summary.Video.Width, summary.Video.Height);

			foreach (PreviewTimeline timeline in Timelines) timeline.Maximum = _previewPlayer.Duration.TotalSeconds;
			PreviewPlaceholder.IsVisible = false;
			UpdatePreviewStatus();
			TransportPanel.IsEnabled = true;
			CutsEditor.Attach(summary.Video.Fps, SourceFrames);
			RefreshCutViews();
			foreach (PreviewTimeline timeline in Timelines) timeline.IsEnabled = true;
			UpdateAudioPanel();
			_ = LoadTimelineTracksAsync(summary);

			// _hasGpsFix false means the recording never had a fix at all - not an anomaly worth
			// flagging on the timeline, just this file's normal state (see RunGetSummaryAsync).
			IReadOnlyList<TimeRange> gpsLoss = _hasGpsFix && summary.TelemetryFrames is { Count: > 0 } rawFrames
				? [.. TelemetryProcessor.FindGpsLossRanges(rawFrames).Select(r => new TimeRange(r.Start, r.End))]
				: [];
			foreach (PreviewTimeline timeline in Timelines) timeline.GpsLoss = gpsLoss;
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
		foreach (PreviewTimeline timeline in Timelines) timeline.IsEnabled = false;
		UpdateAudioPanel();

		_previewFrameSize = null;
		PreviewVideo.Clear();
		UpdatePreviewStatus();
		CutScrim.IsVisible = false;
		PreviewPlaceholder.IsVisible = true;
		_overlayPresetsLoaded = false;
		foreach (PreviewTimeline timeline in Timelines) timeline.GpsLoss = [];
	}

	/// <summary>A still (paused, seeking) - played frames go to PreviewVideo on the render thread, see OnPlaybackFrameShown.</summary>
	private void OnPreviewFrameReady(ComposedPreviewFrame frame)
	{
		if (_previewFrameSize is null) return;

		PreviewVideo.ShowStill(frame);
		ShowPreviewPosition(frame.Position);
	}

	/// <summary>
	///     The newest played frame reached the screen. Posted from the render thread, so it can land just after playback
	///     stopped - the still decoded for the pause brings the position then.
	/// </summary>
	private void OnPlaybackFrameShown(TimeSpan position, double framesPerSecond)
	{
		if (!_previewPlayer.IsPlaying) return;

		ShowPreviewPosition(position);
		ShowPlaybackFps(framesPerSecond);
	}

	private void OnPreviewPlaybackStarted()
	{
		PreviewVideo.StartPlayback(_previewPlayer.Frames);
		ResetPlaybackFps();
	}

	private void ShowPreviewPosition(TimeSpan position)
	{
		if (!_timelineScrubbing)
		{
			_suppressTimelineEvent = true;
			PreviewTimeline.Value = position.TotalSeconds;
			_suppressTimelineEvent = false;
		}

		_previewPosition = position;
		_previewFrame = FrameAt(position.TotalSeconds);
		UpdateCutScrim(position);
		UpdatePreviewTimeText();
		UpdateFrameStatus();
	}

	private void OnPreviewPlaybackStopped()
	{
		ShowPlayingState(false);
		UpdatePreviewTimeText();
		ResetPlaybackFps();
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

	/// <summary>The time format flyout's entries, one per PreviewTimeFormats option (the format in the button's Tag).</summary>
	private void BuildTimeFormatMenu()
	{
		foreach (ChoiceOption<PreviewTimeFormat> option in PreviewTimeFormats.Options)
		{
			var button = new Button { Classes = { "toolbar" }, Content = option.Label, Tag = option.Value };
			button.Click += (_, _) =>
			{
				TimeFormatButton.Flyout?.Hide();
				SetTimeFormat(option.Value);
			};
			TimeFormatMenu.Children.Add(button);
		}

		ApplyTimeFormatButtonClasses();
	}

	private void OnPreviewTimeTextPressed(object? sender, PointerPressedEventArgs e)
	{
		if (!e.GetCurrentPoint(PreviewTimeText).Properties.IsLeftButtonPressed) return;

		SetTimeFormat(PreviewTimeFormats.Next(_timeFormat));
		e.Handled = true;
	}

	private void SetTimeFormat(PreviewTimeFormat format, bool save = true)
	{
		if (format == _timeFormat) return;

		_timeFormat = format;
		ApplyTimeFormatButtonClasses();
		if (save) OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { PreviewTimeFormat = format.ToString() });
		UpdatePreviewTimeText();
	}

	private void ApplyTimeFormatButtonClasses()
	{
		foreach (Button button in TimeFormatMenu.Children.OfType<Button>())
			button.Classes.Set("active", button.Tag is PreviewTimeFormat format && format == _timeFormat);
		TimeFormatButton.Classes.Set("active", _timeFormat != PreviewTimeFormat.Seconds);
	}

	/// <summary>
	///     The readout's column is Auto next to the timeline's *, so the timeline takes whatever width the text leaves.
	///     Runs for every frame shown while playing - the end of the recording is formatted once per recording and format.
	/// </summary>
	private void UpdatePreviewTimeText()
	{
		TimeSpan duration = _previewPlayer.Duration;
		if (_timeEndText is not { } end || end.Format != _timeFormat || end.Duration != duration || !ReferenceEquals(end.Summary, _summary))
			_timeEndText = end = (_timeFormat, duration, _summary, FormatTime(duration, SourceFrames));

		PreviewTimeText.Text = $"{FormatTime(_previewPosition, _previewFrame)} / {end.Text}";
	}

	private string FormatTime(TimeSpan time, long frame)
	{
		return _summary is null
			? PreviewTimeFormats.Format(PreviewTimeFormat.Seconds, time, 0, 0, null)
			: PreviewTimeFormats.Format(_timeFormat, time, frame, _summary.Video.Fps, _summary.Video.Timecode);
	}

	/// <summary>
	///     The compact timeline in the transport row stays there, and the expanded one (in the log's place, see
	///     SetTimelineExpanded) shows the same: whatever describes the recording goes to both. The compact one holds the
	///     position everything reads (PreviewTimeline.Value); the expanded one follows it and hands a drag back to it.
	/// </summary>
	private PreviewTimeline[] Timelines => [PreviewTimeline, ExpandedTimeline];

	private void WirePreviewTimeline()
	{
		foreach (PreviewTimeline timeline in Timelines)
		{
			timeline.ScrubStarted += () =>
			{
				_timelineScrubbing = true;
				_previewPlayer.BeginScrubDrag();
			};
			timeline.ScrubEnded += () =>
			{
				_timelineScrubbing = false;
				_previewPlayer.EndScrubDrag(TimeSpan.FromSeconds(PreviewTimeline.Value));
			};
		}
	}

	private void OnExpandedTimelineValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
	{
		PreviewTimeline.Value = e.NewValue;
	}

	private void OnPreviewTimelineValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
	{
		ExpandedTimeline.Value = e.NewValue;
		if (_suppressTimelineEvent) return;

		// Keyframes while dragging keep up with the pointer; the exact frame follows on release (EndScrubDrag).
		_previewPlayer.RequestSeek(TimeSpan.FromSeconds(e.NewValue), _timelineScrubbing ? SeekAccuracy.Keyframe : SeekAccuracy.Exact);
	}
}
