using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Preview;
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
			case "busy" when _main is not null:
				result = await _main.AskComputerBusyAsync(argument == "gpu-cpu" ? new BenchmarkLoad(0.82, 0.12, 0, 0) : new BenchmarkLoad(0.63, 0.97, 0.42, 2),
					argument is "gpu" or "gpu-cpu");
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
				AppSound.Play(Enum.Parse<UiSound>(argument));
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
			case "benchmark":
				var benchmark = new BenchmarkWindow();
				var source = new VideoInfo("hevc", "Main 10", 3840, 2160, "60000/1001", "yuv420p10le", "bt709", "bt709", "bt709", "tv", 73_000_000);
				benchmark.ShowSample(new BenchmarkResult(
					new BenchmarkSystem("Microsoft Windows 10.0.26200", "Intel(R) Core(TM) i9-14900HX", 32, 32L << 30,
						["NVIDIA GeForce RTX 4070 Laptop GPU (32.0.15.7714)"], "9.0.2", "1.0.0"),
					new BenchmarkLoad(0.31, 0.64), source, source,
					new BenchmarkStat(142, 139, 147, 3), new BenchmarkStat(118, 112, 121, 3),
					new FrameTimes(16.4, 19.8), new FrameTimes(4.4, 5.1), 1920, 1080, new FrameTimes(4.4, 5.6), new FrameTimes(2.0, 2.4),
					new BenchmarkStat(59, 57, 60, 2), new BenchmarkStat(36, 35, 37, 2),
					[
						new EncoderSpeed("hevc_nvenc", "p1", new BenchmarkStat(121, 118, 124, 2), false),
						new EncoderSpeed("hevc_nvenc", "p4", new BenchmarkStat(114, 99, 120, 2), false),
						new EncoderSpeed("hevc_nvenc", "p7", new BenchmarkStat(46, 45, 47, 2), false),
						new EncoderSpeed("libx265", null, new BenchmarkStat(5, 5, 5, 2), false)
					],
					new BenchmarkStat(31.5, 30.9, 32.0, 2), new BenchmarkStat(26.0, 25.1, 26.4, 2), TimeSpan.FromSeconds(212),
					new SourceSpeeds(source, new BenchmarkStat(131, 124, 138, 3), new BenchmarkStat(41, 40, 43, 3), new BenchmarkStat(29.2, 28.4, 30.1, 2),
						new BenchmarkStat(24.1, 23.8, 24.5, 2))),
				[
					(Strings.Benchmark_PreviewInWindow, SamplePreview(Strings.Benchmark_TestRecording, Strings.Benchmark_ModeTestWindow, 1120, 630, 0, 2)),
					(string.Format(Strings.Benchmark_PreviewInApp, "DJI_20261004123456_0001_D.MP4"),
						SamplePreview("DJI_20261004123456_0001_D.MP4", Strings.PreviewBenchmark_ModeWindow, 1664, 936, 3, 1))
				], 54_000);
				await benchmark.ShowDialog(this);
				break;
			default:
				return;
		}

		SetStatus(result is null ? $"Shown: {action}" : $"Shown: {action} - returned {result}");
	}

	private static PreviewRun SamplePreview(string source, string mode, int shownWidth, int shownHeight, int skipped, int dropped)
	{
		var result = new PreviewMeasurementResult(20.01, 1196, 59.94, new PreviewTiming(1195, 16.68, 17.42), 41.7, 0, skipped, dropped, 2,
			new PreviewTiming(1201, 4.12, 6.85), new PreviewTiming(1199, 3.9, 5.4), new PreviewTiming(1196, 0.61, 1.12), 165.0);
		var context = new PreviewReportContext(source, mode, "DELL U2723QE (2560x1440, 165 Hz)", new PixelSize(1280, 720), shownWidth, shownHeight, 1.25,
			1, "hevc (d3d11va)", true, false);
		return new PreviewRun(result, context, [new BenchmarkLoad(0.24, 0.41, Decoder: 0.18), new BenchmarkLoad(0.27, 0.43, Decoder: 0.19)], 1_412L << 20);
	}

	private void SetStatus(string text)
	{
		StatusText.Text = text;
	}
}
