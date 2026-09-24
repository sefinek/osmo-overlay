using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Gui;

/// <summary>
///     The timeline's filmstrip and waveform (PreviewTimeline) and the scrollbar under it. The generators belong to the
///     loaded recording, not to the preview: reopening the preview (quality, settings) keeps what they already made.
/// </summary>
public partial class MainWindow
{
	// Thumbnails are made at twice the filmstrip's height, so they stay sharp on a HiDPI screen.
	private const int ThumbnailHeight = 96;

	private TimelineThumbnails? _timelineThumbnails;
	private AudioWaveform? _timelineWaveform;
	private FileSummary? _timelineTracksFor;

	private void WireTimelineTracks()
	{
		PreviewTimeline.ViewChanged += SyncTimelineScrollBar;
		Closed += (_, _) => ReleaseTimelineTracks();
		SetTimelineExpanded(OverlaySettingsStore.Load().PreviewTimelineExpanded, false);
	}

	private void OnToggleTimelineClick(object? sender, RoutedEventArgs e)
	{
		SetTimelineExpanded(!PreviewTimeline.Expanded, true);
	}

	/// <summary>
	///     One timeline control, moved between the transport row (compact) and the log's place under the window
	///     (expanded) - the bottom row then shrinks to the timeline's height, leaving the rest to the preview.
	/// </summary>
	private void SetTimelineExpanded(bool expanded, bool save)
	{
		if (PreviewTimeline.Parent is Border oldHost) oldHost.Child = null;
		(expanded ? ExpandedTimelineHost : CompactTimelineHost).Child = PreviewTimeline;
		PreviewTimeline.Expanded = expanded;

		TimelineCard.IsVisible = expanded;
		LogCard.IsVisible = !expanded;
		RootGrid.RowDefinitions[1].Height = expanded ? GridLength.Auto : new GridLength(230);
		ToggleTimelineButton.Classes.Set("active", expanded);
		SyncTimelineScrollBar();

		if (save) OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { PreviewTimelineExpanded = expanded });
	}

	/// <summary>A render's progress is in the log - it takes the timeline's place back while rendering.</summary>
	private void ShowLogForRender()
	{
		if (PreviewTimeline.Expanded) SetTimelineExpanded(false, false);
	}

	private void OnTimelineScroll(object? sender, ScrollEventArgs e)
	{
		PreviewTimeline.ScrollTo(e.NewValue);
	}

	private void SyncTimelineScrollBar()
	{
		var duration = PreviewTimeline.Maximum - PreviewTimeline.Minimum;
		var visible = PreviewTimeline.ViewLength;
		TimelineScrollBar.IsVisible = duration > 0 && visible < duration - 1e-3;
		TimelineScrollBar.Maximum = Math.Max(0, duration - visible);
		TimelineScrollBar.ViewportSize = visible;
		TimelineScrollBar.LargeChange = visible * 0.9;
		TimelineScrollBar.SmallChange = visible * 0.1;
		TimelineScrollBar.Value = PreviewTimeline.ViewStart;
	}

	/// <summary>With the preview: starts the generators for a newly loaded recording, or hands the timeline the ones it already has.</summary>
	private async Task LoadTimelineTracksAsync(FileSummary summary)
	{
		var aspect = (double)summary.Video.Width / summary.Video.Height;
		if (ReferenceEquals(_timelineTracksFor, summary))
		{
			PreviewTimeline.SetSources(_timelineThumbnails, _timelineWaveform, summary.Video.Fps, aspect);
			return;
		}

		ReleaseTimelineTracks();
		_timelineTracksFor = summary;

		List<PlaybackSegment> segments = PlaybackSegment.Of(summary);
		var thumbnailWidth = (int)Math.Round(ThumbnailHeight * aspect) & ~1;
		TimelineThumbnails? thumbnails = null;
		AudioWaveform? waveform = null;
		try
		{
			(thumbnails, waveform) = await Task.Run(() =>
				(TimelineThumbnails.Open(segments, summary.Video.Fps, thumbnailWidth, ThumbnailHeight), AudioWaveform.Start(segments)));
		}
		catch (InvalidOperationException ex)
		{
			AppLogger.Warn(ex, "Timeline thumbnails and waveform unavailable");
		}

		// Another recording was loaded while these opened.
		if (!ReferenceEquals(_timelineTracksFor, summary))
		{
			thumbnails?.Dispose();
			waveform?.Dispose();
			return;
		}

		_timelineThumbnails = thumbnails;
		_timelineWaveform = waveform;
		PreviewTimeline.SetSources(thumbnails, waveform, summary.Video.Fps, aspect);
	}

	private void ReleaseTimelineTracks()
	{
		PreviewTimeline.SetSources(null, null, 0, 0);
		_timelineThumbnails?.Dispose();
		_timelineWaveform?.Dispose();
		_timelineThumbnails = null;
		_timelineWaveform = null;
		_timelineTracksFor = null;
	}
}
