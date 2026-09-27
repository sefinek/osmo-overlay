using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Preview;
using OsmoOverlay.Core.Reframe;

namespace OsmoOverlay.Gui;

/// <summary>
///     The expanded timeline's filmstrip and waveform (ExpandedTimeline) and the scrollbar under it. The generators belong to the
///     loaded recording, not to the preview: reopening the preview (quality, settings) keeps what they already made.
/// </summary>
public partial class MainWindow
{
	// Thumbnails are made at twice the filmstrip's height, so they stay sharp on a HiDPI screen.
	private const int ThumbnailHeight = 96;

	private TimelineThumbnails? _timelineThumbnails;
	private AudioWaveform? _timelineWaveform;
	private FileSummary? _timelineTracksFor;
	// The user's choice (remembered in OverlaySettings) - shown only while there's a recording to show on it.
	private bool _timelineWanted;

	private void WireTimelineTracks()
	{
		ExpandedTimeline.ViewChanged += SyncTimelineScrollBar;
		Closed += (_, _) => ReleaseTimelineTracks();
		_timelineWanted = OverlaySettingsStore.Load().PreviewTimelineExpanded;
		SetTimelineExpanded(_timelineWanted, false);
	}

	private void OnToggleTimelineClick(object? sender, RoutedEventArgs e)
	{
		SetTimelineExpanded(!TimelineCard.IsVisible, true);
	}

	/// <summary>
	///     The expanded timeline in the log's place under the window - the bottom row then shrinks to its height, leaving
	///     the rest to the preview. The compact one (still the one holding the position, see Timelines) is hidden meanwhile
	///     and the status strip takes its place in the transport row. Without a loaded recording (no file yet, a summary or
	///     a render running, no telemetry so no preview) it would be empty, so the log shows instead.
	/// </summary>
	private void SetTimelineExpanded(bool expanded, bool save)
	{
		if (save)
		{
			_timelineWanted = expanded;
			OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { PreviewTimelineExpanded = expanded });
		}

		expanded &= _phase == UiPhase.SummaryReady && _summary?.HasTelemetry == true;

		TimelineCard.IsVisible = expanded;
		LogCard.IsVisible = !expanded;
		RootGrid.RowDefinitions[1].Height = expanded ? GridLength.Auto : new GridLength(230);
		ToggleTimelineButton.Classes.Set("active", expanded);

		// The expanded timeline takes over seeking, so the compact one makes room for the status strip.
		PreviewTimeline.IsVisible = !expanded;
		Grid.SetRow(PreviewStatusBar, expanded ? 0 : 1);
		Grid.SetColumn(PreviewStatusBar, expanded ? 1 : 0);
		Grid.SetColumnSpan(PreviewStatusBar, expanded ? 1 : 3);
		PreviewStatusBar.Margin = expanded ? default : new Thickness(0, 8, 0, 0);
		SyncTimelineScrollBar();
	}

	private void OnTimelineScroll(object? sender, ScrollEventArgs e)
	{
		ExpandedTimeline.ScrollTo(e.NewValue);
	}

	private void SyncTimelineScrollBar()
	{
		var duration = ExpandedTimeline.Maximum - ExpandedTimeline.Minimum;
		var visible = ExpandedTimeline.ViewLength;
		TimelineScrollBar.IsVisible = duration > 0 && visible < duration - 1e-3;
		TimelineScrollBar.Maximum = Math.Max(0, duration - visible);
		TimelineScrollBar.ViewportSize = visible;
		TimelineScrollBar.LargeChange = visible * 0.9;
		TimelineScrollBar.SmallChange = visible * 0.1;
		TimelineScrollBar.Value = ExpandedTimeline.ViewStart;
	}

	/// <summary>With the preview: starts the generators for a newly loaded recording, or hands the timeline the ones it already has.</summary>
	private async Task LoadTimelineTracksAsync(FileSummary summary)
	{
		var aspect = (double)summary.Video.Width / summary.Video.Height;
		if (ReferenceEquals(_timelineTracksFor, summary))
		{
			ExpandedTimeline.SetSources(_timelineThumbnails, _timelineWaveform, summary.Video.Fps, aspect);
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
				(TimelineThumbnails.Open(segments, summary.Video.Fps, thumbnailWidth, ThumbnailHeight, ReframerFor(summary, _reframe)),
					AudioWaveform.Start(segments)));
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
		ExpandedTimeline.SetSources(thumbnails, waveform, summary.Video.Fps, aspect);
	}

	/// <summary>A 360 recording's filmstrip shows its view - generated again for a new one, the waveform kept.</summary>
	private async Task ReloadTimelineThumbnailsAsync()
	{
		if (_timelineTracksFor is not { Fisheye: not null } summary) return;

		var aspect = (double)summary.Video.Width / summary.Video.Height;
		var thumbnailWidth = (int)Math.Round(ThumbnailHeight * aspect) & ~1;
		List<PlaybackSegment> segments = PlaybackSegment.Of(summary);
		ReframeView view = _reframe;
		Reframer? reframer = ReframerFor(summary, view);
		TimelineThumbnails thumbnails;
		try
		{
			thumbnails = await Task.Run(() => TimelineThumbnails.Open(segments, summary.Video.Fps, thumbnailWidth, ThumbnailHeight, reframer));
		}
		catch (InvalidOperationException ex)
		{
			AppLogger.Warn(ex, "Timeline thumbnails unavailable");
			return;
		}

		// Another recording, or a newer view, took over meanwhile.
		if (!ReferenceEquals(_timelineTracksFor, summary) || view != _reframe)
		{
			thumbnails.Dispose();
			return;
		}

		TimelineThumbnails? previous = _timelineThumbnails;
		_timelineThumbnails = thumbnails;
		ExpandedTimeline.SetSources(thumbnails, _timelineWaveform, summary.Video.Fps, aspect);
		previous?.Dispose();
	}

	private void ReleaseTimelineTracks()
	{
		ExpandedTimeline.SetSources(null, null, 0, 0);
		_timelineThumbnails?.Dispose();
		_timelineWaveform?.Dispose();
		_timelineThumbnails = null;
		_timelineWaveform = null;
		_timelineTracksFor = null;
	}
}
