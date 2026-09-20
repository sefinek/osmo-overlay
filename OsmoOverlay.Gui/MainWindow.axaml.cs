using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Preview;
using RenderOptions = OsmoOverlay.Core.RenderOptions;

namespace OsmoOverlay.Gui;

/// <summary>
///     Window shell: input file list, the Get Summary/Render action flow, and phase-driven panel
///     visibility. The rest of this window's logic is split across sibling partial-class files by
///     concern - MainWindow.Summary.cs (Get Summary + Input/Telemetry/Output info cards),
///     MainWindow.Render.cs (running the render + completion dialog), MainWindow.Preview.cs (live
///     preview transport), MainWindow.OverlayEditor.cs (overlay preset/widget editing + canvas drag).
/// </summary>
public partial class MainWindow : Window
{
	private readonly List<string> _inputPaths = [];
	private readonly PreviewPlayer _previewPlayer = new();
	private string _activePresetId = "";

	private CancellationTokenSource? _cts;
	private string _detectedEncoder = "";
	private Point _dragAnchorOffset;
	private OverlayElementType? _draggingElementType;
	private int? _frameLimit;

	private List<(double Start, double End)> _gpsLossRanges = [];
	// Default true (nothing greyed out) until a file's actually been read - RefreshElementCheckboxes
	// only starts using these once _summary is set, so the default only matters for that brief gap.
	private bool _hasContainerTime = true;
	private bool _hasGpsFix = true;
	private bool _hasGpsTimestamp = true;
	private List<OverlayPreset> _overlayPresets = [];
	// Guards a real race: SetPhase(SummaryReady) can run before LoadOverlayPresets (an earlier await
	// in OpenPreviewAsync) has populated _overlayPresets. Without this, the Overlay panel's "New"/
	// "Duplicate" buttons become clickable in that gap and can create a preset against an empty list,
	// which OverlayPresetStore.Save's cleanup then treats as authoritative - deleting every other
	// preset file on disk, including default.json. Keeping OverlayContent hidden until this is true
	// closes the race at the source.
	private bool _overlayPresetsLoaded;
	private UiPhase _phase = UiPhase.Idle;
	private WriteableBitmap? _previewBitmap;
	private int _previewMaxWidth = OverlaySettingsStore.Load().PreviewMaxWidth;
	private bool _showWatermark = OverlaySettingsStore.Load().ShowWatermark;
	private bool _sliderDragInProgress;
	private bool _smoothGpsMotion = OverlaySettingsStore.Load().SmoothGpsMotion;
	private FileSummary? _summary;
	private bool _suppressOverlayEvents;
	private bool _suppressSliderEvent;

	public MainWindow()
	{
		InitializeComponent();
		Opened += OnWindowOpened;

		DateTimeFormatCombo.ItemsSource = DateFormatOptions;
		DateTimeLocaleCombo.ItemsSource = LocaleOptions;
		UtcTimeFormatCombo.ItemsSource = DateFormatOptions;
		UtcTimeLocaleCombo.ItemsSource = LocaleOptions;

		// Every widget gets the same Appear at/Disappear at/Animation/Duration controls (see
		// WireTiming) - unlike the type-specific settings above, there's nothing widget-specific about
		// timing, so this is one mechanical block instead of 15 near-identical constructors' worth of
		// per-widget code.
		DateTimeAnimationCombo.ItemsSource = AnimationOptions;
		UtcTimeAnimationCombo.ItemsSource = AnimationOptions;
		ElevationAnimationCombo.ItemsSource = AnimationOptions;
		GradientAnimationCombo.ItemsSource = AnimationOptions;
		DistanceAnimationCombo.ItemsSource = AnimationOptions;
		CameraInfoAnimationCombo.ItemsSource = AnimationOptions;
		CompassAnimationCombo.ItemsSource = AnimationOptions;
		SunAnimationCombo.ItemsSource = AnimationOptions;
		PitchAnimationCombo.ItemsSource = AnimationOptions;
		GMeterAnimationCombo.ItemsSource = AnimationOptions;
		ElapsedTimeAnimationCombo.ItemsSource = AnimationOptions;
		CameraModelAnimationCombo.ItemsSource = AnimationOptions;
		SpeedAnimationCombo.ItemsSource = AnimationOptions;
		MapAnimationCombo.ItemsSource = AnimationOptions;
		TripProgressBarAnimationCombo.ItemsSource = AnimationOptions;

		WireTiming(OverlayElementType.DateTimeText, DateTimeAppearAtBox, DateTimeDisappearAtBox, DateTimeAnimationCombo, DateTimeAnimationDurationBox, DateTimeAnimationDurationPanel);
		WireTiming(OverlayElementType.UtcTimeText, UtcTimeAppearAtBox, UtcTimeDisappearAtBox, UtcTimeAnimationCombo, UtcTimeAnimationDurationBox, UtcTimeAnimationDurationPanel);
		WireTiming(OverlayElementType.Elevation, ElevationAppearAtBox, ElevationDisappearAtBox, ElevationAnimationCombo, ElevationAnimationDurationBox, ElevationAnimationDurationPanel);
		WireTiming(OverlayElementType.Gradient, GradientAppearAtBox, GradientDisappearAtBox, GradientAnimationCombo, GradientAnimationDurationBox, GradientAnimationDurationPanel);
		WireTiming(OverlayElementType.Distance, DistanceAppearAtBox, DistanceDisappearAtBox, DistanceAnimationCombo, DistanceAnimationDurationBox, DistanceAnimationDurationPanel);
		WireTiming(OverlayElementType.CameraInfo, CameraInfoAppearAtBox, CameraInfoDisappearAtBox, CameraInfoAnimationCombo, CameraInfoAnimationDurationBox, CameraInfoAnimationDurationPanel);
		WireTiming(OverlayElementType.Compass, CompassAppearAtBox, CompassDisappearAtBox, CompassAnimationCombo, CompassAnimationDurationBox, CompassAnimationDurationPanel);
		WireTiming(OverlayElementType.SunWidget, SunAppearAtBox, SunDisappearAtBox, SunAnimationCombo, SunAnimationDurationBox, SunAnimationDurationPanel);
		WireTiming(OverlayElementType.PitchGauge, PitchAppearAtBox, PitchDisappearAtBox, PitchAnimationCombo, PitchAnimationDurationBox, PitchAnimationDurationPanel);
		WireTiming(OverlayElementType.GMeter, GMeterAppearAtBox, GMeterDisappearAtBox, GMeterAnimationCombo, GMeterAnimationDurationBox, GMeterAnimationDurationPanel);
		WireTiming(OverlayElementType.ElapsedTimeText, ElapsedTimeAppearAtBox, ElapsedTimeDisappearAtBox, ElapsedTimeAnimationCombo, ElapsedTimeAnimationDurationBox, ElapsedTimeAnimationDurationPanel);
		WireTiming(OverlayElementType.CameraModelText, CameraModelAppearAtBox, CameraModelDisappearAtBox, CameraModelAnimationCombo, CameraModelAnimationDurationBox, CameraModelAnimationDurationPanel);
		WireTiming(OverlayElementType.SpeedGauge, SpeedAppearAtBox, SpeedDisappearAtBox, SpeedAnimationCombo, SpeedAnimationDurationBox, SpeedAnimationDurationPanel);
		WireTiming(OverlayElementType.MapWidget, MapAppearAtBox, MapDisappearAtBox, MapAnimationCombo, MapAnimationDurationBox, MapAnimationDurationPanel);
		WireTiming(OverlayElementType.TripProgressBar, TripProgressBarAppearAtBox, TripProgressBarDisappearAtBox, TripProgressBarAnimationCombo, TripProgressBarAnimationDurationBox, TripProgressBarAnimationDurationPanel);

		// Bounds pulled from Core's own clamps (RouteMapMosaic.BuildAsync, OverlayRenderer's
		// MapDynamicZoomMaxFactorMin/Max) instead of separate hardcoded Minimum/Maximum literals in
		// XAML, so the two can't silently drift apart if either constant ever changes.
		MapZoomBox.Minimum = RouteMapMosaic.MinZoom;
		MapZoomBox.Maximum = RouteMapMosaic.MaxZoom;
		MapZoomOutMaxBox.Minimum = (decimal)OverlayRenderer.MapDynamicZoomMaxFactorMin;
		MapZoomOutMaxBox.Maximum = (decimal)OverlayRenderer.MapDynamicZoomMaxFactorMax;

		_previewPlayer.FrameReady += OnPreviewFrameReady;
		_previewPlayer.PlaybackStopped += OnPreviewPlaybackStopped;
		_previewPlayer.Message += message => AppendLog(message);

		// See AppLogger.Notified for the general contract. Concretely: ffmpeg/ffprobe/exiftool
		// invocations (from this window, ToolsWindow, or CompareVideosWindow), one-off status lines,
		// and now every Warn/Error too - a failed map tile fetch, a corrupt preset file, a failed
		// render - previously visible only in app.log. AppendLogLine (not AppendLog) skips re-logging
		// to AppLogger, since the call that raised Notified already did its own logging.
		AppLogger.Notified += (message, level) => Dispatcher.UIThread.Post(() =>
			LogBox.AppendLogLine(LogScroll, message, level switch
			{
				AppLogLevel.Warn => LogLevel.Warn,
				AppLogLevel.Error => LogLevel.Error,
				_ => LogLevel.Info
			}));

		// The canvas has no width until layout runs (and resizes with the window afterwards) - marks
		// are positioned in absolute pixels, so they need redrawing whenever that width changes.
		GpsLossCanvas.SizeChanged += (_, _) => DrawGpsLossMarks();
	}

	private async void OnWindowOpened(object? sender, EventArgs e)
	{
		Opened -= OnWindowOpened;

		AppendBanner();

		IReadOnlyList<ExternalTool> missing = await DependencyChecker.FindMissingAsync(RequiredTools.All);
		if (missing.Count == 0) return;

		await new DependencyPromptWindow(missing).ShowDialog(this);
	}

	private async void OnAddInputFilesClick(object? sender, RoutedEventArgs e)
	{
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Select DJI Osmo Action recording(s) - pick several segments to stitch them together",
			AllowMultiple = true,
			FileTypeFilter = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4", "*.MP4"] }]
		});

		if (files.Count == 0) return;

		var wasEmpty = _inputPaths.Count == 0;
		_inputPaths.AddRange(files.Select(f => f.Path.LocalPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
		RefreshInputFilesList();

		if (wasEmpty)
			OutputPathBox.Text = RenderOptions.DefaultOutputPath(_inputPaths);

		ClosePreview();
		SetPhase(UiPhase.Idle);
		ActionButton.IsEnabled = _inputPaths.Count > 0;
	}

	private void OnMoveInputUpClick(object? sender, RoutedEventArgs e)
	{
		var index = InputFilesList.SelectedIndex;
		if (index <= 0) return;

		(_inputPaths[index - 1], _inputPaths[index]) = (_inputPaths[index], _inputPaths[index - 1]);
		RefreshInputFilesList();
		InputFilesList.SelectedIndex = index - 1;
	}

	private void OnMoveInputDownClick(object? sender, RoutedEventArgs e)
	{
		var index = InputFilesList.SelectedIndex;
		if (index < 0 || index >= _inputPaths.Count - 1) return;

		(_inputPaths[index + 1], _inputPaths[index]) = (_inputPaths[index], _inputPaths[index + 1]);
		RefreshInputFilesList();
		InputFilesList.SelectedIndex = index + 1;
	}

	private void OnRemoveInputClick(object? sender, RoutedEventArgs e)
	{
		var index = InputFilesList.SelectedIndex;
		if (index < 0) return;

		_inputPaths.RemoveAt(index);
		RefreshInputFilesList();

		ClosePreview();
		SetPhase(UiPhase.Idle);
		ActionButton.IsEnabled = _inputPaths.Count > 0;
	}

	private void RefreshInputFilesList()
	{
		InputFilesList.ItemsSource = _inputPaths.Select(Path.GetFileName).ToList();
	}

	private async void OnSettingsClick(object? sender, RoutedEventArgs e)
	{
		OverlaySettings currentSettings = OverlaySettingsStore.Load();
		var settings = new SettingsWindow(_frameLimit, _showWatermark, _smoothGpsMotion, _previewMaxWidth,
			currentSettings.MapTileUrlTemplate, currentSettings.MapAttribution, currentSettings.MapShowAttribution,
			currentSettings.MapApiKey, RouteIntroSettings.From(currentSettings));
		await settings.ShowDialog(this);
		_frameLimit = settings.FrameLimit;

		if (settings.ShowWatermark != _showWatermark)
		{
			_showWatermark = settings.ShowWatermark;
			OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { ShowWatermark = _showWatermark });
			_previewPlayer.SetShowWatermark(_showWatermark);
		}

		// Unlike ShowWatermark, this changes the actual per-frame position data the renderer was built
		// with (see PreviewPlayer.OpenAsync), not just a draw-time flag - there's no cheap "swap it live"
		// path, so a changed setting only takes effect on the next preview open. Reopen here instead of
		// leaving the user staring at a preview that doesn't match the checkbox they just changed.
		var needsPreviewReopen = false;
		if (settings.SmoothGpsMotion != _smoothGpsMotion)
		{
			_smoothGpsMotion = settings.SmoothGpsMotion;
			OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { SmoothGpsMotion = _smoothGpsMotion });
			needsPreviewReopen = true;
		}

		// Also can't be swapped live - it's the resolution VideoFrameSource decodes ffmpeg output at
		// (MainWindow.OpenPreviewAsync), baked into the running preview's decoder process and bitmap.
		if (settings.PreviewMaxWidth != _previewMaxWidth)
		{
			_previewMaxWidth = settings.PreviewMaxWidth;
			OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { PreviewMaxWidth = _previewMaxWidth });
			needsPreviewReopen = true;
		}

		// Same "baked in at OpenAsync, no live-swap path" category as SmoothGpsMotion/PreviewMaxWidth
		// above - the map tile source and route-intro card are both fetched/laid out when the preview
		// (or a render) starts, not per frame. Reloaded fresh (not the pre-dialog currentSettings)
		// since the ShowWatermark/SmoothGpsMotion/PreviewMaxWidth blocks above may have already saved
		// their own changes to disk; OverlaySettings' structural equality then does this whole group's
		// change-detection in one comparison instead of one `if` per field.
		OverlaySettings beforeMapAndRouteIntroChanges = OverlaySettingsStore.Load();
		OverlaySettings updatedSettings = beforeMapAndRouteIntroChanges with
		{
			MapTileUrlTemplate = settings.MapTileUrlTemplate,
			MapAttribution = settings.MapAttribution,
			MapShowAttribution = settings.MapShowAttribution,
			MapApiKey = settings.MapApiKey,
			ShowRouteIntro = settings.RouteIntro.Enabled,
			RouteIntroDurationSeconds = settings.RouteIntro.DurationSeconds,
			RouteIntroShowDistance = settings.RouteIntro.ShowDistance,
			RouteIntroShowMaxSpeed = settings.RouteIntro.ShowMaxSpeed,
			RouteIntroShowAvgSpeed = settings.RouteIntro.ShowAvgSpeed,
			RouteIntroShowDate = settings.RouteIntro.ShowDate,
			RouteIntroShowDuration = settings.RouteIntro.ShowDuration,
			RouteIntroShowCameraModel = settings.RouteIntro.ShowCameraModel,
			RouteIntroShowElevationGain = settings.RouteIntro.ShowElevationGain,
			RouteIntroUnits = settings.RouteIntro.Units
		};
		if (updatedSettings != beforeMapAndRouteIntroChanges)
		{
			OverlaySettingsStore.Save(updatedSettings);
			needsPreviewReopen = true;
		}

		if (needsPreviewReopen && _summary is { HasTelemetry: true } summary && _phase == UiPhase.SummaryReady)
			await OpenPreviewAsync(summary);

		if (_summary is not null && _phase == UiPhase.SummaryReady)
			PopulateOutputInfo(_summary, _detectedEncoder);
	}

	private async void OnToolsClick(object? sender, RoutedEventArgs e)
	{
		await new ToolsWindow().ShowDialog(this);
	}

	private async void OnPickOutputClick(object? sender, RoutedEventArgs e)
	{
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Save as",
			SuggestedFileName = string.IsNullOrWhiteSpace(OutputPathBox.Text)
				? "output_overlay.mp4"
				: Path.GetFileName(OutputPathBox.Text),
			DefaultExtension = "mp4",
			FileTypeChoices = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4"] }]
		});

		if (file is not null)
			OutputPathBox.Text = file.Path.LocalPath;
	}

	private async void OnActionClick(object? sender, RoutedEventArgs e)
	{
		switch (_phase)
		{
			case UiPhase.Idle:
				await RunGetSummaryAsync();
				break;
			case UiPhase.SummaryReady:
				await RunRenderAsync(false);
				break;
		}
	}

	private async void OnGreenScreenRenderClick(object? sender, RoutedEventArgs e)
	{
		await RunRenderAsync(true);
	}

	private void SetPhase(UiPhase phase)
	{
		_phase = phase;

		var summaryVisible = phase is UiPhase.SummaryReady or UiPhase.Rendering;
		SummaryPanel.IsVisible = summaryVisible;
		OutputInfoCard.IsVisible = summaryVisible;
		FileInfoCard.IsVisible = phase == UiPhase.SummaryReady;
		TelemetryCard.IsVisible = phase == UiPhase.SummaryReady && _summary?.Telemetry is not null;
		var overlayReady = summaryVisible && _summary?.HasTelemetry == true && _overlayPresetsLoaded;
		OverlayContent.IsVisible = overlayReady;
		OverlayPlaceholder.IsVisible = !overlayReady;

		CancelButton.IsVisible = phase == UiPhase.Rendering;
		Progress.IsVisible = phase == UiPhase.Rendering;
		ActionButton.IsVisible = phase != UiPhase.Rendering;
		ActionButton.Content = phase == UiPhase.SummaryReady ? "Render" : "Get Summary";
		GreenScreenButton.IsVisible = phase == UiPhase.SummaryReady;

		if (phase is UiPhase.LoadingSummary or UiPhase.Rendering)
			ActionButton.IsEnabled = false;
	}

	private void AppendLog(string message, LogLevel level = LogLevel.Info)
	{
		LogBox.AppendLog(LogScroll, message, level);
	}

	/// <summary>Same banner text the CLI prints to console (see AppBanner), shown in the GUI's own log so both surfaces show the same startup info.</summary>
	private void AppendBanner()
	{
		foreach (var line in AppBanner.BuildLines("GUI"))
			AppendLog(line);
		AppendLog("");
	}

	protected override void OnClosed(EventArgs e)
	{
		_previewPlayer.Dispose();
		base.OnClosed(e);
	}

	private enum UiPhase
	{
		Idle,
		LoadingSummary,
		SummaryReady,
		Rendering
	}
}
