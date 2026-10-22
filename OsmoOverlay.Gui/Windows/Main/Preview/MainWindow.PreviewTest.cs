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
	// The performance test is playing in this window's player: nothing else may pause it, move it or reopen it meanwhile.
	private bool _previewTestRunning;

	/// <summary>
	///     The performance test's playback in this window's player (AppPreviewTest): the loaded recording - or, with nothing
	///     added, the test recording loaded the usual way (it stays loaded) - played as the preview plays it, in full screen
	///     or on the second screen when asked or on, and measured (PreviewTest). Then everything is put back: the mode, the
	///     quality, the loop, the position and whether it was playing. Esc in a preview window stops the test.
	/// </summary>
	private async Task<AppPreviewOutcome> RunAppPreviewTestAsync(FileSummary testRecording, bool fullscreen, Action<string> status,
		Action<PreviewLive> live, Action cancel, CancellationToken ct)
	{
		if (_previewTestRunning || _phase == UiPhase.Rendering) return new AppPreviewOutcome(null, null, Strings.Benchmark_SkippedBusy);
		if (_inputPaths.Count == 0 && _phase == UiPhase.Idle)
			await LoadInputsAsync(testRecording.InputPaths);

		if (_summary is null || _phase != UiPhase.SummaryReady || _previewFrameSize is null)
			return new AppPreviewOutcome(null, null, Strings.Benchmark_SkippedNotLoaded);
		if (_previewPlayer.Duration.TotalSeconds < 1) return new AppPreviewOutcome(null, null, Strings.Benchmark_SkippedTooShort);

		OverlaySettings settings = OverlaySettingsStore.Load();
		bool secondScreen = _fullscreenWindow is null && settings.SecondScreenEnabled && SecondScreenTarget(settings) is not null;
		string source = _summary.InputPaths.SequenceEqual(testRecording.InputPaths)
			? Strings.Benchmark_TestRecording
			: string.Join(", ", _summary.InputPaths.Select(Path.GetFileName));
		TimeSpan position = _previewPosition;
		bool wasPlaying = _previewPlayer.IsPlaying;
		bool wasFullscreen = _fullscreenWindow is not null;
		bool hadSecondScreen = _secondScreen is not null;
		bool wasOnSecondScreen = _viewportOnSecondScreen;
		bool loop = _previewPlayer.Loop;
		TimeRange? loopRange = _previewPlayer.LoopRange;
		int originalWidth = _openedPreviewMaxWidth;
		var windows = new List<Window>();

		void BlockKeys(object? sender, KeyEventArgs e)
		{
			if (e.Key == Key.Escape) cancel();
			e.Handled = true;
		}

		void BlockPointer(object? sender, PointerPressedEventArgs e) => e.Handled = true;

		void Watch(Window window)
		{
			windows.Add(window);
			window.AddHandler(KeyDownEvent, BlockKeys, RoutingStrategies.Tunnel);
			window.AddHandler(PointerPressedEvent, BlockPointer, RoutingStrategies.Tunnel);
		}

		_previewTestRunning = true;
		try
		{
			PausePlayback();
			_autoQualityDelay.Stop();
			bool useFullscreen = fullscreen && !secondScreen;
			if (wasFullscreen && !useFullscreen) _fullscreenWindow?.Close();
			if (!wasFullscreen && useFullscreen) TogglePreviewFullscreen();
			if (secondScreen && _secondScreen is null) OpenSecondScreen();
			if (_fullscreenWindow is { } fullscreenWindow) Watch(fullscreenWindow);
			if (_secondScreen is { } second) Watch(second);

			// Moving the viewport queues a seek and a layout pass before its compositor can play.
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			if (secondScreen && !_viewportOnSecondScreen) MoveViewportToSecondScreen();
			await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			if (_previewMaxWidth == AutoPreviewWidth && ResolvePreviewMaxWidth(_summary) != _openedPreviewMaxWidth)
				await ReopenPreviewAsync();
			ct.ThrowIfCancellationRequested();
			if (_previewFrameSize is null) return new AppPreviewOutcome(null, null, Strings.Benchmark_SkippedNotLoaded);

			double rate = _previewPlayer.PlaybackRate;
			_previewPlayer.SuppressRouteIntro(true);
			_previewPlayer.Play(PreviewTest.StartFor(position, _previewPlayer.Duration, rate, loop));
			ShowPlayingState(true);
			PreviewMeasurementResult? result = await PreviewTest.MeasureAsync(_previewPlayer, status, live, ct);
			if (result is null) return new AppPreviewOutcome(null, null, Strings.PreviewBenchmark_EndedDuringWarmup);

			string display = _fullscreenWindow is { } full ? DescribeScreen(Screens.ScreenFromWindow(full))
				: _viewportOnSecondScreen ? _secondScreenName : DescribeScreen(Screens.ScreenFromWindow(this));
			string mode = _fullscreenWindow is not null ? Strings.PreviewBenchmark_ModeFullscreen
				: _viewportOnSecondScreen ? Strings.PreviewBenchmark_ModeSecondScreen : Strings.PreviewBenchmark_ModeWindow;
			return new AppPreviewOutcome(result, PreviewReportContextFor(source, mode, display, rate, settings));
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			AppLogger.Warn(ex, "The performance test's playback in the app failed");
			return new AppPreviewOutcome(null, null, ex.Message);
		}
		finally
		{
			PausePlayback();
			_previewPlayer.SuppressRouteIntro(false);
			foreach (Window window in windows)
			{
				window.RemoveHandler(KeyDownEvent, BlockKeys);
				window.RemoveHandler(PointerPressedEvent, BlockPointer);
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
				_previewTestRunning = false;
			}

			if (wasPlaying && !_closing)
			{
				_previewPlayer.Play(position);
				ShowPlayingState(true);
			}
		}
	}

	/// <summary>`paths` as the recording, read the usual way (Get Summary) - in place of whatever was added.</summary>
	private async Task LoadInputsAsync(IReadOnlyList<string> paths)
	{
		ClosePreview();
		_inputPaths.Clear();
		_inputPaths.AddRange(paths);
		RefreshInputFilesList();
		OutputPathBox.Text = RenderOptions.DefaultOutputPath(_inputPaths, OverlaySettingsStore.Load().DefaultOutputFolder);
		if (paths.SequenceEqual(SyntheticRecording.Existing() ?? [])) AppendLog(string.Format(Strings.Benchmark_LoadedTestRecording, SyntheticRecording.Folder));
		await RunGetSummaryAsync();
	}

	/// <summary>
	///     Tools' "Load into the app": the test recording as the main window's recording - asked first when other files are
	///     added, since their cuts and the rest go with them. False when the user kept them, or a render is running.
	/// </summary>
	private async Task<bool> LoadTestRecordingAsync(IReadOnlyList<string> paths)
	{
		if (_phase == UiPhase.Rendering)
		{
			await ConfirmDialog.ShowAsync(this, Strings.Tools_TestRecording, Strings.Benchmark_WhileRendering, kind: DialogKind.Warning);
			return false;
		}

		if (_inputPaths.Count > 0 && !_inputPaths.SequenceEqual(paths) &&
		    !await ConfirmDialog.AskAsync(this, Strings.Tools_TestRecording, Strings.Tools_ReplaceRecording, Strings.Tools_LoadTestRecording))
			return false;

		await LoadInputsAsync(paths);
		return true;
	}

	private PreviewReportContext PreviewReportContextFor(string source, string mode, string display, double rate, OverlaySettings settings)
	{
		Window viewportWindow = GetTopLevel(PreviewVideo) as Window ?? this;
		double scaling = viewportWindow.RenderScaling;
		return new PreviewReportContext(source, mode, display, _previewFrameSize, PreviewVideo.Bounds.Width * scaling,
			PreviewVideo.Bounds.Height * scaling, scaling, rate, _previewPlayer.DecoderDescription, _showOverlay,
			settings.PreviewDrawsShadows && !settings.DisableShadows);
	}
}
