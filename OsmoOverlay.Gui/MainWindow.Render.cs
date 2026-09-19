using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Gui.Native;
using RenderOptions = OsmoOverlay.Core.RenderOptions;

namespace OsmoOverlay.Gui;

/// <summary>Running the actual render (RenderJob), progress reporting, and the completion dialog/balloon.</summary>
public partial class MainWindow
{
	private async Task RunRenderAsync()
	{
		var outputPath = OutputPathBox.Text ?? "";

		if (string.IsNullOrWhiteSpace(outputPath))
		{
			AppendLog("Enter an output file path");
			return;
		}

		var frameLimit = _frameLimit;

		_cts = new CancellationTokenSource();
		SetPhase(UiPhase.Rendering);
		LogBox.ClearLog();
		AppendBanner();
		Progress.Value = 0;
		TaskbarProgress.SetState(this, TaskbarProgress.State.Normal);
		OutMeasuredPanel.IsVisible = false;
		OutPlanText.IsVisible = true;
		OutFrames.IsVisible = true;

		var progress = new Progress<RenderStatus>(OnProgress);
		IReadOnlyList<OverlayElement>? layout = _overlayPresets.Count > 0 ? ActiveElements : null;
		var options = new RenderOptions(_inputPaths, outputPath, frameLimit, _detectedEncoder, _summary?.TelemetryFrames,
			Layout: layout, ShowWatermark: _showWatermark, SmoothGpsMotion: _smoothGpsMotion, CameraModel: _summary?.CameraModel);

		AppendLog($"Output: {outputPath}");
		AppendLog($"Encoder: {_detectedEncoder}, frame limit: {(frameLimit is { } fl ? fl.ToString() : "none")}");
		var presetName = _overlayPresets.FirstOrDefault(p => p.Id == _activePresetId)?.Name;
		AppendLog($"Overlay preset: {presetName ?? "default (none loaded)"}");
		AppendLog($"CLI equivalent: {BuildCliCommand(outputPath, frameLimit)}");

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

			if (_summary is not null) PopulateMeasuredOutputInfo(outputPath, _summary);

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
			AppendLog($"Error: {result.ErrorMessage}", LogLevel.Error);
			// Left set (not cleared) rather than immediately reset to NoProgress - the red taskbar
			// overlay stays as a persistent "this needs attention" flag until the next Get Summary/
			// Render click resets it, since there's no other natural moment to clear it.
			TaskbarProgress.SetState(this, TaskbarProgress.State.Error);
			await NotifyRenderFinishedAsync("Render failed", result.ErrorMessage ?? "Unknown error", DialogKind.Danger);
		}

		SetPhase(UiPhase.SummaryReady);
		ActionButton.IsEnabled = true;
	}

	private string BuildCliCommand(string outputPath, int? frameLimit)
	{
		var parts = new List<string> { "OsmoOverlay.Cli" };
		parts.AddRange(_inputPaths.Select(QuoteArg));
		parts.Add("-o");
		parts.Add(QuoteArg(outputPath));
		if (frameLimit is { } limit)
		{
			parts.Add("--frames");
			parts.Add(limit.ToString());
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
