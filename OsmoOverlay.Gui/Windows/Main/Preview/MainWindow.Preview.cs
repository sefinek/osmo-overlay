using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
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

		try
		{
			_openedPreviewMaxWidth = ResolvePreviewMaxWidth(summary);
			ShowQualityPicker();
			double scale = Math.Min(1.0, (double)_openedPreviewMaxWidth / summary.Video.Width);
			int previewWidth = (int)(summary.Video.Width * scale) & ~1;
			int previewHeight = (int)(summary.Video.Height * scale) & ~1;

			_previewFrameSize = new PixelSize(previewWidth, previewHeight);
			ApplyPreviewLayout();

			await _previewPlayer.OpenAsync(summary, previewWidth, previewHeight);
			LoadOverlayPresets();

			foreach (PreviewTimeline timeline in Timelines) timeline.Maximum = _previewPlayer.Duration.TotalSeconds;
			PreviewPlaceholder.IsVisible = false;
			UpdatePreviewStatus();
			TransportPanel.IsEnabled = true;
			CutsEditor.Attach(summary.Video.Fps, SourceFrames);
			MomentsEditor.Attach(summary.Video.Fps, SourceFrames,
				summary.DerivedFrames is { } derived ? SpeedCalibration.Apply(derived, OverlaySettingsStore.Load().SpeedCorrectionPercent) : null,
				UnitSystem.Metric);
			RefreshCutViews();
			RefreshMoments();
			foreach (PreviewTimeline timeline in Timelines) timeline.IsEnabled = true;
			UpdateAudioPanel();
			_ = LoadTimelineTracksAsync(summary);

			// No fix at all isn't an anomaly worth flagging on the timeline, just this file's normal state (see RunGetSummaryAsync).
			IReadOnlyList<TimeRange> gpsLoss = _availability.GpsFix && summary.TelemetryFrames is { Count: > 0 } rawFrames
				? [.. TelemetryProcessor.FindGpsLossRanges(rawFrames).Select(r => new TimeRange(r.Start, r.End))]
				: [];
			foreach (PreviewTimeline timeline in Timelines) timeline.GpsLoss = gpsLoss;
		}
		catch (Exception ex)
		{
			AppendLog(string.Format(Strings.Preview_Unavailable, ex.Message));
			ClosePreview();
		}
	}

	private void ClosePreview()
	{
		_previewPlayer.Close();
		ShowPlayingState(false);
		TransportPanel.IsEnabled = false;
		CutsEditor.Attach(0, 0);
		MomentsEditor.Attach(0, 0, null, UnitSystem.Metric);
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
		OnSecondScreenPlaybackStarted();
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
		RefreshWidgetFrames();
	}

	private void OnPreviewPlaybackStopped()
	{
		ShowPlayingState(false);
		UpdatePreviewTimeText();
		ResetPlaybackFps();
		QueueAutoQuality();
		OnSecondScreenPlaybackStopped();
	}

	// Width-based (not "720p" height labels): _previewMaxWidth caps the preview by width (OpenPreviewAsync), and a
	// height would depend on the recording's aspect ratio.
	private static readonly List<PreviewQualityOption> PreviewQualityOptions =
	[
		new(Strings.Main_Auto, AutoPreviewWidth),
		new($"{Strings.Preview_QualityLow} · 640 px", 640),
		new($"{Strings.Preview_QualityMedium} · 960 px", 960),
		new($"{Strings.Preview_QualityHigh} · 1280 px", 1280),
		new($"{Strings.Preview_QualityVeryHigh} · 1920 px", 1920),
		new(Strings.Preview_QualityFull, int.MaxValue)
	];

	// OverlaySettings.PreviewMaxWidth of "Auto": the preview is sized to how big it is on screen (ResolvePreviewMaxWidth).
	private const int AutoPreviewWidth = 0;

	// A quality counts as enough when it has at least this share of the pixels the preview takes on screen - a step
	// up for the last few percent would only cost decoding speed.
	private const double AutoQualityTolerance = 0.9;

	private readonly DispatcherTimer _autoQualityDelay = new() { Interval = TimeSpan.FromMilliseconds(800) };
	private bool _suppressPreviewQualityEvent;
	// The width cap the open preview was sized with - what Auto compares a new size against.
	private int _openedPreviewMaxWidth;

	private void WirePreviewQuality()
	{
		ShowQualityPicker();
		_autoQualityDelay.Tick += (_, _) => ApplyAutoQuality();
	}

	/// <summary>The picker with the chosen quality selected - and while it's Auto, the quality it picked in its label ("Auto · 1280 px"), once a preview is open.</summary>
	private void ShowQualityPicker()
	{
		List<PreviewQualityOption> options = [.. PreviewQualityOptions];
		if (_previewMaxWidth == AutoPreviewWidth && _openedPreviewMaxWidth > 0)
		{
			string picked = _openedPreviewMaxWidth == int.MaxValue ? Strings.Preview_QualityFull : $"{_openedPreviewMaxWidth} px";
			options[0] = new PreviewQualityOption($"{Strings.Main_Auto} · {picked}", AutoPreviewWidth);
		}

		_suppressPreviewQualityEvent = true;
		PreviewQualityCombo.ItemsSource = options;
		PreviewQualityCombo.SelectedItem = options.FirstOrDefault(o => o.MaxWidth == _previewMaxWidth) ?? options[3];
		_suppressPreviewQualityEvent = false;
	}

	/// <summary>The width cap to open `summary`'s preview at: the chosen quality's, or for Auto the smallest one that fills the preview's size on screen.</summary>
	private int ResolvePreviewMaxWidth(FileSummary summary)
	{
		if (_previewMaxWidth != AutoPreviewWidth) return _previewMaxWidth;

		double needed = NeededPreviewWidth(summary);
		foreach (PreviewQualityOption option in PreviewQualityOptions)
		{
			if (option.MaxWidth == AutoPreviewWidth) continue;

			if (Math.Min(option.MaxWidth, summary.Video.Width) >= needed * AutoQualityTolerance) return option.MaxWidth;
		}

		return int.MaxValue;
	}

	/// <summary>The preview's width in device pixels as it's shown now: the fitted frame, or at a zoom the source's width times it (100% is one source pixel per device pixel).</summary>
	private double NeededPreviewWidth(FileSummary summary)
	{
		if (_previewZoom is { } zoom) return summary.Video.Width * zoom;

		double aspect = summary.Video.Width / (double)summary.Video.Height;
		double fitted = Math.Min(OverlayDragCanvas.Bounds.Width, OverlayDragCanvas.Bounds.Height * aspect);
		// Not laid out yet - the same width the quality was fixed at before Auto.
		return fitted > 0 ? fitted * UiScale.DeviceScaling(this) : 1280;
	}

	/// <summary>The preview's size on screen changed (the window, the zoom, full screen) - once it settles, Auto may need another quality.</summary>
	private void QueueAutoQuality()
	{
		if (_previewMaxWidth != AutoPreviewWidth) return;

		_autoQualityDelay.Stop();
		_autoQualityDelay.Start();
	}

	private void ApplyAutoQuality()
	{
		_autoQualityDelay.Stop();
		if (_summary is not { } summary || _phase != UiPhase.SummaryReady || _previewMaxWidth != AutoPreviewWidth) return;

		// Reopening stops the playback - it's looked at again when the playback ends (OnPreviewPlaybackStopped).
		if (_previewPlayer.IsPlaying || _timelineScrubbing || ResolvePreviewMaxWidth(summary) == _openedPreviewMaxWidth) return;

		_ = ReopenPreviewAsync();
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
		if (_summary is not { } summary || _phase != UiPhase.SummaryReady) return;

		double position = PreviewTimeline.Value;
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
			timeline.ScrubStarted += BeginTimelineScrub;
			timeline.ScrubEnded += EndTimelineScrub;
		}
	}

	/// <summary>A drag moving the playhead - from either timeline or the layers: playback pauses until it ends, then goes on from there.</summary>
	private void BeginTimelineScrub()
	{
		_timelineScrubbing = true;
		_previewPlayer.BeginScrubDrag();
	}

	private void EndTimelineScrub()
	{
		_timelineScrubbing = false;
		_previewPlayer.EndScrubDrag(TimeSpan.FromSeconds(PreviewTimeline.Value));
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
