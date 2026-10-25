using System.Globalization;
using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry.External;
using OsmoOverlay.Gui.Native;
using RenderOptions = OsmoOverlay.Core.RenderOptions;

namespace OsmoOverlay.Gui;

/// <summary>Running the actual render (RenderJob), progress reporting, and the completion dialog/balloon.</summary>
public partial class MainWindow
{
	/// <summary>
	///     Starts a render once. The checks before it (the drive's space, the computer's load - a second of sampling, or a dialog)
	///     run before the phase becomes Rendering, so a second click in that time started a second render beside the first,
	///     both fetching the same map tiles and writing the same cache files.
	/// </summary>
	/// <param name="range">Only this part of the recording (frames, as the selection on the timeline) - the cuts still apply inside it.</param>
	/// <param name="alpha">The overlay alone on a transparent background in this format, instead of the video.</param>
	private async Task RunRenderAsync(bool greenScreen, FrameRange? range = null, AlphaCodec? alpha = null)
	{
		if (_renderStarting) return;

		_renderStarting = true;
		SetRenderButtons(false);
		try
		{
			await RunRenderCoreAsync(greenScreen, range, alpha);
		}
		finally
		{
			_renderStarting = false;
			if (_phase == UiPhase.SummaryReady) SetRenderButtons(CanRenderTelemetry);
		}
	}

	private async Task RunRenderCoreAsync(bool greenScreen, FrameRange? range, AlphaCodec? alpha)
	{
		if (_phase != UiPhase.SummaryReady || !CanRenderTelemetry) return;

		if (QueueBusy)
		{
			await ConfirmDialog.ShowAsync(this, Strings.Queue_Title, Strings.Queue_Busy, kind: DialogKind.Warning);
			return;
		}

		string normalOutputPath = OutputPathBox.Text ?? "";

		if (string.IsNullOrWhiteSpace(normalOutputPath))
		{
			AppendLog(Strings.Render_EnterOutputPath);
			return;
		}

		// Derived from whatever's in OutputPathBox (respects a location the user picked via "..."),
		// not a second independent path the user has to manage themselves - this render is a
		// compositing asset, not an alternative final output, so it shouldn't need its own UI.
		double fps = _summary?.Video.Fps ?? 0;
		double? rangeStart = range is { } part && fps > 0 ? part.Start / fps : null;
		double? rangeEnd = range is { } part2 && fps > 0 ? part2.End / fps : null;
		string outputPath = alpha is { } alphaFormat ? RenderOptions.AlphaOutputPath(normalOutputPath, alphaFormat)
			: greenScreen ? RenderOptions.GreenScreenOutputPath(normalOutputPath) : normalOutputPath;
		if (rangeStart is { } from && rangeEnd is { } to) outputPath = RenderOptions.RangeOutputPath(outputPath, from, to);
		if (!await ConfirmDiskSpaceAsync(outputPath, alpha)) return;
		if (!await ConfirmComputerNotBusyAsync(alpha is { } cpuFormat ? AlphaOverlay.EncoderName(cpuFormat) : _detectedEncoder)) return;

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
		foreach (MusicClip missing in _music.Where(c => !File.Exists(c.Path)))
			AppLogger.Warn(string.Format(Strings.Render_MusicMissing, missing.Path));
		var options = new RenderOptions(inputPaths, outputPath, null, _detectedEncoder, _summary?.TelemetryFrames,
			Layout: layout, ShowWatermark: _showWatermark, SmoothGpsMotion: _smoothGpsMotion, CameraModel: _summary?.CameraModel,
			GreenScreen: greenScreen, RangeStartSeconds: rangeStart, RangeEndSeconds: rangeEnd, CutOuts: cutOuts, Reframe: _reframe, Volume: [.. _volumeLine],
			Music: [.. _music.Where(c => File.Exists(c.Path))], Alpha: alpha);

		AppendLog(alpha is { } logged ? string.Format(Strings.Render_ModeAlpha, AlphaOverlay.EncoderName(logged))
			: greenScreen ? Strings.Render_ModeGreenScreen : Strings.Render_ModeNormal);
		if (range is { } shown) AppendLog(string.Format(Strings.Render_LogRange, DescribeSelection(shown)));
		AppendLog(string.Format(Strings.Render_LogOutput, outputPath));
		AppendLog(string.Format(Strings.Render_LogEncoder, _detectedEncoder, HasCuts ? DescribeCuts() : Strings.Render_NoCuts));
		string? presetName = _overlayPresets.FirstOrDefault(p => p.Id == _activePresetId)?.Name;
		AppendLog(string.Format(Strings.Render_LogPreset, presetName ?? Strings.Render_NoPreset));
		// No CLI equivalent shown for green screen - the CLI doesn't have a flag for this mode yet.
		if (!greenScreen && alpha is null)
			AppendLog(string.Format(Strings.Render_LogCli, BuildCliCommand(inputPaths, outputPath, cutOuts ?? [], Is360 ? _reframe : null, rangeStart, rangeEnd, _externalTrack,
				[.. _volumeLine], [.. _music.Where(c => File.Exists(c.Path))])));

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
			if (!greenScreen && alpha is null && _summary is not null) await PopulateMeasuredOutputInfoAsync(outputPath, _summary);

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
		SetRenderButtons(true);
	}

	/// <summary>The buttons that start a render (the whole video, a range, the overlay alone) and the one that queues it.</summary>
	private void SetRenderButtons(bool enabled)
	{
		ActionButton.IsEnabled = GreenScreenButton.IsEnabled = RenderRangeButton.IsEnabled = QueueAddButton.IsEnabled = enabled;
	}

	private static string BuildCliCommand(IReadOnlyList<string> inputPaths, string outputPath, IReadOnlyList<TimeRange> cutOuts,
		ReframeView? view, double? rangeStart = null, double? rangeEnd = null, ExternalTrackSource? track = null,
		IReadOnlyList<VolumePoint>? volume = null, IReadOnlyList<MusicClip>? music = null)
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

		if (rangeStart is { } from) parts.AddRange(["--from", from.ToString("0.###", CultureInfo.InvariantCulture)]);
		if (rangeEnd is { } to) parts.AddRange(["--to", to.ToString("0.###", CultureInfo.InvariantCulture)]);
		if (track is not null)
		{
			parts.AddRange(["--track", QuoteArg(track.Path)]);
			if (track.OffsetSeconds is { } offset) parts.AddRange(["--track-offset", offset.ToString("R", CultureInfo.InvariantCulture)]);
			if (track.Synchronization is { } sync)
			{
				parts.AddRange(["--track-anchor", sync.First.ToArgument()]);
				if (sync.Second is { } second) parts.AddRange(["--track-anchor", second.ToArgument()]);
			}
		}

		if (volume is { Count: > 0 }) parts.AddRange(["--volume", QuoteArg(VolumeSpec.Format(volume))]);
		foreach (MusicClip clip in music ?? [])
			parts.AddRange(["--music", QuoteArg(MusicSpec.Format(clip))]);

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
		return value.IndexOfAny([' ', ';', ',', '&', '(', ')']) >= 0 ? $"\"{value}\"" : value;
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
	///     plays its matching app sound. The taskbar balloon is additive, not a replacement: it only
	///     appears when the window is unfocused, since that's the one case where the dialog - fully
	///     shown, just not the visible surface right now - could otherwise go unnoticed until the user
	///     switches back to it.
	/// </summary>
	/// <summary>
	///     Before a render: whether the output's drive has room for it (RenderDiskSpace). Too little asks first, and a
	///     balloon carries the warning when the window isn't in front. True to go on, also when the space can't be read.
	/// </summary>
	private async Task<bool> ConfirmDiskSpaceAsync(string outputPath, AlphaCodec? alpha = null)
	{
		if (_summary is null) return true;

		OverlaySettings settings = OverlaySettingsStore.Load();
		VideoInfo source = _summary.Video;
		double bitrateScale = source.BitRate > 0
			? (double)OutputVideo.For(source, settings).BitRate / source.BitRate * settings.OutputBitrateMultiplier
			: 1;
		VideoInfo written = OutputVideo.For(source, settings);
		long estimate = alpha is { } format
			? AlphaOverlay.EstimateBytes(format, written.Width, written.Height, PlannedFrameCount())
			: RenderDiskSpace.EstimateOutputBytes(_summary.FileSizeBytes, SourceFrames, PlannedFrameCount(), bitrateScale);
		long required = RenderDiskSpace.RequiredBytes(estimate);
		if (estimate <= 0 || RenderDiskSpace.AvailableBytes(outputPath) is not { } available || available >= required) return true;

		AppendLog(string.Format(Strings.Render_LogLowDisk, FormatHelper.FormatBytes(required), FormatHelper.FormatBytes(available)), LogLevel.Warn);
		return await AskLowDiskSpaceAsync(outputPath, estimate, required, available);
	}

	/// <summary>
	///     Before a render: whether something else already keeps it busy - for a GPU encoder its card and video encoder (a game,
	///     OBS, GeForce's or Radeon's recording, another render) and the CPU the overlay is drawn on, for x264/x265 the CPU
	///     (RenderBenchmark.SampleLoadAsync, ~1 s, on the card the encoder runs on). A busy one asks first. The preview stops
	///     first, so its own playback isn't what's measured. True to go on.
	/// </summary>
	private async Task<bool> ConfirmComputerNotBusyAsync(string encoder)
	{
		_previewPlayer.Pause();
		AppendLog(Strings.Render_CheckingLoad);
		BenchmarkLoad load = await RenderBenchmark.SampleLoadAsync(encoder, CancellationToken.None);
		bool gpuEncoder = FfmpegPipeline.IsGpuEncoder(encoder);
		bool busy = load.SlowsRender(gpuEncoder);
		AppendLog(string.Format(Strings.Render_LogLoad, LoadPercent(load.Cpu), LoadPercent(load.Gpu), LoadPercent(load.Encoder)),
			busy ? LogLevel.Warn : LogLevel.Info);
		return !busy || await AskComputerBusyAsync(load, gpuEncoder);
	}

	internal async Task<bool> AskComputerBusyAsync(BenchmarkLoad load, bool gpuEncoder)
	{
		List<string> reasons = [];
		if (gpuEncoder && load.GpuSlowsRender)
			reasons.Add(string.Format(Strings.Render_GpuBusyMessage, LoadPercent(load.Gpu), LoadPercent(load.Encoder), load.EncoderSessions ?? 0));
		if (load.CpuSlowsRender(gpuEncoder) || reasons.Count == 0)
			reasons.Add(string.Format(gpuEncoder ? Strings.Render_CpuBusyGpuEncoderMessage : Strings.Render_CpuBusyMessage, LoadPercent(load.Cpu)));
		string message = string.Join("\n\n", reasons);

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

		if (!IsActive)
			BalloonNotifier.Show(this, title, string.Format(Strings.Render_LowDiskBalloon, FormatHelper.FormatBytes(available), FormatHelper.FormatBytes(required)));

		return await ConfirmDialog.AskAsync(this, title, message, Strings.Render_Anyway, DialogKind.Warning);
	}

	internal async Task NotifyRenderFinishedAsync(string title, string message, DialogKind kind, string? outputPath = null)
	{
		if (!IsActive)
			BalloonNotifier.Show(this, title, message);

		await ConfirmDialog.ShowAsync(this, title, message, kind: kind,
			secondaryText: outputPath is not null ? Strings.Common_ShowInFolder : null,
			onSecondary: outputPath is not null ? () => ExplorerHelper.ShowInFolder(outputPath) : null,
			sound: kind == DialogKind.Success ? UiSound.RenderComplete : null);
	}
}
