using Avalonia.Controls;
using Avalonia.Interactivity;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Gui.Native;

namespace OsmoOverlay.Gui;

/// <summary>
///     Ctrl+Shift+D in the main window: every dialog and notification with sample text, to check how they look. Nothing here
///     changes settings or files - the welcome window opened from here doesn't save either (it's the real one, so Finish does).
/// </summary>
public partial class DeveloperWindow : Window
{
	private const string SampleTitle = "Sample title";
	private const string SampleMessage = "This is what a message looks like. It's a sample from the developer tools - nothing happened.";

	private readonly MainWindow? _main;

	public DeveloperWindow()
	{
		InitializeComponent();
	}

	public DeveloperWindow(MainWindow main) : this()
	{
		_main = main;
	}

	private async void OnActionClick(object? sender, RoutedEventArgs e)
	{
		if ((sender as Control)?.Tag is not string action) return;

		string[] parts = action.Split(':', 2);
		string argument = parts.Length > 1 ? parts[1] : "";
		object? result = null;
		switch (parts[0])
		{
			case "ask":
				result = await ConfirmDialog.AskAsync(this, SampleTitle, SampleMessage, "Confirm", Enum.Parse<DialogKind>(argument));
				break;
			case "ask-working":
				result = await ConfirmDialog.AskAsync(this, SampleTitle, "Confirm runs a 2 s task while the dialog stays open.", "Start",
					DialogKind.Info, onConfirm: () => Task.Delay(2000), workingText: "Working...");
				break;
			case "show":
				await ConfirmDialog.ShowAsync(this, SampleTitle, SampleMessage, kind: Enum.Parse<DialogKind>(argument));
				break;
			case "show-actions":
				await ConfirmDialog.ShowAsync(this, SampleTitle, SampleMessage, kind: DialogKind.Success,
					secondaryText: "Secondary", onSecondary: () => SetStatus("Secondary clicked"),
					extraText: "Extra", onExtra: () => SetStatus("Extra clicked"));
				break;
			case "show-long":
				await ConfirmDialog.ShowAsync(this, "A much longer title, to see how it wraps or doesn't",
					string.Join("\n\n", Enumerable.Repeat(SampleMessage + " " + SampleMessage, 4)), kind: DialogKind.Warning);
				break;
			case "render-done" when _main is not null:
				await _main.NotifyRenderFinishedAsync("Render complete", "DJI_20260101_0001_D_overlay.MP4\nTime: 00:04:12 · Size: 1.92 GB",
					DialogKind.Success);
				break;
			case "render-failed" when _main is not null:
				await _main.NotifyRenderFinishedAsync("Render failed", "ffmpeg exited with code 1: sample error message", DialogKind.Danger);
				break;
			case "disk-space" when _main is not null:
				result = await _main.AskLowDiskSpaceAsync(Path.Combine(Path.GetTempPath(), "render.mp4"), 4_200_000_000, 8_400_000_000,
					3_100_000_000);
				break;
			case "no-telemetry" when _main is not null:
				await _main.ShowNoTelemetryNoticeAsync();
				break;
			case "no-gps" when _main is not null:
				await _main.ShowNoGpsNoticeAsync();
				break;
			case "balloon":
				BalloonNotifier.Show(this, SampleTitle, SampleMessage);
				break;
			case "balloon-later":
				SetStatus("The balloon shows in 3 s - switch to another window.");
				await Task.Delay(3000);
				BalloonNotifier.Show(this, SampleTitle, SampleMessage);
				break;
			case "sound":
				SystemSound.PlayNotification();
				break;
			case "log":
				if (argument == "warn") AppLogger.Warn("Sample warning from the developer tools");
				else if (argument == "error") AppLogger.Error("Sample error from the developer tools");
				else AppLogger.Notify("Sample message from the developer tools");
				break;
			case "taskbar" when _main is not null:
				TaskbarProgress.State state = Enum.Parse<TaskbarProgress.State>(argument);
				TaskbarProgress.SetState(_main, state);
				if (state is TaskbarProgress.State.Normal or TaskbarProgress.State.Paused or TaskbarProgress.State.Error)
					TaskbarProgress.SetValue(_main, 40, 100);
				break;
			case "welcome":
				await new WelcomeWindow(true).ShowDialog(this);
				break;
			case "dependencies":
				await new DependencyPromptWindow(RequiredTools.All).ShowDialog(this);
				break;
			default:
				return;
		}

		SetStatus(result is null ? $"Shown: {action}" : $"Shown: {action} - returned {result}");
	}

	private void SetStatus(string text)
	{
		StatusText.Text = text;
	}
}
