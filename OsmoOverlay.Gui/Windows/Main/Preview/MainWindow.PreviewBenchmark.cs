using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Gui;

public partial class MainWindow
{
	private bool _previewBenchmarkRunning;
	private CancellationTokenSource? _previewBenchmarkCancel;

	private async Task ShowPreviewBenchmarkAsync()
	{
		if (_previewBenchmarkRunning) return;
		if (_phase != UiPhase.SummaryReady || _summary is null || _previewFrameSize is null || _previewPlayer.Duration.TotalSeconds < 1)
		{
			await ConfirmDialog.ShowAsync(this, Strings.PreviewBenchmark_Title, Strings.PreviewBenchmark_NotReady);
			return;
		}

		OverlaySettings settings = OverlaySettingsStore.Load();
		bool secondScreen = _fullscreenWindow is null && settings.SecondScreenEnabled && SecondScreenTarget(settings) is not null;
		string source = string.Join(", ", _summary.InputPaths.Select(Path.GetFileName));
		var setup = new PreviewBenchmarkWindow(_fullscreenWindow is not null || settings.PreviewMonitor is not null, secondScreen, source);
		if (!await setup.ShowDialog<bool>(this)) return;

		TimeSpan position = _previewPosition;
		bool wasPlaying = _previewPlayer.IsPlaying;
		bool wasFullscreen = _fullscreenWindow is not null;
		bool hadSecondScreen = _secondScreen is not null;
		bool wasOnSecondScreen = _viewportOnSecondScreen;
		bool loop = _previewPlayer.Loop;
		TimeRange? loopRange = _previewPlayer.LoopRange;
		int originalWidth = _openedPreviewMaxWidth;
		using var cts = new CancellationTokenSource();
		_previewBenchmarkCancel = cts;
		_previewBenchmarkRunning = true;
		var controls = RootGrid.Children.Where(c => c != PreviewBenchmarkStatus).Select(c => (Control: c, c.IsEnabled)).ToArray();
		var windows = new List<Window>();
		var loads = new List<BenchmarkLoad>();
		long peakMemory = 0;
		PreviewMeasurements? measurements = null;
		Task loadTask = Task.CompletedTask;
		string? report = null;
		string? failure = null;
		bool cancelled = false;

		void BlockKeys(object? sender, KeyEventArgs e)
		{
			if (e.Key == Key.Escape) cts.Cancel();
			e.Handled = true;
		}
		void BlockPointer(object? sender, PointerPressedEventArgs e) => e.Handled = true;
		void OnClosing(object? sender, WindowClosingEventArgs e)
		{
			e.Cancel = true;
			_closing = false;
			cts.Cancel();
		}
		void OnStopped() => measurements?.Finish();
		void Watch(Window window)
		{
			windows.Add(window);
			window.AddHandler(KeyDownEvent, BlockKeys, RoutingStrategies.Tunnel);
			if (window != this) window.AddHandler(PointerPressedEvent, BlockPointer, RoutingStrategies.Tunnel);
			window.Closing += OnClosing;
		}

		try
		{
			PausePlayback();
			_autoQualityDelay.Stop();
			foreach (var item in controls) item.Control.IsEnabled = false;
			PreviewBenchmarkStatus.IsVisible = true;
			Watch(this);
			if (wasFullscreen && !setup.UseFullscreen) _fullscreenWindow?.Close();
			if (!wasFullscreen && !secondScreen && setup.UseFullscreen) TogglePreviewFullscreen();
			if (secondScreen && _secondScreen is null) OpenSecondScreen();
			if (_fullscreenWindow is { } fullscreen) Watch(fullscreen);
			if (_secondScreen is { } second) Watch(second);

			// Moving the viewport queues a seek and a layout pass before its compositor can play.
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			if (secondScreen && !_viewportOnSecondScreen) MoveViewportToSecondScreen();
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			if (_previewMaxWidth == AutoPreviewWidth && ResolvePreviewMaxWidth(_summary) != _openedPreviewMaxWidth)
				await ReopenPreviewAsync();
			cts.Token.ThrowIfCancellationRequested();
			if (_previewFrameSize is null) throw new InvalidOperationException(Strings.PreviewBenchmark_NotReady);

			double rate = _previewPlayer.PlaybackRate;
			double warmupSeconds = Math.Min(3, _previewPlayer.Duration.TotalSeconds / rate / 5);
			double start = loop ? position.TotalSeconds : Math.Min(position.TotalSeconds,
				Math.Max(0, _previewPlayer.Duration.TotalSeconds - rate * (20 + warmupSeconds)));
			_previewPlayer.Play(TimeSpan.FromSeconds(start));
			ShowPlayingState(true);
			var warmup = Stopwatch.StartNew();
			while (warmup.Elapsed.TotalSeconds < warmupSeconds && _previewPlayer.IsPlaying)
			{
				PreviewBenchmarkProgress.Text = Strings.PreviewBenchmark_WarmingUp;
				await Task.Delay(100, cts.Token);
			}
			if (!_previewPlayer.IsPlaying) throw new InvalidOperationException(Strings.PreviewBenchmark_EndedDuringWarmup);

			measurements = new PreviewMeasurements(_previewPlayer.PlaybackFrameRate);
			_previewPlayer.Frames.Measurements = measurements;
			_previewPlayer.PlaybackStopped += OnStopped;
			loadTask = SampleLoadAsync(cts.Token);
			var clock = Stopwatch.StartNew();
			while (clock.Elapsed.TotalSeconds < 20 && _previewPlayer.IsPlaying)
			{
				PreviewBenchmarkProgress.Text = string.Format(Strings.PreviewBenchmark_Progress, clock.Elapsed.TotalSeconds.ToString("0"), 20);
				await Task.Delay(100, cts.Token);
			}
			PreviewMeasurementResult result = measurements.Finish();
			string display = _fullscreenWindow is { } full ? DescribeScreen(Screens.ScreenFromWindow(full))
				: _viewportOnSecondScreen ? _secondScreenName : DescribeScreen(Screens.ScreenFromWindow(this));
			string mode = _fullscreenWindow is not null ? Strings.PreviewBenchmark_ModeFullscreen
				: _viewportOnSecondScreen ? Strings.PreviewBenchmark_ModeSecondScreen : Strings.PreviewBenchmark_ModeWindow;
			report = PreviewReport(result, source, mode, display, rate, loads, peakMemory);
		}
		catch (OperationCanceledException) when (cts.IsCancellationRequested) { cancelled = true; }
		catch (Exception ex)
		{
			failure = ex.Message;
			AppLogger.Warn(ex, Strings.PreviewBenchmark_Title);
		}
		finally
		{
			measurements?.Finish();
			_previewPlayer.Frames.Measurements = null;
			_previewPlayer.PlaybackStopped -= OnStopped;
			cts.Cancel();
			await loadTask;
			PausePlayback();
			foreach (Window window in windows)
			{
				window.RemoveHandler(KeyDownEvent, BlockKeys);
				window.RemoveHandler(PointerPressedEvent, BlockPointer);
				window.Closing -= OnClosing;
			}
			try
			{
				if (!wasFullscreen) _fullscreenWindow?.Close();
				else if (_fullscreenWindow is null) TogglePreviewFullscreen();
				if (!wasOnSecondScreen && _viewportOnSecondScreen && _secondScreen is { } second) MoveViewportHome(second);
				if (!hadSecondScreen && _secondScreen is not null) CloseSecondScreen();
				await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
				if (_openedPreviewMaxWidth != originalWidth) await ReopenPreviewAsync();
				_previewPlayer.SetLoop(loop, loopRange);
				ShowPreviewPosition(position);
				_previewPlayer.RequestSeek(position);
			}
			finally
			{
				PreviewBenchmarkStatus.IsVisible = false;
				foreach (var item in controls) item.Control.IsEnabled = item.IsEnabled;
				_previewBenchmarkRunning = false;
				_previewBenchmarkCancel = null;
			}
		}

		if (report is not null) await new PreviewBenchmarkWindow(report).ShowDialog(this);
		else if (failure is not null) await ConfirmDialog.ShowAsync(this, Strings.PreviewBenchmark_Title,
			string.Format(Strings.Benchmark_Failed, failure), kind: DialogKind.Danger);
		else if (cancelled) AppendLog(Strings.PreviewBenchmark_Cancelled);
		if (wasPlaying && !_closing)
		{
			_previewPlayer.Play(position);
			ShowPlayingState(true);
		}

		async Task SampleLoadAsync(CancellationToken token)
		{
			try
			{
				using var monitor = await Task.Run(LoadMonitor.Start, token);
				using var process = Process.GetCurrentProcess();
				while (true)
				{
					await Task.Delay(1000, token);
					BenchmarkLoad load = await Task.Run(() => monitor.ReadAsync(null, true, token), token);
					loads.Add(load);
					process.Refresh();
					peakMemory = Math.Max(peakMemory, process.WorkingSet64);
				}
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested) { }
			catch (Exception ex) { AppLogger.Warn(ex, Strings.PreviewBenchmark_LoadFailed); }
		}
	}

	private string PreviewReport(PreviewMeasurementResult result, string source, string mode, string display, double rate,
		IReadOnlyList<BenchmarkLoad> loads, long peakMemory)
	{
		var text = new StringBuilder();
		text.AppendLine(Strings.PreviewBenchmark_Title);
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Source, source));
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Display, mode, display));
		Window viewportWindow = GetTopLevel(PreviewVideo) as Window ?? this;
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Resolution, _previewFrameSize?.Width, _previewFrameSize?.Height,
			(PreviewVideo.Bounds.Width * viewportWindow.RenderScaling).ToString("0"),
			(PreviewVideo.Bounds.Height * viewportWindow.RenderScaling).ToString("0"), viewportWindow.RenderScaling.ToString("0.##")));
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Rate, rate.ToString("0.##"), result.ExpectedFps.ToString("0.##")));
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Refresh, result.DisplayRefreshHz?.ToString("0.0") ?? Strings.Benchmark_Unknown));
		OverlaySettings settings = OverlaySettingsStore.Load();
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Settings, _previewPlayer.DecoderDescription ?? Strings.Benchmark_Unknown,
			_showOverlay ? Strings.PreviewBenchmark_On : Strings.PreviewBenchmark_Off,
			settings.PreviewDrawsShadows && !settings.DisableShadows ? Strings.PreviewBenchmark_On : Strings.PreviewBenchmark_Off));
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Result, result.PresentedFrames, result.Seconds.ToString("0.00"), result.PresentedFps.ToString("0.00")));
		if (result.Seconds < 19.5) text.AppendLine(Strings.PreviewBenchmark_ShortSample);
		if (result.PresentedFrames == 0) text.AppendLine(Strings.PreviewBenchmark_NoFrames);
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Gaps, result.Intervals.MedianMs.ToString("0.00"),
			result.Intervals.P95Ms.ToString("0.00"), result.LongestGapMs.ToString("0.00"), result.Stalls));
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Dropped, result.DecodeSkipped, result.OverlayDropped, result.DisplayDropped));
		Timing(Strings.Benchmark_StageDecode, result.Decode);
		Timing(Strings.Benchmark_StageOverlay, result.Overlay);
		Timing(Strings.PreviewBenchmark_Draw, result.Draw);
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Load, Average(l => l.Cpu), Average(l => l.Gpu), Average(l => l.Decoder),
			peakMemory > 0 ? FormatHelper.FormatBytes(peakMemory) : Strings.Benchmark_Unknown));
		return text.ToString();

		void Timing(string stage, PreviewTiming timing) => text.AppendLine(string.Format(Strings.PreviewBenchmark_Timing, stage,
			timing.Count > 0 ? timing.MedianMs.ToString("0.00") : Strings.Benchmark_Unknown,
			timing.Count > 0 ? timing.P95Ms.ToString("0.00") : Strings.Benchmark_Unknown));
		string Average(Func<BenchmarkLoad, double?> select)
		{
			double[] values = loads.Select(select).OfType<double>().ToArray();
			return values.Length > 0 ? values.Average().ToString("P0") : Strings.Benchmark_Unknown;
		}
	}

	private void OnCancelPreviewBenchmarkClick(object? sender, RoutedEventArgs e) => _previewBenchmarkCancel?.Cancel();
}
