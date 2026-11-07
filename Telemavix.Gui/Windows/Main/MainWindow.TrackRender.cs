using Telemavix.Core;
using Telemavix.Core.Logging;
using Telemavix.Core.Overlay;
using Telemavix.Gui.Native;

namespace Telemavix.Gui;

/// <summary>Rendering a GPS track viewed with no recording (TrackRenderJob): the Render button there. No cuts, range, green screen or queue.</summary>
public partial class MainWindow
{
	private async Task RunTrackRenderAsync()
	{
		if (_fullTelemetry is not { } playback || _trackOnlyPath is null) return;

		if (QueueBusy)
		{
			await ConfirmDialog.ShowAsync(this, Strings.Queue_Title, Strings.Queue_Busy, kind: DialogKind.Warning);
			return;
		}

		if (!await ConfirmExportRightsAsync(CurrentExportMaps)) return;

		string outputPath = OutputPathBox.Text?.Trim() ?? "";
		if (outputPath.Length == 0)
		{
			AppendLog(Strings.Render_EnterOutputPath);
			return;
		}

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
