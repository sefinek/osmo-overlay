using Avalonia.Platform.Storage;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry.External;
using OsmoOverlay.Gui.Native;

namespace OsmoOverlay.Gui;

/// <summary>Rendering a GPS track viewed with no recording (TrackRenderJob): the Render button there. No cuts, range, green screen or queue.</summary>
public partial class MainWindow
{
	private async Task RunTrackRenderAsync()
	{
		if (_fullTelemetry is not { } playback || _trackOnlyPath is not { } source) return;

		if (QueueBusy)
		{
			await ConfirmDialog.ShowAsync(this, Strings.Queue_Title, Strings.Queue_Busy, kind: DialogKind.Warning);
			return;
		}

		if (!await ConfirmExportRightsAsync(CurrentExportMaps)) return;

		IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = Strings.TrackRender_SaveTitle,
			SuggestedFileName = Path.GetFileNameWithoutExtension(source) + "_overlay.mp4",
			SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(source) ?? "."),
			DefaultExtension = "mp4",
			FileTypeChoices = [new FilePickerFileType(Strings.Common_Mp4Video) { Patterns = ["*.mp4"] }]
		});
		if (file?.TryGetLocalPath() is not { } outputPath) return;
		if (!await ConfirmComputerNotBusyAsync(_detectedEncoder)) return;

		_cts = new CancellationTokenSource();
		SetPhase(UiPhase.Rendering);
		ClearLogPanels();
		AppendBanner();
		Progress.Value = 0;
		TaskbarProgress.SetState(this, TaskbarProgress.State.Normal);

		IReadOnlyList<OverlayElement>? layout = _overlayPresets.Count > 0 ? OverlayLayers.Drawn(ActiveElements, ActiveLayers) : null;
		List<MusicClip> music = [.. _music.Where(c => File.Exists(c.Path))];
		foreach (MusicClip missing in _music.Where(c => !File.Exists(c.Path)))
			AppLogger.Warn(string.Format(Strings.Render_MusicMissing, missing.Path));
		var options = new TrackRenderOptions(playback.Track, _trackOnlyFormat, outputPath, _detectedEncoder, Layout: layout, Music: music, CutOuts: CutOutsForRender(),
			Playback: playback);
		AppendLog(string.Format(Strings.Render_LogOutput, outputPath));

		CancellationTokenSource cts = _cts;
		RenderResult result;
		try
		{
			result = await TrackRenderJob.RunAsync(options, new Progress<RenderStatus>(OnProgress), cts.Token);
		}
		finally
		{
			_cts = null;
		}

		if (result.Success)
		{
			Progress.Value = 100;
			string elapsedText = result.Elapsed.ToString(@"hh\:mm\:ss");
			string sizeText = File.Exists(outputPath) ? FormatHelper.FormatBytes(new FileInfo(outputPath).Length) : Strings.Common_Unknown;
			AppendLog(string.Format(Strings.Render_LogDone, outputPath, elapsedText, sizeText));
			TaskbarProgress.SetState(this, TaskbarProgress.State.NoProgress);
			await NotifyRenderFinishedAsync(Strings.Render_CompleteTitle, Path.GetFileName(outputPath) + "\n" + string.Format(Strings.Render_DoneSummary, elapsedText, sizeText),
				DialogKind.Success, outputPath);
		}
		else if (cts.Token.IsCancellationRequested)
		{
			AppendLog(string.Format(Strings.Render_LogCancelled, result.ErrorMessage));
			TaskbarProgress.SetState(this, TaskbarProgress.State.NoProgress);
		}
		else
		{
			TaskbarProgress.SetState(this, TaskbarProgress.State.Error);
			await NotifyRenderFinishedAsync(Strings.Render_FailedTitle, result.ErrorMessage ?? Strings.Render_UnknownError, DialogKind.Danger);
		}

		SetPhase(UiPhase.SummaryReady);
		SetRenderButtons(true);
	}
}
