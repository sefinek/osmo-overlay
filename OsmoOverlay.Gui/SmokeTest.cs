using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Preview;
using SkiaSharp;

namespace OsmoOverlay.Gui;

/// <summary>
///     `--smoke-test` (CI): the main window opens, then the things that only fail on a machine - the native libraries
///     (SkiaSharp, FFmpeg's shared libraries), the required tools, the registered cameras - are tried, and the app exits
///     with 0 or 1. The welcome, dependency and update prompts are skipped, so nothing waits for a click.
/// </summary>
internal static class SmokeTest
{
	public const string Flag = "--smoke-test";
	private const int TimeoutExitCode = 2;
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

	public static bool Enabled { get; private set; }

	public static bool IsRequested(string[] args)
	{
		return args.Contains(Flag);
	}

	/// <summary>Arms the watchdog: a window that never opens would otherwise hang the job until its own timeout.</summary>
	public static void Start()
	{
		Enabled = true;
		var watchdog = new Thread(() =>
		{
			Thread.Sleep(Timeout);
			Console.Error.WriteLine($"Smoke test: the main window didn't open within {Timeout.TotalSeconds:0} s");
			Environment.Exit(TimeoutExitCode);
		}) { IsBackground = true };
		watchdog.Start();
	}

	public static void Complete()
	{
		List<string> failures = Run();
		foreach (string failure in failures) Console.Error.WriteLine($"Smoke test failed: {failure}");
		if (failures.Count == 0) Console.WriteLine("Smoke test passed");

		// Opened fires inside the lifetime's own Start; shutting down there leaves StartCore with a half-built state (NullReferenceException).
		if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
			Dispatcher.UIThread.Post(() => desktop.Shutdown(failures.Count == 0 ? 0 : 1), DispatcherPriority.Background);
	}

	private static List<string> Run()
	{
		List<string> failures = [];

		try
		{
			using var bitmap = new SKBitmap(16, 16);
			using var canvas = new SKCanvas(bitmap);
			canvas.Clear(SKColors.Red);
			if (bitmap.GetPixel(0, 0) != SKColors.Red) failures.Add("SkiaSharp drew a different pixel than it was asked to");
		}
		catch (Exception ex)
		{
			failures.Add($"SkiaSharp: {ex.Message}");
		}

		string[] missing = [.. DependencyChecker.FindMissing(RequiredTools.All).Where(t => !t.IsOptional).Select(t => t.DisplayName)];
		if (missing.Length > 0) failures.Add($"required tools missing: {string.Join(", ", missing)}");

		if (LibavLoader.TryLoad() is { } libavFailure) failures.Add($"FFmpeg libraries: {libavFailure}");

		if (CameraFormats.All.Count == 0) failures.Add("no camera format is registered");

		foreach (string failure in failures) AppLogger.Info($"Smoke test: {failure}");
		return failures;
	}
}
