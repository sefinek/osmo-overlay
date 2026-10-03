using System.Globalization;
using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Gui.Native;
using RenderOptions = OsmoOverlay.Core.RenderOptions;

namespace OsmoOverlay.Gui;

/// <summary>Running the actual render (RenderJob), progress reporting, and the completion dialog/balloon.</summary>
public partial class MainWindow
{
	private async Task RunRenderAsync(bool greenScreen)
	{
		string normalOutputPath = OutputPathBox.Text ?? "";

		if (string.IsNullOrWhiteSpace(normalOutputPath))
		{
			AppendLog(Strings.Render_EnterOutputPath);
			return;
		}

		// Derived from whatever's in OutputPathBox (respects a location the user picked via "..."),
		// not a second independent path the user has to manage themselves - this render is a
		// compositing asset, not an alternative final output, so it shouldn't need its own UI.
		string outputPath = greenScreen ? RenderOptions.GreenScreenOutputPath(normalOutputPath) : normalOutputPath;
		if (!await ConfirmDiskSpaceAsync(outputPath)) return;
		if (!await ConfirmComputerNotBusyAsync()) return;

		_cts = new CancellationTokenSource();
		SetPhase(UiPhase.Rendering);
		ClearLogPanels();
		AppendBanner();
		Progress.Value = 0;
		TaskbarProgress.SetState(this, TaskbarProgress.State.Normal);
		OutMeasuredPanel.IsVisible = false;
		OutPlanText.IsVisible = true;
		SetPlannedFramesVisible(true);

		// The summary's own paths, not the live list - its TelemetryFrames were stitched from exactly these.
		IReadOnlyList<string> inputPaths = _summary?.InputPaths ?? [.. _inputPaths];
		var progress = new Progress<RenderStatus>(OnProgress);
		// As the preview shows it - muted and unsoloed layers left out.
		IReadOnlyList<OverlayElement>? layout = _overlayPresets.Count > 0 ? OverlayLayers.Drawn(ActiveElements, ActiveLayers) : null;
		List<TimeRange>? cutOuts = CutOutsForRender();
		var options = new RenderOptions(inputPaths, outputPath, null, _detectedEncoder, _summary?.TelemetryFrames,
			Layout: layout, ShowWatermark: _showWatermark, SmoothGpsMotion: _smoothGpsMotion, CameraModel: _summary?.CameraModel,
			GreenScreen: greenScreen, CutOuts: cutOuts, Reframe: _reframe);

		AppendLog(greenScreen ? Strings.Render_ModeGreenScreen : Strings.Render_ModeNormal);
		AppendLog(string.Format(Strings.Render_LogOutput, outputPath));
		AppendLog(string.Format(Strings.Render_LogEncoder, _detectedEncoder, HasCuts ? DescribeCuts() : Strings.Render_NoCuts));
		string? presetName = _overlayPresets.FirstOrDefault(p => p.Id == _activePresetId)?.Name;
		AppendLog(string.Format(Strings.Render_LogPreset, presetName ?? Strings.Render_NoPreset));
		// No CLI equivalent shown for green screen - the CLI doesn't have a flag for this mode yet.
		if (!greenScreen)
			AppendLog(string.Format(Strings.Render_LogCli, BuildCliCommand(inputPaths, outputPath, cutOuts ?? [], Is360 ? _reframe : null)));

		CancellationTokenSource cts = _cts;
		RenderResult result;
		try
		{
			result = await RenderJob.RunAsync(options, progress, cts.Token);
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

			// "Matches the source" doesn't mean anything for a green-screen render - there's no source
			// video in it to match (synthetic background, no audio) - so the Output card keeps showing
			// whatever it already had (the plan, or an earlier normal render's measured results).
			if (!greenScreen && _summary is not null) await PopulateMeasuredOutputInfoAsync(outputPath, _summary);

			TaskbarProgress.SetState(this, TaskbarProgress.State.NoProgress);

			// User-facing surfaces (dialog, balloon) show just the filename - the full path is only
			// useful for the log line above, where it's there to be pasted/searched, not read at a glance.
			string summary = Path.GetFileName(outputPath) + "\n" + string.Format(Strings.Render_DoneSummary, elapsedText, sizeText);
			await NotifyRenderFinishedAsync(Strings.Render_CompleteTitle, summary, DialogKind.Success, outputPath);
		}
		else if (cts.Token.IsCancellationRequested)
		{
			// The user asked for this via the Cancel button - not a failure, so no red taskbar flag
			// and no "render failed" dialog/balloon telling them something they already know.
			AppendLog(string.Format(Strings.Render_LogCancelled, result.ErrorMessage));
			TaskbarProgress.SetState(this, TaskbarProgress.State.NoProgress);
		}
		else
		{
			// No AppendLog here - RenderJob already logged this failure through AppLogger.Error, which
			// AppLogger.Notified mirrors into this same panel (see MainWindow.axaml.cs's subscriber).
			// Left set (not cleared) rather than immediately reset to NoProgress - the red taskbar
			// overlay stays as a persistent "this needs attention" flag until the next Get Summary/
			// Render click resets it, since there's no other natural moment to clear it.
			TaskbarProgress.SetState(this, TaskbarProgress.State.Error);
			await NotifyRenderFinishedAsync(Strings.Render_FailedTitle, result.ErrorMessage ?? Strings.Render_UnknownError, DialogKind.Danger);
		}

		SetPhase(UiPhase.SummaryReady);
		ActionButton.IsEnabled = true;
		GreenScreenButton.IsEnabled = true;
	}

	private static string BuildCliCommand(IReadOnlyList<string> inputPaths, string outputPath, IReadOnlyList<TimeRange> cutOuts,
		ReframeView? view)
	{
		var parts = new List<string> { "OsmoOverlay.Cli" };
		parts.AddRange(inputPaths.Select(QuoteArg));
		parts.Add("-o");
		parts.Add(QuoteArg(outputPath));
		if (view is not null)
		{
			parts.AddRange(["--view", view.ToArgument()]);
			if (!view.Level) parts.Add("--no-level");
		}

		foreach (TimeRange cut in cutOuts)
		{
			parts.AddRange([
				"--cut", $"{cut.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture)}-{cut.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture)}" +
				         (cut.Transition is { } transition ? $"@{transition.ToArgument()}" : "")
			]);
		}

		return string.Join(' ', parts);
	}

	private static string QuoteArg(string value)
	{
		return value.Contains(' ') ? $"\"{value}\"" : value;
	}

	private void OnCancelClick(object? sender, RoutedEventArgs e)
	{
		_cts?.Cancel();
	}

	private void OnProgress(RenderStatus status)
	{
		AppendLog(status.Message);

		if (status.TotalFrames > 0)
		{
			Progress.Value = 100.0 * status.CurrentFrame / status.TotalFrames;
			TaskbarProgress.SetValue(this, (ulong)status.CurrentFrame, (ulong)status.TotalFrames);
		}
	}

	/// <summary>
	///     A render finishing always gets the app's own dialog (so there's a clear, unambiguous answer
	///     to what the user just clicked, whether or not the window is currently focused) and always
	///     plays the OS notification sound. The taskbar balloon is additive, not a replacement: it only
	///     appears when the window is unfocused, since that's the one case where the dialog - fully
	///     shown, just not the visible surface right now - could otherwise go unnoticed until the user
	///     switches back to it.
	/// </summary>
	/// <summary>
	///     Before a render: whether the output's drive has room for it (RenderDiskSpace). Too little asks first, and a
	///     balloon carries the warning when the window isn't in front. True to go on, also when the space can't be read.
	/// </summary>
	private async Task<bool> ConfirmDiskSpaceAsync(string outputPath)
	{
		if (_summary is null) return true;

		OverlaySettings settings = OverlaySettingsStore.Load();
		VideoInfo source = _summary.Video;
		double bitrateScale = source.BitRate > 0
			? (double)OutputVideo.For(source, settings).BitRate / source.BitRate * settings.OutputBitrateMultiplier
			: 1;
		long estimate = RenderDiskSpace.EstimateOutputBytes(_summary.FileSizeBytes, SourceFrames, PlannedFrameCount(), bitrateScale);
		long required = RenderDiskSpace.RequiredBytes(estimate);
		if (estimate <= 0 || RenderDiskSpace.AvailableBytes(outputPath) is not { } available || available >= required) return true;

		AppendLog(string.Format(Strings.Render_LogLowDisk, FormatHelper.FormatBytes(required), FormatHelper.FormatBytes(available)), LogLevel.Warn);
		return await AskLowDiskSpaceAsync(outputPath, estimate, required, available);
	}

	/// <summary>
	///     Before a render: whether something else already keeps its encoder busy - the GPU and its video encoder for NVENC (a
	///     game, OBS, GeForce's recording, another render), the CPU for x264/x265 (RenderBenchmark.SampleLoadAsync, ~1 s). A busy
	///     one asks first. The preview stops first, so its own playback isn't what's measured. True to go on.
	/// </summary>
	private async Task<bool> ConfirmComputerNotBusyAsync()
	{
		_previewPlayer.Pause();
		AppendLog(Strings.Render_CheckingLoad);
		BenchmarkLoad load = await RenderBenchmark.SampleLoadAsync(CancellationToken.None);
		bool gpuEncoder = FfmpegPipeline.IsGpuEncoder(_detectedEncoder);
		bool busy = load.SlowsRender(gpuEncoder);
		AppendLog(string.Format(Strings.Render_LogLoad, LoadPercent(load.Cpu), LoadPercent(load.Gpu), LoadPercent(load.Encoder)),
			busy ? LogLevel.Warn : LogLevel.Info);
		return !busy || await AskComputerBusyAsync(load, gpuEncoder);
	}

	internal async Task<bool> AskComputerBusyAsync(BenchmarkLoad load, bool gpuEncoder)
	{
		string message = gpuEncoder
			? string.Format(Strings.Render_GpuBusyMessage, LoadPercent(load.Gpu), LoadPercent(load.Encoder), load.EncoderSessions ?? 0)
			: string.Format(Strings.Render_CpuBusyMessage, LoadPercent(load.Cpu));

		SystemSound.PlayNotification();
		if (!IsActive) BalloonNotifier.Show(this, Strings.Render_BusyTitle, message);

		return await ConfirmDialog.AskAsync(this, Strings.Render_BusyTitle, message, Strings.Render_Anyway, DialogKind.Warning);
	}

	private static string LoadPercent(double? share)
	{
		return share is { } value ? $"{value * 100:0}%" : Strings.Benchmark_Unknown;
	}

	internal async Task<bool> AskLowDiskSpaceAsync(string outputPath, long estimate, long required, long available)
	{
		string title = Strings.Render_LowDiskTitle;
		string drive = Path.GetPathRoot(Path.GetFullPath(outputPath)) ?? outputPath;
		string message = string.Format(Strings.Render_LowDiskMessage, FormatHelper.FormatBytes(estimate), FormatHelper.FormatBytes(required),
			FormatHelper.FormatBytes(available), drive);

		SystemSound.PlayNotification();
		if (!IsActive)
			BalloonNotifier.Show(this, title, string.Format(Strings.Render_LowDiskBalloon, FormatHelper.FormatBytes(available), FormatHelper.FormatBytes(required)));

		return await ConfirmDialog.AskAsync(this, title, message, Strings.Render_Anyway, DialogKind.Warning);
	}

	internal async Task NotifyRenderFinishedAsync(string title, string message, DialogKind kind, string? outputPath = null)
	{
		SystemSound.PlayNotification();

		if (!IsActive)
			BalloonNotifier.Show(this, title, message);

		await ConfirmDialog.ShowAsync(this, title, message, kind: kind,
			secondaryText: outputPath is not null ? Strings.Common_ShowInFolder : null,
			onSecondary: outputPath is not null ? () => ExplorerHelper.ShowInFolder(outputPath) : null);
	}
}
