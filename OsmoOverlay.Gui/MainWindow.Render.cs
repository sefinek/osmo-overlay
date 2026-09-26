using System.Globalization;
using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Gui.Native;
using RenderOptions = OsmoOverlay.Core.RenderOptions;

namespace OsmoOverlay.Gui;

/// <summary>Running the actual render (RenderJob), progress reporting, and the completion dialog/balloon.</summary>
public partial class MainWindow
{
	private async Task RunRenderAsync(bool greenScreen)
	{
		var normalOutputPath = OutputPathBox.Text ?? "";

		if (string.IsNullOrWhiteSpace(normalOutputPath))
		{
			AppendLog("Enter an output file path");
			return;
		}

		// Derived from whatever's in OutputPathBox (respects a location the user picked via "..."),
		// not a second independent path the user has to manage themselves - this render is a
		// compositing asset, not an alternative final output, so it shouldn't need its own UI.
		var outputPath = greenScreen ? RenderOptions.GreenScreenOutputPath(normalOutputPath) : normalOutputPath;

		_cts = new CancellationTokenSource();
		SetPhase(UiPhase.Rendering);
		LogBox.ClearLog();
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
			GreenScreen: greenScreen, CutOuts: cutOuts);

		AppendLog(greenScreen ? "Mode: green screen (HUD only, solid background, no audio)" : "Mode: normal");
		AppendLog($"Output: {outputPath}");
		AppendLog($"Encoder: {_detectedEncoder}, cuts: {(HasCuts ? DescribeCuts() : "none, whole recording")}");
		var presetName = _overlayPresets.FirstOrDefault(p => p.Id == _activePresetId)?.Name;
		AppendLog($"Overlay preset: {presetName ?? "default (none loaded)"}");
		// No CLI equivalent shown for green screen - the CLI doesn't have a flag for this mode yet.
		if (!greenScreen)
			AppendLog($"CLI equivalent: {BuildCliCommand(inputPaths, outputPath, cutOuts ?? [])}");

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
			var elapsedText = result.Elapsed.ToString(@"hh\:mm\:ss");
			var sizeText = File.Exists(outputPath) ? FormatHelper.FormatBytes(new FileInfo(outputPath).Length) : "unknown";
			AppendLog($"Done: {outputPath} (time: {elapsedText}, {sizeText})");

			// "Matches the source" doesn't mean anything for a green-screen render - there's no source
			// video in it to match (synthetic background, no audio) - so the Output card keeps showing
			// whatever it already had (the plan, or an earlier normal render's measured results).
			if (!greenScreen && _summary is not null) PopulateMeasuredOutputInfo(outputPath, _summary);

			TaskbarProgress.SetState(this, TaskbarProgress.State.NoProgress);

			// User-facing surfaces (dialog, balloon) show just the filename - the full path is only
			// useful for the log line above, where it's there to be pasted/searched, not read at a glance.
			var summary = $"{Path.GetFileName(outputPath)}\nTime: {elapsedText} · Size: {sizeText}";
			await NotifyRenderFinishedAsync("Render complete", summary, DialogKind.Success, outputPath);
		}
		else if (cts.Token.IsCancellationRequested)
		{
			// The user asked for this via the Cancel button - not a failure, so no red taskbar flag
			// and no "render failed" dialog/balloon telling them something they already know.
			AppendLog($"Cancelled: {result.ErrorMessage}");
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
			await NotifyRenderFinishedAsync("Render failed", result.ErrorMessage ?? "Unknown error", DialogKind.Danger);
		}

		SetPhase(UiPhase.SummaryReady);
		ActionButton.IsEnabled = true;
		GreenScreenButton.IsEnabled = true;
	}

	private static string BuildCliCommand(IReadOnlyList<string> inputPaths, string outputPath, IReadOnlyList<TimeRange> cutOuts)
	{
		var parts = new List<string> { "OsmoOverlay.Cli" };
		parts.AddRange(inputPaths.Select(QuoteArg));
		parts.Add("-o");
		parts.Add(QuoteArg(outputPath));
		foreach (TimeRange cut in cutOuts)
			parts.AddRange(["--cut", $"{cut.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture)}-{cut.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture)}"]);

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
	private async Task NotifyRenderFinishedAsync(string title, string message, DialogKind kind, string? outputPath = null)
	{
		SystemSound.PlayNotification();

		if (!IsActive)
			BalloonNotifier.Show(this, title, message);

		await ConfirmDialog.ShowAsync(this, title, message, kind: kind,
			secondaryText: outputPath is not null ? "Show in folder" : null,
			onSecondary: outputPath is not null ? () => ExplorerHelper.ShowInFolder(outputPath) : null);
	}
}
