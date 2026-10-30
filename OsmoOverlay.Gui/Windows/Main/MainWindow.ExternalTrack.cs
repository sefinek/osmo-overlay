using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;
using OsmoOverlay.Gui.Native;

namespace OsmoOverlay.Gui;

public partial class MainWindow
{
	private (string Path, ExternalTrack Track)? _externalTrackDetails;
	private ExternalTrackSource? _externalTrack;
	private FileSummary? _cameraSummary;
	private bool _externalTrackApplied;
	private ExternalTrackPlayback? _fullTelemetry;
	private TimeSpan _fullTelemetryPosition;
	private int _fullTelemetryGeneration;
	private bool _trackOnly;
	private TrackOnlyFormat _trackOnlyFormat = TrackOnlyFormat.Default;
	private bool CanRenderTelemetry => !_trackOnly && _summary?.HasTelemetry == true && (_externalTrack is null || _externalTrackApplied);

	private async void OnExternalTrackClick(object? sender, RoutedEventArgs e)
	{
		if (_phase != UiPhase.SummaryReady || _cameraSummary is not { HasTelemetry: true } camera) return;
		_previewPlayer.Pause();
		bool timelineWasOpen = TimelineCard.IsVisible;
		SetTimelineExpanded(false, false);
		var editor = new ExternalTrackWindow(camera, _externalTrack, _previewPosition.TotalSeconds);
		editor.Log += (message, level) => AppendLog(message, level);
		await editor.ShowDialog(this);
		if (!editor.Accepted)
		{
			SetTimelineExpanded(timelineWasOpen, false);
			return;
		}

		OverlayProject edits = BuildProject();
		SetPhase(UiPhase.LoadingSummary);
		if (editor.Source is { } adding) AppendLog(string.Format(Strings.Track_Applying, Path.GetFileName(adding.Path)));
		try
		{
			ExternalTrack? loadedTrack = editor.LoadedTrack;
			FileSummary updated = editor.Source is { } source
				? await Task.Run(() => loadedTrack is not null ? source.Apply(camera, loadedTrack) : source.Apply(camera)) : camera;
			_externalTrack = editor.Source;
			_externalTrackDetails = editor.Source is { } applied && loadedTrack is not null ? (applied.Path, loadedTrack) : null;
			_externalTrackApplied = true;
			_summary = updated;
			_availability = OverlayAvailability.Of(updated.TelemetryFrames!, updated.ContainerRecordingStartUtc is not null, updated.CameraFormat);
			_mapSourcesForRoute = null;
			await Task.Run(() => RecordingSpeeds(updated));
			PopulateTelemetryInfo(updated);
			PopulateInputInfo(updated);
			UpdateTelemetryStatus(updated);
			SetPhase(UiPhase.SummaryReady);
			await OpenPreviewAsync(updated);
			ApplyProjectEdits(edits);
			ResetHistory();
			RefreshExternalTrack();
			AppendLog(_externalTrack is null ? Strings.Track_Removed : string.Format(Strings.Track_Applied, Path.GetFileName(_externalTrack.Path)));
		}
		catch (Exception ex)
		{
			AppLogger.Error(ex, ex.Message);
			await ConfirmDialog.ShowAsync(this, Strings.Track_Title, ex.Message, kind: DialogKind.Warning);
		}
		finally
		{
			SetPhase(UiPhase.SummaryReady);
			SetRenderButtons(CanRenderTelemetry);
		}
	}

	private void ClearExternalTrack()
	{
		_externalTrack = null;
		_externalTrackDetails = null;
		_cameraSummary = null;
		_externalTrackApplied = false;
		RefreshExternalTrack();
	}

	private void RefreshExternalTrack()
	{
		FullTelemetryButton.IsVisible = FullTelemetryContext() is not null;
		FullTelemetryButton.IsEnabled = _phase == UiPhase.SummaryReady && !_previewTestRunning;
		ExternalTrackStatus.Text = _externalTrack is { } source ? Path.GetFileName(source.Path) : Strings.Track_None;
		ExternalTrackButton.IsEnabled = _phase == UiPhase.SummaryReady && _cameraSummary?.HasTelemetry == true && !_previewTestRunning;
		ExternalTrackInfo.IsVisible = false;
		if (_externalTrack is { } shown && _externalTrackDetails is { } details && details.Path == shown.Path)
			ShowExternalTrackInfo(details.Track);
	}

	private (ExternalTrack Track, TrackSynchronization Sync)? FullTelemetryContext()
	{
		if (!_externalTrackApplied || _externalTrack is not { } source || _externalTrackDetails is not { } details || details.Path != source.Path ||
		    _summary is not { TelemetryFrames.Count: > 0 } summary || summary.Video.Fps <= 0 || summary.TelemetryFrames[0].GpsTimestamp is not { } utc)
		{
			return null;
		}
		TelemetryFrame first = summary.TelemetryFrames[0];
		TrackSynchronization sync = source.Synchronization ?? new TrackSynchronization(new TrackSyncAnchor(first.SampleTimeSeconds, utc));
		double duration = summary.TotalFrameCount / summary.Video.Fps;
		return ExternalTrackPreview.ExtendsRecording(details.Track, sync, duration) ? (details.Track, sync) : null;
	}

	private async void OnFullTelemetryClick(object? sender, RoutedEventArgs e)
	{
		if (_fullTelemetry is not null) { CloseFullTelemetry(); return; }
		if (_phase != UiPhase.SummaryReady || FullTelemetryContext() is not { } context || _summary is not { } summary) return;
		int generation = ++_fullTelemetryGeneration;
		_previewPlayer.Pause();
		OverlaySettings settings = OverlaySettingsStore.Load();
		try
		{
			(ExternalTrackPlayback playback, IReadOnlyList<GpsAnomaly> anomalies, IReadOnlyList<(double Start, double End)> lossRanges) = await Task.Run(() =>
			{
				ExternalTrackPlayback prepared = ExternalTrackPreview.Prepare(context.Track, context.Sync, summary.TotalFrameCount / summary.Video.Fps, settings);
				return (prepared, (IReadOnlyList<GpsAnomaly>)TelemetryProcessor.FindGpsAnomalies(prepared.RawFrames),
					(IReadOnlyList<(double Start, double End)>)TelemetryProcessor.FindGpsLossRanges(prepared.RawFrames));
			});
			if (generation != _fullTelemetryGeneration || _summary != summary || _phase != UiPhase.SummaryReady) return;
			TimeSpan position = TimeSpan.FromSeconds(Math.Clamp((context.Sync.At(_previewPosition.TotalSeconds) - context.Track.StartUtc).TotalSeconds, 0, playback.DurationSeconds));
			await ShowFullTelemetryAsync(summary, playback, anomalies, lossRanges, position);
		}
		catch (Exception ex)
		{
			if (generation != _fullTelemetryGeneration) return;
			AppLogger.Error(ex, "Full telemetry preview failed");
			AppendLog(string.Format(Strings.FullTelemetry_Error, ex.Message), LogLevel.Error);
			CloseFullTelemetry();
		}
	}

	private async Task ShowFullTelemetryAsync(FileSummary summary, ExternalTrackPlayback playback, IReadOnlyList<GpsAnomaly> anomalies,
		IReadOnlyList<(double Start, double End)> lossRanges, TimeSpan position)
	{
		_fullTelemetry = playback;
		_fullTelemetryPosition = position;
		FullTelemetryButton.Classes.Set("active", true);
		CutScrim.IsVisible = false;
		foreach (PreviewTimeline timeline in Timelines)
		{
			timeline.Cuts = [];
			timeline.Moments = [];
			timeline.Volume = [];
			timeline.Maximum = playback.DurationSeconds;
			timeline.RecordingTimeAt = playback.VideoPosition;
			timeline.RouteIntro = null;
			timeline.Selection = null;
			timeline.IsReadOnly = true;
			timeline.Highlight = ExternalTrackPreview.RecordingRange(playback.Track, playback.Synchronization, playback.RecordingSeconds);
			timeline.Files = timeline.Highlight is { } range ? [new TimelineFile(range.StartSeconds, range.EndSeconds, Strings.FullTelemetry_Recording, false)] : [];
			timeline.FlightEvents = [.. playback.Track.Events.Select(e => new TimelineFlightEvent((e.Utc - playback.Track.StartUtc).TotalSeconds, e.Event))];
		}
		ShowGpsMarks(anomalies, lossRanges);
		RefreshLayers();
		await OpenPreviewPlayerAsync(summary, position, trackPlayback: playback);
	}

	private void ReturnToRecordingPreview()
	{
		if (_fullTelemetry is not { } playback || _trackOnly) return;
		double seconds = ExternalTrackPreview.RecordingPosition(playback.Track, playback.Synchronization, _fullTelemetryPosition.TotalSeconds);
		_previewPosition = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, playback.RecordingSeconds - 1 / _summary!.Video.Fps)));
		_previewFrame = FrameAt(_previewPosition.TotalSeconds);
		CloseFullTelemetry();
	}

	private void CloseFullTelemetry() => CloseFullTelemetry(true);

	private void CloseFullTelemetry(bool restorePreview)
	{
		_fullTelemetryGeneration++;
		if (_fullTelemetry is not { } closing) return;
		_fullTelemetry = null;
		_previewPlayer.Close();
		closing.Backdrop?.Dispose();
		FullTelemetryButton.Classes.Set("active", false);
		_suppressTimelineEvent = true;
		foreach (PreviewTimeline timeline in Timelines)
		{
			timeline.RecordingTimeAt = null;
			timeline.Highlight = null;
			timeline.IsReadOnly = false;
			timeline.Maximum = _summary is { } recording ? recording.TotalFrameCount / recording.Video.Fps : 1;
			timeline.Value = _previewPosition.TotalSeconds;
		}
		_suppressTimelineEvent = false;
		if (restorePreview && _phase == UiPhase.SummaryReady && _summary is { } summary)
			_ = OpenPreviewAsync(summary, previewPosition: _previewPosition);
	}

	private async void OnOpenTrackOnlyClick(object? sender, RoutedEventArgs e)
	{
		if (_phase is UiPhase.LoadingSummary or UiPhase.Rendering || _previewTestRunning) return;
		if (await TrackFormatWindow.AskAsync(this, _trackOnlyFormat) is not { } format) return;

		_trackOnlyFormat = format;
		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenRememberedAsync("gps-track", new FilePickerOpenOptions
		{
			Title = Strings.Track_Title,
			FileTypeFilter = [new FilePickerFileType(Strings.Track_FileType) { Patterns = [.. ExternalTrackReader.Extensions.SelectMany(x => new[] { "*" + x, "*" + x.ToUpperInvariant() })] }]
		});
		if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

		await OpenTrackOnlyAsync(path, format);
	}

	/// <summary>A GPS track on its own (TrackOnlyRecording) in the full telemetry preview - it only plays, so rendering stays off.</summary>
	private async Task OpenTrackOnlyAsync(string path, TrackOnlyFormat format)
	{
		if (_phase is UiPhase.LoadingSummary or UiPhase.Rendering) return;

		ClosePreview();
		ResetReframe();
		ClearCuts();
		SetVolumeLine([]);
		ResetMusic();
		ClearMoments();
		ReleaseTimelineTracks();
		_inputPaths.Clear();
		RefreshInputFilesList();
		OutputPathBox.Text = "";
		ClearExternalTrack();
		SetPhase(UiPhase.LoadingSummary);
		AppendLog(string.Format(Strings.TrackOnly_Loading, Path.GetFileName(path)));
		ExternalTrackPlayback? opening = null;
		try
		{
			OverlaySettings settings = OverlaySettingsStore.Load();
			(FileSummary summary, ExternalTrackPlayback playback, IReadOnlyList<GpsAnomaly> anomalies, IReadOnlyList<(double Start, double End)> lossRanges) =
				await Task.Run(() =>
				{
					(FileSummary created, ExternalTrackPlayback prepared) = TrackOnlyRecording.Create(ExternalTrackReader.Read(path), settings, format);
					return (created, prepared, (IReadOnlyList<GpsAnomaly>)TelemetryProcessor.FindGpsAnomalies(prepared.RawFrames),
						(IReadOnlyList<(double Start, double End)>)TelemetryProcessor.FindGpsLossRanges(prepared.RawFrames));
				});

			playback = opening = playback with { Backdrop = await TrackBackdrop.BuildAsync(playback.DerivedFrames, settings, format) };
			_summary = summary;
			_trackOnly = true;
			_cleanFingerprint = null;
			_availability = OverlayAvailability.Of(summary.TelemetryFrames!, true, null);
			_mapSourcesForRoute = null;
			_measuredSpeeds = null;
			await Task.Run(() => RecordingSpeeds(summary));
			PopulateTelemetryInfo(summary);
			SetPhase(UiPhase.SummaryReady);
			SetRenderButtons(false);
			LoadOverlayPresets();
			ShowTrackMapSettings();
			PreviewPlaceholder.IsVisible = false;
			TransportPanel.IsEnabled = true;
			foreach (PreviewTimeline timeline in Timelines) timeline.IsEnabled = true;
			await ShowFullTelemetryAsync(summary, playback, anomalies, lossRanges, TimeSpan.Zero);
			UpdatePreviewStatus();
			AppendLog(string.Format(Strings.TrackOnly_Opened, Path.GetFileName(path)));
		}
		catch (Exception ex)
		{
			AppLogger.Error(ex, ex.Message);
			AppendLog(string.Format(Strings.TrackOnly_Failed, ex.Message), LogLevel.Error);
			ClosePreview();
			if (_fullTelemetry != opening) opening?.Backdrop?.Dispose();
			SetPhase(UiPhase.Idle);
			ActionButton.IsEnabled = false;
			await ConfirmDialog.ShowAsync(this, Strings.Track_Title, string.Format(Strings.TrackOnly_Failed, ex.Message), kind: DialogKind.Warning);
		}
		finally
		{
			TaskbarProgress.SetState(this, TaskbarProgress.State.NoProgress);
		}
	}

	private void ShowExternalTrackInfo(ExternalTrack track)
	{
		TimeSpan length = track.EndUtc - track.StartUtc;
		string text = string.Format(Strings.Track_Summary,
			length.ToString(length.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture), track.Points.Count);
		if (track.DeviceName is { } name) text = $"{name} · {text}";
		ExternalTrackInfo.Text = text;
		ToolTip.SetTip(ExternalTrackInfo, text);
		ExternalTrackInfo.IsVisible = true;
	}
}
