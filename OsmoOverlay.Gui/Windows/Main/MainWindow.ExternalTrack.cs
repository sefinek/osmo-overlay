using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Gui;

public partial class MainWindow
{
	private ExternalTrackSource? _externalTrack;
	private FileSummary? _cameraSummary;
	private bool _externalTrackApplied;
	private bool CanRenderTelemetry => _summary?.HasTelemetry == true && (_externalTrack is null || _externalTrackApplied);

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
			FileSummary updated = editor.Source is { } source ? await Task.Run(() => source.Apply(camera)) : camera;
			_externalTrack = editor.Source;
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
		_cameraSummary = null;
		_externalTrackApplied = false;
		RefreshExternalTrack();
	}

	private void RefreshExternalTrack()
	{
		ExternalTrackStatus.Text = _externalTrack is { } source ? Path.GetFileName(source.Path) : Strings.Track_None;
		ExternalTrackButton.IsEnabled = _phase == UiPhase.SummaryReady && _cameraSummary?.HasTelemetry == true && !_previewTestRunning;
		ExternalTrackInfo.IsVisible = false;
		if (_externalTrack is { } shown) _ = ShowExternalTrackInfoAsync(shown);
	}

	private async Task ShowExternalTrackInfoAsync(ExternalTrackSource source)
	{
		try
		{
			ExternalTrack track = await Task.Run(() => ExternalTrackReader.Read(source.Path));
			if (!ReferenceEquals(_externalTrack, source)) return;

			TimeSpan length = track.EndUtc - track.StartUtc;
			string text = string.Format(Strings.Track_Summary,
				length.ToString(length.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture), track.Points.Count);
			if (track.DeviceName is { } name) text = $"{name} · {text}";
			ExternalTrackInfo.Text = text;
			ToolTip.SetTip(ExternalTrackInfo, text);
			ExternalTrackInfo.IsVisible = true;
		}
		catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
		{
			AppLogger.Warn(ex, "The external track's details could not be read.");
		}
	}
}
