using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Gui;

/// <summary>What the test runs with: the loaded recording (null = none), the layout a render would draw and the preview's width cap.</summary>
public sealed record BenchmarkRequest(string? RecordingPath, IReadOnlyList<OverlayElement> Layout, int PreviewWidth);

/// <summary>
///     The main window's part of the test: the loaded recording (or, with nothing loaded, the test recording) played and
///     measured in the app's own player.
/// </summary>
public delegate Task<AppPreviewOutcome> AppPreviewTest(FileSummary testRecording, bool fullscreen, Action<string> status, Action<PreviewLive> live,
	Action cancel, CancellationToken ct);

/// <summary>The app's playback measured and where it played - or, both null, why it couldn't run there.</summary>
public sealed record AppPreviewOutcome(PreviewMeasurementResult? Result, PreviewReportContext? Context, string? Skipped = null);

/// <summary>
///     The performance test: makes (once) the test recording with its anomalies (SyntheticRecording), measures each stage of
///     a render on it and on the loaded recording (RenderBenchmark), then plays it in this window's own preview and plays the
///     loaded recording in the app's player (AppPreviewTest) - everything shown live: the playback or the frames a render
///     draws, the step's frames a second and the computer's load. Then the computer and how busy it was, every result, the
///     slowest stage and the settings that would be faster - each applied with a click (saved at once). The encoder preset is
///     only shown: it changes the file's quality, so it's never suggested. "Copy report" puts all of it on the clipboard as
///     Markdown.
/// </summary>
public partial class BenchmarkWindow : Window
{
	// OverlaySettings.PreviewMaxWidth's Auto (MainWindow's AutoPreviewWidth).
	private const int AutoPreviewWidth = 0;
	private readonly BenchmarkRequest? _request;
	private readonly long _totalFrames;
	private readonly Func<bool> _isRendering = () => false;
	private readonly AppPreviewTest? _appPreview;
	private readonly Stopwatch _clock = new();
	private readonly List<(Phase Phase, BenchmarkLoad Load, long Memory)> _loads = [];
	private readonly int _fpsSeries, _droppedSeries, _cpuSeries, _gpuSeries, _decoderSeries, _encoderSeries;
	private CancellationTokenSource? _cts;
	private BenchmarkResult? _result;
	private List<(string Where, PreviewRun Run)> _previews = [];
	private string? _appPreviewSkipped;
	// The encoder the running test measures with, once it's picked - its card is the GPU shown live.
	private string? _measuredWith;
	private BenchmarkStage? _liveStage;
	private Phase _phase;
	private bool _opened;

	public BenchmarkWindow()
	{
		InitializeComponent();
		// Two lines a chart at most - four on one couldn't all be told apart. Each meter's bar is its line's legend.
		_fpsSeries = FpsChart.AddSeries(() => Palette.ChartSeries1);
		_droppedSeries = FpsChart.AddSeries(() => Palette.Danger);
		_cpuSeries = LoadChart.AddSeries(() => Palette.ChartSeries1);
		_gpuSeries = LoadChart.AddSeries(() => Palette.ChartSeries2);
		_encoderSeries = VideoChart.AddSeries(() => Palette.ChartSeries4);
		_decoderSeries = VideoChart.AddSeries(() => Palette.ChartSeries3);
		LoadChart.Maximum = 100;
		VideoChart.Maximum = 100;
		LiveCpuBar.Foreground = Palette.ChartSeries1;
		LiveGpuBar.Foreground = Palette.ChartSeries2;
		LiveEncoderBar.Foreground = Palette.ChartSeries4;
		LiveDecoderBar.Foreground = Palette.ChartSeries3;
	}

	/// <param name="totalFrames">The recording's frames, for the estimated render time - 0 without one.</param>
	public BenchmarkWindow(BenchmarkRequest request, long totalFrames, Func<bool> isRendering, AppPreviewTest? appPreview) : this()
	{
		_request = request;
		_totalFrames = totalFrames;
		_isRendering = isRendering;
		_appPreview = appPreview;
		FullscreenCheck.IsVisible = appPreview is not null;
		RecordingCheck.IsVisible = request.RecordingPath is not null;
		RecordingCheck.IsCheckedChanged += (_, _) => DescribeSource();
		DescribeSource();
	}

	private bool TestsRecording => _request?.RecordingPath is not null && RecordingCheck.IsChecked == true;

	/// <summary>What the test will run on: always the test recording, and the loaded one when it's there and ticked.</summary>
	private void DescribeSource()
	{
		SourceText.Text = _request?.RecordingPath is not { } path ? Strings.Benchmark_OnSynthetic
			: TestsRecording ? string.Format(Strings.Benchmark_OnRecording, RenderBenchmark.SampleSeconds, Path.GetFileName(path))
			: string.Format(Strings.Benchmark_RecordingNotTested, Path.GetFileName(path));
	}

	private enum Phase
	{
		Prepare,
		Render,
		WindowPreview,
		AppPreview
	}

	/// <summary>Developer tools: a result as the window shows it, without running anything.</summary>
	public void ShowSample(BenchmarkResult result, IReadOnlyList<(string Where, PreviewRun Run)> previews, long totalFrames)
	{
		StartButton.IsEnabled = false;
		FullscreenCheck.IsVisible = false;
		RecordingCheck.IsVisible = false;
		SourceText.Text = "Sample result from the developer tools - nothing was measured.";
		_previews = [.. previews];
		ShowResult(result, totalFrames, false);
	}

	protected override void OnOpened(EventArgs e)
	{
		base.OnOpened(e);
		_opened = true;
		// The results make the window several times taller - never past the screen: the content scrolls instead.
		if (CurrentScreen() is { } screen && FrameSize is { } frame)
			MaxHeight = screen.WorkingArea.Height / DesktopScaling - (frame.Height - ClientSize.Height);
	}

	/// <summary>
	///     The window sizes itself to its content, which only grows down from its top edge - the results would run off the screen.
	///     It grows both ways instead, its middle where it was, kept on the screen.
	/// </summary>
	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property != ClientSizeProperty || !_opened || change.OldValue is not Size before || change.NewValue is not Size after ||
		    CurrentScreen() is not { } screen)
			return;

		PixelRect area = screen.WorkingArea;
		int grown = (int)Math.Round((after.Height - before.Height) * DesktopScaling);
		int height = (int)Math.Round((FrameSize?.Height ?? after.Height) * DesktopScaling);
		int y = Math.Clamp(Position.Y - grown / 2, area.Y, Math.Max(area.Y, area.Bottom - height));
		if (y != Position.Y) Position = new PixelPoint(Position.X, y);
	}

	private Screen? CurrentScreen()
	{
		return Screens.ScreenFromWindow(this) ?? Screens.Primary;
	}

	protected override void OnClosing(WindowClosingEventArgs e)
	{
		_cts?.Cancel();
		base.OnClosing(e);
	}

	private async void OnStartClick(object? sender, RoutedEventArgs e)
	{
		if (_cts is { } running)
		{
			running.Cancel();
			return;
		}

		if (_request is null) return;
		if (_isRendering())
		{
			StatusText.Text = Strings.Benchmark_WhileRendering;
			return;
		}

		var cts = new CancellationTokenSource();
		_cts = cts;
		StartButton.Content = Strings.Benchmark_Stop;
		FullscreenCheck.IsEnabled = false;
		RecordingCheck.IsEnabled = false;
		Progress.IsVisible = true;
		Progress.IsIndeterminate = true;
		ResultsPanel.IsVisible = false;
		CopyReportButton.IsEnabled = false;
		TitleText.Text = Strings.Benchmark_Title;
		_measuredWith = null;
		_liveStage = null;
		_previews = [];
		_appPreviewSkipped = null;
		_loads.Clear();
		FpsChart.Clear();
		LoadChart.Clear();
		VideoChart.Clear();
		LivePanel.IsVisible = true;
		_clock.Restart();
		Task live = ShowLiveLoadAsync(cts.Token);
		Task elapsed = ShowElapsedAsync(cts.Token);
		try
		{
			EnterPhase(Phase.Prepare);
			ShowScreenText(Strings.Benchmark_LivePreparing);
			IReadOnlyList<string> paths = await Task.Run(() => SyntheticRecording.EnsureAsync(share => Dispatcher.UIThread.Post(() =>
			{
				Progress.IsIndeterminate = false;
				Progress.Maximum = 1;
				Progress.Value = share;
				StatusText.Text = string.Format(Strings.Benchmark_StagePreparingRecording, (share * 100).ToString("0"));
			}), cts.Token));
			Progress.IsIndeterminate = true;
			StatusText.Text = Strings.Benchmark_StageReadingRecording;
			(FileSummary? testRecording, string? problem) = await Task.Run(() => FileSummaryReader.Read(paths), cts.Token);
			if (testRecording?.TelemetryFrames is not { Count: > 0 }) throw new InvalidOperationException(problem ?? Strings.Benchmark_TestRecordingUnreadable);

			EnterPhase(Phase.Render);
			FpsChart.Reference = null;
			BenchmarkResult result = await Task.Run(() => RenderBenchmark.RunAsync(
				new BenchmarkInput(testRecording, TestsRecording ? _request.RecordingPath : null, _request.Layout, _request.PreviewWidth),
				progress => Dispatcher.UIThread.Post(() => ShowProgress(progress)),
				reading => Dispatcher.UIThread.Post(() => ShowLive(reading)), cts.Token));

			EnterPhase(Phase.WindowPreview);
			if (await RunWindowPreviewAsync(testRecording, cts.Token) is { } window)
				_previews.Add((Strings.Benchmark_PreviewInWindow, window));

			if (_appPreview is not null)
			{
				EnterPhase(Phase.AppPreview);
				if (await RunAppPreviewAsync(testRecording, cts) is { } app)
					_previews.Add((string.Format(Strings.Benchmark_PreviewInApp, app.Context.Source), app));
			}

			TitleText.Text = string.Format(Strings.Benchmark_TitleDone, _clock.Elapsed.TotalSeconds.ToString("0"));
			StatusText.Text = "";
			ShowResult(result, _totalFrames, true);
		}
		catch (OperationCanceledException)
		{
			StatusText.Text = Strings.Benchmark_Stopped;
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, "Performance test failed");
			StatusText.Text = string.Format(Strings.Benchmark_Failed, ex.Message);
		}
		finally
		{
			_cts = null;
			cts.Cancel();
			await live;
			await elapsed;
			cts.Dispose();
			_clock.Stop();
			StartButton.Content = Strings.Benchmark_Start;
			FullscreenCheck.IsEnabled = true;
			RecordingCheck.IsEnabled = true;
			Progress.IsVisible = false;
			LivePanel.IsVisible = false;
			ClearLiveFrame();
		}
	}

	private void EnterPhase(Phase phase)
	{
		_phase = phase;
		_liveStage = null;
		FpsChart.Clear();
		FpsLegend.Text = "";
	}

	/// <summary>
	///     The test recording in this window's own player, sized as the live screen shows it, the layout a render would draw:
	///     played from its start (all three files, the joins and the stop included) and measured like the app's preview.
	/// </summary>
	private async Task<PreviewRun?> RunWindowPreviewAsync(FileSummary testRecording, CancellationToken ct)
	{
		ClearLiveFrame();
		LiveScreenText.IsVisible = false;
		LiveVideo.IsVisible = true;
		LiveCaption.Text = Strings.Benchmark_LivePlayback;
		await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, ct);

		double scaling = RenderScaling;
		int width = Math.Min(testRecording.Video.Width, (int)(LiveScreen.Bounds.Width * scaling)) & ~1;
		int height = (int)Math.Round(width * (double)testRecording.Video.Height / testRecording.Video.Width) & ~1;
		OverlaySettings settings = OverlaySettingsStore.Load();
		using var player = new PreviewPlayer();
		player.FrameReady += LiveVideo.ShowStill;
		player.PlaybackStarted += () => LiveVideo.StartPlayback(player.Frames);
		try
		{
			await player.OpenAsync(testRecording, width, height, layoutOverride: _request!.Layout);
			ct.ThrowIfCancellationRequested();
			player.SetAudioVolume(0, true);
			player.SuppressRouteIntro(true);
			FpsChart.Reference = player.PlaybackFrameRate;
			player.Play(TimeSpan.Zero);
			PreviewMeasurementResult? result = await PreviewTest.MeasureAsync(player, text => StatusText.Text = text, AddPreviewLive, ct);
			if (result is null) return null;

			Screen? screen = Screens.ScreenFromWindow(this);
			int index = screen is null ? 0 : Math.Max(0, Screens.All.ToList().FindIndex(s => MonitorChoice.KeyOf(s) == MonitorChoice.KeyOf(screen)));
			var context = new PreviewReportContext(Strings.Benchmark_TestRecording, Strings.Benchmark_ModeTestWindow,
				screen is null ? Strings.Benchmark_Unknown : MonitorChoice.Describe(screen, index), new PixelSize(width, height),
				LiveVideo.Bounds.Width * scaling, LiveVideo.Bounds.Height * scaling, scaling, player.PlaybackRate, player.DecoderDescription, true,
				settings.PreviewDrawsShadows && !settings.DisableShadows);
			return Run(result, context, Phase.WindowPreview);
		}
		finally
		{
			player.Pause();
			player.Close();
			LiveVideo.Clear();
			LiveVideo.IsVisible = false;
		}
	}

	/// <summary>
	///     The app's player (AppPreviewTest), its readings shown here; skipped with a reason when it can't run there or the user
	///     declines. Asked first, as it takes over the main window: meanwhile this window and the ones it was opened over
	///     (Settings, Tools) are minimized so the preview is in view, and restored after.
	/// </summary>
	private async Task<PreviewRun?> RunAppPreviewAsync(FileSummary testRecording, CancellationTokenSource cts)
	{
		ShowScreenText(Strings.Benchmark_LiveApp);
		LiveCaption.Text = "";
		StatusText.Text = Strings.Benchmark_AppPreviewAskTitle;
		string message = _request!.RecordingPath is null
			? Strings.Benchmark_AppPreviewAskMessage + Environment.NewLine + Environment.NewLine + Strings.Benchmark_AppPreviewAskTestRecording
			: Strings.Benchmark_AppPreviewAskMessage;
		if (!await ConfirmDialog.AskAsync(this, Strings.Benchmark_AppPreviewAskTitle, message, Strings.Benchmark_AppPreviewStart, DialogKind.Info))
		{
			_appPreviewSkipped = string.Format(Strings.Benchmark_PreviewSkipped, Strings.Benchmark_SkippedByUser);
			return null;
		}

		cts.Token.ThrowIfCancellationRequested();
		List<Window> minimized = [];
		for (Window? window = this; window?.Owner is Window owner; window = owner)
			minimized.Add(window);
		foreach (Window window in minimized) window.WindowState = WindowState.Minimized;

		AppPreviewOutcome outcome;
		try
		{
			outcome = await _appPreview!(testRecording, FullscreenCheck.IsChecked == true, text => StatusText.Text = text, AddPreviewLive, cts.Cancel,
				cts.Token);
		}
		finally
		{
			for (int i = minimized.Count - 1; i >= 0; i--) minimized[i].WindowState = WindowState.Normal;
			Activate();
		}

		cts.Token.ThrowIfCancellationRequested();
		if (outcome is { Result: { } result, Context: { } context }) return Run(result, context, Phase.AppPreview);

		_appPreviewSkipped = string.Format(Strings.Benchmark_PreviewSkipped, outcome.Skipped ?? Strings.Benchmark_Unknown);
		return null;
	}

	private PreviewRun Run(PreviewMeasurementResult result, PreviewReportContext context, Phase phase)
	{
		var during = _loads.Where(l => l.Phase == phase).ToList();
		return new PreviewRun(result, context, [.. during.Select(l => l.Load)], during.Count > 0 ? during.Max(l => l.Memory) : 0);
	}

	private void AddPreviewLive(PreviewLive reading)
	{
		double seconds = _clock.Elapsed.TotalSeconds;
		FpsChart.Add(_fpsSeries, seconds, reading.ShownFps);
		FpsChart.Add(_droppedSeries, seconds, reading.DroppedFps);
		FpsLegend.Text = string.Format(Strings.Benchmark_LivePreviewFps, reading.ShownFps.ToString("0"), reading.DroppedFps.ToString("0"));
	}

	/// <summary>
	///     The CPU, the GPU and its video decoder and encoder while the test runs, once a second (LoadMonitor) - this app's own work counted,
	///     as it's part of the test - kept per phase with the app's memory for the preview's report. Ends with `ct`.
	/// </summary>
	private async Task ShowLiveLoadAsync(CancellationToken ct)
	{
		try
		{
			using LoadMonitor monitor = await Task.Run(LoadMonitor.Start, ct);
			using var process = Process.GetCurrentProcess();
			while (true)
			{
				await Task.Delay(TimeSpan.FromSeconds(1), ct);
				uint? vendor = _measuredWith is { } encoder ? FfmpegPipeline.GpuVendor(encoder) : null;
				BenchmarkLoad load = await Task.Run(() => monitor.ReadAsync(vendor, true, ct), ct);
				process.Refresh();
				_loads.Add((_phase, load, process.WorkingSet64));
				double seconds = _clock.Elapsed.TotalSeconds;
				LoadChart.Add(_cpuSeries, seconds, load.Cpu * 100);
				LoadChart.Add(_gpuSeries, seconds, load.Gpu * 100);
				VideoChart.Add(_decoderSeries, seconds, load.Decoder * 100);
				VideoChart.Add(_encoderSeries, seconds, load.Encoder * 100);
				ShowMeter(LiveCpuBar, LiveCpuText, load.Cpu);
				ShowMeter(LiveGpuBar, LiveGpuText, load.Gpu);
				ShowMeter(LiveDecoderBar, LiveDecoderText, load.Decoder);
				ShowMeter(LiveEncoderBar, LiveEncoderText, load.Encoder);
			}
		}
		catch (OperationCanceledException)
		{
			// The test ended.
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, "Reading the load during the performance test failed");
		}
	}

	/// <summary>How long the test has run, ticking each whole second - ends with `ct`, hidden.</summary>
	private async Task ShowElapsedAsync(CancellationToken ct)
	{
		ElapsedText.IsVisible = true;
		try
		{
			while (true)
			{
				ElapsedText.Text = string.Format(Strings.Benchmark_Elapsed, _clock.Elapsed.ToString(@"m\:ss"));
				await Task.Delay(TimeSpan.FromMilliseconds(1000 - _clock.ElapsedMilliseconds % 1000), ct);
			}
		}
		catch (OperationCanceledException)
		{
			// The test ended.
		}

		ElapsedText.IsVisible = false;
	}

	private static void ShowMeter(ProgressBar bar, TextBlock text, double? share)
	{
		bar.Value = (share ?? 0) * 100;
		bar.Opacity = share is null ? 0.4 : 1;
		text.Text = share is { } value ? $"{value * 100:0}%" : Strings.Benchmark_Unknown;
	}

	private void ShowProgress(BenchmarkProgress progress)
	{
		if (_cts is null) return;

		_measuredWith = progress.Encoder ?? _measuredWith;
		Progress.IsIndeterminate = progress.Total == 0;
		Progress.Maximum = Math.Max(progress.Total, 1);
		Progress.Value = progress.Done;
		string stage = StageText(progress.Stage);
		StatusText.Text = progress.Total == 0 ? stage : string.Format(Strings.Benchmark_StageProgress, stage, progress.Done + 1, progress.Total);
		ShowStage(progress.Stage);
	}

	/// <summary>A new step clears the frames-a-second chart (each step runs at its own pace) and says what the live screen shows.</summary>
	private void ShowStage(BenchmarkStage stage)
	{
		if (_liveStage == stage) return;

		_liveStage = stage;
		FpsChart.Clear();
		FpsLegend.Text = "";
		switch (stage)
		{
			case BenchmarkStage.Overlay:
				LiveCaption.Text = Strings.Benchmark_LiveOverlay;
				break;
			case BenchmarkStage.Render:
				LiveCaption.Text = Strings.Benchmark_LiveRender;
				break;
			case BenchmarkStage.Preparing:
				ShowScreenText(Strings.Benchmark_LivePreparing);
				break;
			default:
				ShowScreenText(Strings.Benchmark_LiveFfmpeg);
				LiveCaption.Text = StageText(stage);
				break;
		}
	}

	private void ShowLive(BenchmarkLive reading)
	{
		if (_cts is null) return;

		if (reading.Fps is { } fps)
		{
			FpsChart.Add(_fpsSeries, _clock.Elapsed.TotalSeconds, fps);
			FpsLegend.Text = string.Format(Strings.Benchmark_Fps, fps.ToString("0"));
		}

		if (reading.Frame is { } frame && reading.Stage is BenchmarkStage.Overlay or BenchmarkStage.Render)
			ShowLiveFrame(frame);
	}

	private void ShowLiveFrame(BenchmarkThumbnail frame)
	{
		var bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888,
			frame.Opaque ? AlphaFormat.Opaque : AlphaFormat.Unpremul);
		using (ILockedFramebuffer buffer = bitmap.Lock())
		{
			for (int row = 0; row < frame.Height; row++)
				Marshal.Copy(frame.Bgra, row * frame.Width * 4, buffer.Address + row * buffer.RowBytes, frame.Width * 4);
		}

		(LiveFrame.Source as IDisposable)?.Dispose();
		LiveFrame.Source = bitmap;
		LiveFrame.IsVisible = true;
		LiveScreenText.IsVisible = false;
	}

	private void ShowScreenText(string text)
	{
		ClearLiveFrame();
		LiveScreenText.Text = text;
		LiveScreenText.IsVisible = true;
	}

	private void ClearLiveFrame()
	{
		(LiveFrame.Source as IDisposable)?.Dispose();
		LiveFrame.Source = null;
		LiveFrame.IsVisible = false;
	}

	private static string StageText(BenchmarkStage stage)
	{
		return stage switch
		{
			BenchmarkStage.Decode => Strings.Benchmark_StageDecode,
			BenchmarkStage.Overlay => Strings.Benchmark_StageOverlay,
			BenchmarkStage.Pipe => Strings.Benchmark_StagePipe,
			BenchmarkStage.Encode => Strings.Benchmark_StageEncode,
			BenchmarkStage.Graph => Strings.Benchmark_StageGraph,
			BenchmarkStage.Render => Strings.Benchmark_StageRender,
			_ => Strings.Benchmark_StagePreparing
		};
	}

	private void ShowResult(BenchmarkResult result, long totalFrames, bool canApply)
	{
		_result = result;
		OverlaySettings settings = OverlaySettingsStore.Load();

		LoadWarningText.IsVisible = result.Load.IsBusy;
		LoadWarningText.Text = string.Format(Strings.Benchmark_LoadWarning, Percent(result.Load.Cpu), Percent(result.Load.Gpu));
		UnstableWarningText.IsVisible = result.IsUnstable;
		WarningCard.IsVisible = LoadWarningText.IsVisible || UnstableWarningText.IsVisible;

		SystemGrid.Children.Clear();
		SystemGrid.RowDefinitions.Clear();
		foreach ((string label, string value) in SystemRows(result))
		{
			int row = AddRow(SystemGrid);
			AddCell(SystemGrid, new TextBlock { Text = label, Classes = { "resultLabel" } }, row, 0);
			AddCell(SystemGrid, new TextBlock { Text = value, Classes = { "resultValue" } }, row, 1);
		}

		ResultsGrid.Children.Clear();
		ResultsGrid.RowDefinitions.Clear();
		int header = AddRow(ResultsGrid);
		string[] headers = [Strings.Benchmark_ColumnStage, Strings.Benchmark_ColumnResult, Strings.Benchmark_ColumnRange, Strings.Benchmark_ColumnNotes];
		for (int column = 0; column < headers.Length; column++)
			AddCell(ResultsGrid, new TextBlock { Text = headers[column], Classes = { "columnHeader" } }, header, column);

		foreach (ResultRow item in ResultRows(result, settings))
		{
			int row = AddRow(ResultsGrid);
			AddCell(ResultsGrid, new TextBlock { Text = item.Stage, Classes = { "resultLabel" } }, row, 0);
			AddCell(ResultsGrid, new TextBlock { Text = item.Result, Classes = { "resultValue" } }, row, 1);
			AddCell(ResultsGrid, new TextBlock { Text = item.Range, Classes = { "resultLabel" } }, row, 2);
			AddCell(ResultsGrid, new TextBlock { Text = item.Notes, Classes = { "resultLabel" }, TextWrapping = TextWrapping.Wrap }, row, 3);
		}

		ShowPreviewResults();

		BottleneckText.Text = BottleneckDescription(result, settings);
		string? estimate = EstimateDescription(result, settings, totalFrames);
		EstimateText.IsVisible = estimate is not null;
		EstimateText.Text = estimate;

		ShowAdvice(Advise(result, settings), SkippedAdvice(result), canApply);
		ResultsPanel.IsVisible = true;
		CopyReportButton.IsEnabled = true;
		CopyReportButton.Content = Strings.Benchmark_CopyReport;
	}

	/// <summary>Each measured playback in a row - how smooth it was and where the time went - and why the app's one is missing, if it is.</summary>
	private void ShowPreviewResults()
	{
		PreviewGrid.Children.Clear();
		PreviewGrid.RowDefinitions.Clear();
		bool any = _previews.Count > 0 || _appPreviewSkipped is not null;
		PreviewSectionLabel.IsVisible = any;
		PreviewCard.IsVisible = any;
		if (!any) return;

		int header = AddRow(PreviewGrid);
		string[] headers = [Strings.Benchmark_PreviewColumnWhere, Strings.Benchmark_ColumnResult, Strings.Benchmark_ColumnNotes];
		for (int column = 0; column < headers.Length; column++)
			AddCell(PreviewGrid, new TextBlock { Text = headers[column], Classes = { "columnHeader" } }, header, column);

		foreach ((string where, PreviewRun run) in _previews)
		{
			int row = AddRow(PreviewGrid);
			AddCell(PreviewGrid, new TextBlock { Text = where, Classes = { "resultLabel" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 }, row, 0);
			AddCell(PreviewGrid, new TextBlock { Text = PreviewSpeed(run.Result), Classes = { "resultValue" } }, row, 1);
			AddCell(PreviewGrid, new TextBlock { Text = PreviewNotes(run), Classes = { "resultLabel" }, TextWrapping = TextWrapping.Wrap }, row, 2);
		}

		if (_appPreviewSkipped is { } skipped)
		{
			int row = AddRow(PreviewGrid);
			var text = new TextBlock { Text = skipped, Classes = { "resultLabel" }, TextWrapping = TextWrapping.Wrap };
			AddCell(PreviewGrid, text, row, 0);
			Grid.SetColumnSpan(text, 3);
		}
	}

	private static string PreviewSpeed(PreviewMeasurementResult result)
	{
		return string.Format(Strings.Benchmark_PreviewFps, result.PresentedFps.ToString("0.0"), result.ExpectedFps.ToString("0.##"));
	}

	private static string PreviewNotes(PreviewRun run)
	{
		PreviewMeasurementResult result = run.Result;
		return string.Join(Environment.NewLine,
			PreviewTest.Gaps(result),
			PreviewTest.Dropped(result),
			string.Join("; ", PreviewTest.Timing(Strings.Benchmark_NameDecode, result.Decode), PreviewTest.Timing(Strings.Benchmark_NameOverlay, result.Overlay),
				PreviewTest.Timing(Strings.PreviewBenchmark_Draw, result.Draw)),
			string.Format(Strings.PreviewBenchmark_Resolution, run.Context.Decoded?.Width, run.Context.Decoded?.Height,
				run.Context.ShownWidth.ToString("0"), run.Context.ShownHeight.ToString("0"), run.Context.Scaling.ToString("0.##")));
	}

	private sealed record ResultRow(string Stage, string Result, string Range, string Notes);

	private static List<(string Label, string Value)> SystemRows(BenchmarkResult result)
	{
		BenchmarkSystem system = result.System;
		List<(string, string)> rows =
		[
			(Strings.Benchmark_Os, system.Os),
			(Strings.Benchmark_Cpu, $"{system.Cpu ?? Strings.Benchmark_Unknown}, {Plural.Format(Strings.Benchmark_Threads, system.Threads)}"),
			(Strings.Benchmark_Memory, FormatHelper.FormatBytes(system.MemoryBytes)),
			(Strings.Benchmark_Gpu, system.Gpus.Count > 0 ? string.Join("; ", system.Gpus) : Strings.Benchmark_Unknown),
			("FFmpeg", system.Ffmpeg ?? Strings.Benchmark_Unknown),
			("OsmoOverlay", system.App),
			(Strings.Benchmark_Load, string.Format(Strings.Benchmark_LoadValue, Percent(result.Load.Cpu), Percent(result.Load.Gpu))),
			(Strings.Benchmark_TestRecording, Describe(result.Source))
		];
		if (result.Recording is { } recording)
		{
			rows.Add((Strings.Benchmark_Recording,
				$"{Describe(recording.Video)} ({string.Format(Strings.Benchmark_RecordingSample, RenderBenchmark.SampleSeconds)})"));
		}

		if (result.Output != result.Source) rows.Add((Strings.Benchmark_Output, Describe(result.Output)));
		return rows;
	}

	private static List<ResultRow> ResultRows(BenchmarkResult result, OverlaySettings settings)
	{
		List<ResultRow> rows =
		[
			Speed(Strings.Benchmark_DecodeGpu, result.HardwareDecode, result.Fps, settings.HardwareDecoding && result.Recording is null),
			Speed(Strings.Benchmark_DecodeCpu, result.SoftwareDecode, result.Fps, !settings.HardwareDecoding && result.Recording is null,
				Strings.Benchmark_SampleComesOutHigh),
			Overlay(string.Format(Strings.Benchmark_OverlayRender, result.Output.Width, result.Output.Height), result.Overlay, result.OverlayPlain),
			Overlay(string.Format(Strings.Benchmark_OverlayPreview, result.PreviewWidth, result.PreviewHeight), result.PreviewOverlay,
				result.PreviewOverlayPlain),
			Speed(string.Format(Strings.Benchmark_Pipe, result.Output.Width, result.Output.Height), result.Pipe, result.Fps, false)
		];

		EncoderSpeed? inUse = RenderBenchmark.EncoderInUse(result, settings);
		foreach (EncoderSpeed encoder in result.Encoders)
		{
			rows.Add(Speed(string.Format(Strings.Benchmark_Encoding, $"{encoder.Encoder} {(encoder.Preset is { } preset ? FfmpegPipeline.PresetName(encoder.Encoder, preset) : null)}".Trim()), encoder.Speed, result.Fps,
				encoder == inUse, encoder.LimitedByDecoding ? Strings.Benchmark_LimitedByDecoding : null));
		}

		rows.Add(Speed(Strings.Benchmark_Graph, result.Graph, result.Fps, true));

		if (result.HardwareRender is not null || result.SoftwareRender is not null)
		{
			rows.Add(Speed(Strings.Benchmark_RenderGpu, result.HardwareRender, result.Fps, settings.HardwareDecoding && result.Recording is null,
				Strings.Benchmark_SampleRoute));
			rows.Add(Speed(Strings.Benchmark_RenderCpu, result.SoftwareRender, result.Fps, !settings.HardwareDecoding && result.Recording is null,
				Strings.Benchmark_SampleRoute));
		}

		if (result.Recording is { } recording)
		{
			static string Named(string stage) => string.Format(Strings.Benchmark_OnYourRecording, stage);
			double fps = recording.Video.Fps;
			rows.Add(Speed(Named(Strings.Benchmark_DecodeGpu), recording.HardwareDecode, fps, settings.HardwareDecoding));
			rows.Add(Speed(Named(Strings.Benchmark_DecodeCpu), recording.SoftwareDecode, fps, !settings.HardwareDecoding));
			rows.Add(Speed(Named(Strings.Benchmark_RenderGpu), recording.HardwareRender, fps, settings.HardwareDecoding, Strings.Benchmark_SampleRoute));
			rows.Add(Speed(Named(Strings.Benchmark_RenderCpu), recording.SoftwareRender, fps, !settings.HardwareDecoding, Strings.Benchmark_SampleRoute));
		}

		return rows;
	}

	private static ResultRow Speed(string stage, BenchmarkStat? stat, double sourceFps, bool inUse, string? note = null)
	{
		if (stat is null) return new ResultRow(stage, Strings.Benchmark_StepFailed, "", "");

		List<string> notes = [];
		if (inUse) notes.Add(Strings.Benchmark_InUse);
		if (note is not null) notes.Add(note);
		if (sourceFps > 0) notes.Add(string.Format(Strings.Benchmark_Realtime, (stat.Median / sourceFps).ToString("0.0#")));
		if (stat.IsUnstable) notes.Add(Strings.Benchmark_Unstable);
		string range = stat.Runs > 1
			? string.Format(Strings.Benchmark_FpsRange, stat.Min.ToString("0"), stat.Max.ToString("0"), (stat.Spread / 2 * 100).ToString("0"))
			: "";
		return new ResultRow(stage, string.Format(Strings.Benchmark_Fps, stat.Median.ToString("0")), range, string.Join(", ", notes));
	}

	private static ResultRow Overlay(string stage, FrameTimes times, FrameTimes plain)
	{
		return new ResultRow(stage, string.Format(Strings.Benchmark_OverlayValue, times.MedianMs.ToString("0.0"), times.Fps.ToString("0")),
			string.Format(Strings.Benchmark_P95, times.P95Ms.ToString("0.0")), string.Format(Strings.Benchmark_WithoutShadows, plain.MedianMs.ToString("0.0")));
	}

	private static string BottleneckDescription(BenchmarkResult result, OverlaySettings settings)
	{
		if (RenderBenchmark.Bottleneck(result, settings) is not { } bottleneck) return Strings.Benchmark_StepFailed;

		string stage = bottleneck.Stage switch
		{
			BenchmarkStage.Decode => Strings.Benchmark_NameDecode,
			BenchmarkStage.Overlay => Strings.Benchmark_NameOverlay,
			BenchmarkStage.Pipe => Strings.Benchmark_NamePipe,
			BenchmarkStage.Graph => Strings.Benchmark_NameGraph,
			_ => Strings.Benchmark_NameEncode
		};
		return bottleneck is { RenderFps: { } render, Efficiency: { } efficiency }
			? string.Format(Strings.Benchmark_BottleneckWithRender, stage, bottleneck.Fps.ToString("0"), render.ToString("0"), (efficiency * 100).ToString("0"))
			: string.Format(Strings.Benchmark_BottleneckOnly, stage, bottleneck.Fps.ToString("0"));
	}

	private static string? EstimateDescription(BenchmarkResult result, OverlaySettings settings, long totalFrames)
	{
		double? renderFps = RenderBenchmark.RenderInUse(result, settings)?.Median;
		return totalFrames > 0 && renderFps > 0
			? string.Format(Strings.Benchmark_Estimate, TimeSpan.FromSeconds(totalFrames / renderFps.Value).ToString(@"hh\:mm\:ss"))
			: null;
	}

	private static string Describe(VideoInfo video)
	{
		string codec = video.CodecName switch
		{
			"hevc" => "HEVC",
			"h264" => "H.264",
			_ => video.CodecName.ToUpperInvariant()
		};
		int depth = video.PixFmt.Contains("10", StringComparison.Ordinal) ? 10 : 8;
		return string.Format(Strings.Benchmark_VideoValue, codec, video.Width, video.Height, video.Fps.ToString("0.##"), depth,
			(video.BitRate / 1_000_000.0).ToString("0"));
	}

	private static string Percent(double? share)
	{
		return share is { } value ? $"{value * 100:0}%" : Strings.Benchmark_Unknown;
	}

	private static int AddRow(Grid grid)
	{
		grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		return grid.RowDefinitions.Count - 1;
	}

	private static void AddCell(Grid grid, Control cell, int row, int column)
	{
		Grid.SetRow(cell, row);
		Grid.SetColumn(cell, column);
		grid.Children.Add(cell);
	}

	/// <summary>Why decoding wasn't compared (RenderBenchmark.Advise) - null when it was.</summary>
	private static string? SkippedAdvice(BenchmarkResult result)
	{
		if (result.Load.IsBusy) return Strings.Benchmark_AdviceSkippedBusy;
		return result.Recording is not null ? null : Strings.Benchmark_AdviceSkippedSample;
	}

	/// <summary>RenderBenchmark's suggestions, and Auto preview quality when the app's playback decoded far more than it showed and fell behind.</summary>
	private List<BenchmarkAdvice> Advise(BenchmarkResult result, OverlaySettings settings)
	{
		List<BenchmarkAdvice> advice = RenderBenchmark.Advise(result, settings);
		if (AppPreviewRun() is { } app && settings.PreviewMaxWidth != AutoPreviewWidth && PreviewTest.DecodedTooMuch(app))
			advice.Add(new BenchmarkAdvice(BenchmarkAdviceKind.PreviewQualityAuto, 0));
		return advice;
	}

	/// <summary>The playback in the app's player - every other one played in this window.</summary>
	private PreviewRun? AppPreviewRun()
	{
		return _previews.Select(p => p.Run).LastOrDefault(r => r.Context.Mode != Strings.Benchmark_ModeTestWindow);
	}

	private void ShowAdvice(List<BenchmarkAdvice> advice, string? skipped, bool canApply)
	{
		AdvicePanel.Children.Clear();
		if (skipped is not null) AdvicePanel.Children.Add(new TextBlock { Text = skipped, Classes = { "warningText" } });
		if (advice.Count == 0)
		{
			if (skipped is null) AdvicePanel.Children.Add(new TextBlock { Text = Strings.Benchmark_NoAdvice, Classes = { "hint" } });
			return;
		}

		foreach (BenchmarkAdvice item in advice)
		{
			var apply = new Button { Content = Strings.Benchmark_Apply, IsEnabled = canApply, VerticalAlignment = VerticalAlignment.Center };
			apply.Click += (_, _) =>
			{
				OverlaySettings settings = OverlaySettingsStore.Load();
				OverlaySettingsStore.Save(Apply(item.Kind, settings));
				AppLogger.Info($"Performance test: applied {item.Kind}");
				apply.Content = Strings.Benchmark_Applied;
				apply.IsEnabled = false;
			};

			var text = new TextBlock { Text = AdviceText(item), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
			var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
			row.Children.Add(text);
			Grid.SetColumn(apply, 1);
			row.Children.Add(apply);
			AdvicePanel.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(16, 10), Child = row });
		}
	}

	private string AdviceText(BenchmarkAdvice advice)
	{
		return advice.Kind switch
		{
			BenchmarkAdviceKind.PreviewQualityAuto when AppPreviewRun()?.Context is { Decoded: { } decoded } shown =>
				string.Format(Strings.Benchmark_AdvicePreviewQuality, decoded.Width, decoded.Height, shown.ShownWidth.ToString("0"), shown.ShownHeight.ToString("0")),
			BenchmarkAdviceKind.SoftwareDecode => string.Format(Strings.Benchmark_AdviceSoftwareDecode, (advice.Gain * 100).ToString("0")),
			BenchmarkAdviceKind.HardwareDecode => string.Format(Strings.Benchmark_AdviceHardwareDecode, (advice.Gain * 100).ToString("0")),
			BenchmarkAdviceKind.PreviewShadowsOff => string.Format(Strings.Benchmark_AdvicePreviewShadowsOff, advice.Gain.ToString("0.0")),
			_ => Strings.Benchmark_AdvicePreviewShadowsOn
		};
	}

	private static OverlaySettings Apply(BenchmarkAdviceKind kind, OverlaySettings settings)
	{
		return kind switch
		{
			BenchmarkAdviceKind.SoftwareDecode => settings with { HardwareDecoding = false },
			BenchmarkAdviceKind.HardwareDecode => settings with { HardwareDecoding = true },
			BenchmarkAdviceKind.PreviewShadowsOff => settings with { PreviewShadows = false },
			BenchmarkAdviceKind.PreviewQualityAuto => settings with { PreviewMaxWidth = AutoPreviewWidth },
			_ => settings with { PreviewShadows = true }
		};
	}

	/// <summary>Everything the window shows as Markdown - for a bug report or comparing two computers.</summary>
	private string BuildReport(BenchmarkResult result)
	{
		OverlaySettings settings = OverlaySettingsStore.Load();
		var text = new StringBuilder();
		text.AppendLine($"# {Strings.Benchmark_Title} - OsmoOverlay {result.System.App}");
		text.AppendLine();
		text.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
		text.AppendLine();
		text.AppendLine($"## {Strings.Benchmark_Computer}");
		text.AppendLine();
		foreach ((string label, string value) in SystemRows(result)) text.AppendLine($"- {label}: {value}");
		if (result.Load.IsBusy) text.AppendLine().AppendLine($"> {string.Format(Strings.Benchmark_LoadWarning, Percent(result.Load.Cpu), Percent(result.Load.Gpu))}");
		if (result.IsUnstable) text.AppendLine().AppendLine($"> {Strings.Benchmark_UnstableWarning}");
		text.AppendLine();
		text.AppendLine($"## {Strings.Benchmark_Results}");
		text.AppendLine();
		text.AppendLine(
			$"| {Strings.Benchmark_ColumnStage} | {Strings.Benchmark_ColumnResult} | {Strings.Benchmark_ColumnRange} | {Strings.Benchmark_ColumnNotes} |");
		text.AppendLine("|---|---|---|---|");
		foreach (ResultRow row in ResultRows(result, settings)) text.AppendLine($"| {row.Stage} | {row.Result} | {row.Range} | {row.Notes} |");
		if (_previews.Count > 0 || _appPreviewSkipped is not null)
		{
			text.AppendLine();
			text.AppendLine($"## {Strings.Benchmark_PreviewSection}");
			foreach ((string where, PreviewRun run) in _previews)
			{
				text.AppendLine();
				text.AppendLine($"### {where}");
				text.AppendLine();
				text.AppendLine($"- {PreviewSpeed(run.Result)}");
				foreach (string line in PreviewTest.Report(run).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
					text.AppendLine($"- {line}");
			}

			if (_appPreviewSkipped is { } skipped) text.AppendLine().AppendLine(skipped);
		}

		text.AppendLine();
		text.AppendLine($"## {Strings.Benchmark_SlowestStage}");
		text.AppendLine();
		text.AppendLine(BottleneckDescription(result, settings));
		if (EstimateDescription(result, settings, _totalFrames) is { } estimate) text.AppendLine().AppendLine(estimate);
		text.AppendLine();
		text.AppendLine($"## {Strings.Benchmark_Suggestions}");
		text.AppendLine();
		List<BenchmarkAdvice> advice = Advise(result, settings);
		if (SkippedAdvice(result) is { } skippedAdvice) text.AppendLine(skippedAdvice).AppendLine();
		else if (advice.Count == 0) text.AppendLine(Strings.Benchmark_NoAdvice);
		foreach (BenchmarkAdvice item in advice) text.AppendLine($"- {AdviceText(item)}");
		return text.ToString();
	}

	private async void OnCopyReportClick(object? sender, RoutedEventArgs e)
	{
		if (_result is null || GetTopLevel(this)?.Clipboard is not { } clipboard) return;

		await clipboard.SetTextAsync(BuildReport(_result));
		CopyReportButton.Content = Strings.Benchmark_Copied;
	}

	private void OnCloseClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}
}
