using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Preview;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Updates;
using OsmoOverlay.Gui.Native;
using RenderOptions = OsmoOverlay.Core.RenderOptions;

namespace OsmoOverlay.Gui;

/// <summary>
///     Which of UpdatePreviewGuides' two guide layers (rule-of-thirds lines, safe-margin box) the
///     preview toolbar's grid button currently shows - independent of SnapToGuides, which has its own toggle.
/// </summary>
public enum PreviewGridMode
{
	Off,
	Thirds,
	Margin,
	Both
}

/// <summary>
///     Window shell: input file list, the Get Summary/Render action flow, and phase-driven panel
///     visibility. The rest of this window's logic is split across the sibling MainWindow.*.cs
///     partial-class files by concern.
/// </summary>
public partial class MainWindow : Window
{
	private readonly List<string> _inputPaths = [];
	private readonly PreviewPlayer _previewPlayer = new();
	private string _activePresetId = "";

	private CancellationTokenSource? _cts;
	private string _detectedEncoder = "";
	private Point _dragAnchorOffset;
	private string? _draggingElementId;
	// Id of whichever element's settings panel is currently populated/shown in the left column's inline
	// widget-settings view - every field-changed handler in that panel targets this instance rather than
	// a fixed OverlayElementType, since a type can have several instances on the canvas at once (see
	// OnWidgetGearHoverButtonClick).
	private string? _editingElementId;

	// Everything available (nothing greyed out) until a file's actually been read - RefreshWidgetList only
	// starts using it once _summary is set, so the default only matters for that brief gap.
	private OverlayAvailability _availability = new(true, true, true);
	private string? _hoveredElementId;
	private TimeSpan _previewPosition;
	// The frame at _previewPosition (FrameAt) - the time readout and the status strip both show it.
	private long _previewFrame;
	private (PreviewTimeFormat Format, TimeSpan Duration, FileSummary? Summary, string Text)? _timeEndText;
	private List<OverlayPreset> _overlayPresets = [];
	// SetPhase(SummaryReady) can run before LoadOverlayPresets finishes. New/Duplicate on the empty list would make
	// OverlayPresetStore.Save's cleanup delete every other preset file, so OverlayContent stays hidden until this is true.
	private bool _overlayPresetsLoaded;
	private UiPhase _phase = UiPhase.Idle;
	// The preview's frame size in pixels - null while no recording is open in the preview.
	private PixelSize? _previewFrameSize;
	private int _previewMaxWidth;
	private string? _selectedElementId;
	private PreviewGridMode _gridMode;
	private bool _snapToGuides;
	private PreviewTimeFormat _timeFormat;
	private string? _resizingElementId;
	private float _resizeStartScale = 1f;
	private double _resizeStartDistance = 1;
	private bool _showWatermark;
	private bool _smoothGpsMotion;
	private FileSummary? _summary;
	// The input set SupportNotice was last shown for (ShowSupportNoticeAsync).
	private string? _supportNoticeShownFor;
	private bool _suppressOverlayEvents;
	private bool _suppressTimelineEvent;
	private bool _timelineScrubbing;

	/// <summary>A .ovproj the app was started with - opened once the window is up.</summary>
	public string? StartupProject { get; init; }

	public MainWindow()
	{
		InitializeComponent();
		Opened += OnWindowOpened;

		OverlaySettings settings = OverlaySettingsStore.Load();
		RestorePlacement(settings);
		_previewMaxWidth = settings.PreviewMaxWidth;
		_gridMode = Enum.TryParse(settings.PreviewGridMode, out PreviewGridMode loadedGridMode) ? loadedGridMode : PreviewGridMode.Both;
		_snapToGuides = settings.PreviewSnapToGrid;
		_timeFormat = PreviewTimeFormats.Parse(settings.PreviewTimeFormat);
		_audioMuted = settings.PreviewAudioMuted;
		_audioVolume = settings.PreviewAudioVolume;
		_showWatermark = settings.ShowWatermark;
		_smoothGpsMotion = settings.SmoothGpsMotion;

		DateTimeFormatCombo.ItemsSource = DateFormatOptions;
		DateTimeLocaleCombo.ItemsSource = LocaleOptions;
		UtcTimeFormatCombo.ItemsSource = DateFormatOptions;
		UtcTimeLocaleCombo.ItemsSource = LocaleOptions;
		TripStatCombo.ItemsSource = TripStatOptions;
		PresetComboBox.ItemTemplate = PresetItemTemplate();

		ElementTimingEditor[] timingEditors =
		[
			DateTimeTiming, UtcTimeTiming, CameraInfoTiming, CompassTiming, MapTiming,
			SpeedTiming, RollTiming, PitchTiming, SunTiming, GMeterTiming, ElapsedTimeTiming,
			CameraModelTiming, TripProgressBarTiming, ElevationTiming, GradientTiming, DistanceTiming,
			ProfileChartTiming, TripStatTiming, TextTiming, ImageTiming
		];
		foreach (ElementTimingEditor timing in timingEditors) timing.TimingChanged += OnElementTimingChanged;

		ElementShadowEditor[] shadowEditors =
		[
			DateTimeShadow, UtcTimeShadow, CameraInfoShadow, CompassShadow, MapShadow,
			SpeedShadow, RollShadow, PitchShadow, SunShadow, GMeterShadow, ElapsedTimeShadow,
			CameraModelShadow, TripProgressBarShadow, ElevationShadow, GradientShadow, DistanceShadow,
			ProfileChartShadow, TripStatShadow, TextShadow, ImageShadow
		];
		foreach (ElementShadowEditor shadow in shadowEditors)
		{
			shadow.ShadowChanged += OnElementShadowChanged;
			shadow.ApplyToAllRequested += ApplyShadowToAll;
		}

		ElementPanelEditor[] panelEditors =
		[
			SunPanelFill, RollPanelFill, PitchPanelFill, GMeterPanelFill, SpeedPanelFill,
			CompassPanelFill, TripProgressBarPanelFill, ProfileChartPanelFill
		];
		foreach (ElementPanelEditor panel in panelEditors)
		{
			panel.PanelChanged += OnElementPanelChanged;
			panel.ApplyToAllRequested += ApplyPanelToAll;
		}
		MapSourceEditor.SelectionChanged += OnMapSourceChanged;
		foreach (TrailStyleEditor trail in new[] { CompassTrail, MapTrail }) trail.Changed += OnTrailStyleChanged;
		foreach (MarkerStyleEditor marker in new[] { CompassMarker, MapMarker }) marker.Changed += OnMarkerStyleChanged;
		MapSourceEditor.SharedChanged += OnMapSourcesShared;
		_presetSaveDelay.Tick += (_, _) => SaveOverlayPresets();

		// Compass/MapWidget/TripProgressBar/Image have no text of their own to style, so no Style editor.
		ElementStyleEditor[] styleEditors =
		[
			DateTimeStyle, UtcTimeStyle, CameraInfoStyle, SpeedStyle, RollStyle, PitchStyle, SunStyle,
			GMeterStyle, ElapsedTimeStyle, CameraModelStyle, ElevationStyle, GradientStyle, DistanceStyle,
			ProfileChartStyle, TripStatStyle, TextStyle
		];
		foreach (ElementStyleEditor style in styleEditors)
		{
			style.StyleChanged += OnElementStyleChanged;
			style.ApplyToAllRequested += ApplyStyleToAll;
		}

		// Bounds pulled from Core's own clamps (RouteMapMosaic.BuildAsync, OverlayRenderer's
		// MapDynamicZoomMaxFactorMin/Max) instead of separate hardcoded Minimum/Maximum literals in
		// XAML, so the two can't silently drift apart if either constant ever changes.
		MapZoomBox.Minimum = RouteMapMosaic.MinZoom;
		MapZoomBox.Maximum = RouteMapMosaic.MaxZoom;
		MapZoomOutMaxBox.Minimum = (decimal)OverlayRenderer.MapDynamicZoomMaxFactorMin;
		MapZoomOutMaxBox.Maximum = (decimal)OverlayRenderer.MapDynamicZoomMaxFactorMax;
		SpeedMaxBox.Minimum = (decimal)OverlayRenderer.SpeedGaugeMaxMin;
		SpeedMaxBox.Maximum = (decimal)OverlayRenderer.SpeedGaugeMaxMax;
		SpeedMaxBox.Increment = (decimal)OverlayRenderer.SpeedGaugeMaxStep;

		WireCuts();
		WireMoments();
		WirePreviewTimeline();
		WireAudio();
		WirePreviewQuality();
		WireSpeed();
		WireTimelineTracks();
		WireLayers();
		WirePreviewZoom();
		WirePreviewShortcuts();
		WirePreviewFullscreen();
		WireSecondScreen();
		WireAutoSave();
		WireReframe();
		WireDeveloperWindow();

		_previewPlayer.FrameReady += OnPreviewFrameReady;
		_previewPlayer.PlaybackStarted += OnPreviewPlaybackStarted;
		PreviewVideo.PlaybackFrameShown += OnPlaybackFrameShown;
		_previewPlayer.PlaybackStopped += OnPreviewPlaybackStopped;
		// Map tile progress arrives from thread-pool threads (see OverlayRenderer.BuildMapMosaicAsync).
		_previewPlayer.Message += message => Dispatcher.UIThread.Post(() => AppendLog(message));

		// See AppLogger.Notified. AppendLogLine (not AppendLog) doesn't log again - the raising call already did.
		AppLogger.Notified += (message, level) => Dispatcher.UIThread.Post(() =>
			AppendLogLine(message, level switch
			{
				AppLogLevel.Warn => LogLevel.Warn,
				AppLogLevel.Error => LogLevel.Error,
				_ => LogLevel.Info
			}));

		// The rule-of-thirds/safe-margin guide lines are positioned in absolute canvas pixels, so a
		// window resize (which resizes OverlayDragCanvas itself, independent of when a new preview
		// bitmap loads) needs to redraw them.
		OverlayDragCanvas.SizeChanged += (_, _) =>
		{
			ApplyPreviewLayout();
			QueueAutoQuality();
		};

		// XAML hardcodes the "Both"/snap-on look as a starting point for the designer - reconcile the
		// toolbar buttons with whatever was actually loaded from settings.json above.
		ApplyGridModeButtonClasses();
		ToggleSnapButton.Classes.Set("active", _snapToGuides);
		BuildTimeFormatMenu();
	}

	private async void OnWindowOpened(object? sender, EventArgs e)
	{
		Opened -= OnWindowOpened;

		AppendBanner();
		_ = Task.Run(FfmpegPipeline.DeleteStaleTempFiles);

		// Only a missing required tool brings the prompt up - it then offers the missing optional ones alongside.
		IReadOnlyList<ExternalTool> missing = DependencyChecker.FindMissing(RequiredTools.All);
		if (missing.Any(t => !t.IsOptional)) await new DependencyPromptWindow(missing).ShowDialog(this);

		bool openRecording = false;
		if (!OverlaySettingsStore.Load().WelcomeShown)
		{
			var welcome = new WelcomeWindow(StartupProject is null);
			await welcome.ShowDialog(this);
			openRecording = welcome.OpenRecordingRequested;
			OverlaySettings welcomed = OverlaySettingsStore.Load();
			_showWatermark = welcomed.ShowWatermark;
			_smoothGpsMotion = welcomed.SmoothGpsMotion;
		}

		OverlaySettings startup = OverlaySettingsStore.Load();
		if (startup.LoopByDefault && !_loopEnabled) ToggleLoop();
		if (startup.SecondScreenEnabled) OpenSecondScreen();
		ApplyAutoSave(startup.AutoSaveMinutes);

		if (openRecording) await AddInputFilesAsync();
		else if (StartupProject is { } project) await OpenProjectAsync(project);
		else if (startup is { ReopenLastProject: true, LastProject: { } last })
		{
			if (File.Exists(last))
			{
				AppendLog($"Reopening the last project: {last}");
				await OpenProjectAsync(last);
			}
			else
			{
				AppendLog($"The last project is gone: {last}", LogLevel.Warn);
			}
		}

		await OfferAppUpdateAsync(await UpdateChecks.Latest);
	}

	/// <summary>
	///     After the startup update check (UpdateChecks, which logs every result - dependency updates are left to Settings'
	///     About tab): asks about a new version of the app, unless this one was declined before.
	/// </summary>
	private async Task OfferAppUpdateAsync(UpdateCheckResult check)
	{
		if (!check.AppUpdateAvailable) return;

		AppRelease release = check.App!;
		OverlaySettings settings = OverlaySettingsStore.Load();
		if (settings.SkippedAppUpdate == release.Version.ToString()) return;

		bool canInstall = AppUpdates.CanUpdateInPlace(release);
		bool accepted = await ConfirmDialog.AskAsync(this, "Update available",
			$"OsmoOverlay {release.Version} is available - you have {AppUpdates.CurrentVersion}.\n\n" +
			(canInstall
				? "Update now? OsmoOverlay will download it, close, install it and start again."
				: "Open the download page?") +
			"\n\nIf you cancel, you won't be asked about this version again - it stays available in Settings > About.",
			canInstall ? "Update" : "Open GitHub", DialogKind.Info);
		if (!accepted)
		{
			OverlaySettingsStore.Save(settings with { SkippedAppUpdate = release.Version.ToString() });
			return;
		}

		TaskbarProgress.SetState(this, TaskbarProgress.State.Normal);
		bool updating = await AppUpdateFlow.UpdateAsync(this, release, () => _phase == UiPhase.Rendering, status => AppendLog(status),
			share => TaskbarProgress.SetValue(this, (ulong)(share * 1000), 1000), false);
		if (!updating) TaskbarProgress.SetState(this, TaskbarProgress.State.NoProgress);
	}

	private async void OnAddInputFilesClick(object? sender, RoutedEventArgs e)
	{
		await AddInputFilesAsync();
	}

	private async Task AddInputFilesAsync()
	{
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Select recording(s) - pick several segments of one recording to stitch them together",
			AllowMultiple = true,
			FileTypeFilter =
			[
				new FilePickerFileType("Camera recordings") { Patterns = ["*.mp4", "*.MP4", "*.insv", "*.INSV", "*.lrv", "*.LRV"] },
				new FilePickerFileType("All files") { Patterns = ["*"] }
			]
		});

		if (files.Count == 0) return;

		bool wasEmpty = _inputPaths.Count == 0;
		// The same file twice would get stitched (and rendered) twice.
		_inputPaths.AddRange(files.Select(f => f.Path.LocalPath)
			.Where(p => !_inputPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
		RefreshInputFilesList();

		if (wasEmpty)
			OutputPathBox.Text = RenderOptions.DefaultOutputPath(_inputPaths, OverlaySettingsStore.Load().DefaultOutputFolder);

		ClosePreview();
		SetPhase(UiPhase.Idle);
		ActionButton.IsEnabled = _inputPaths.Count > 0;
	}

	private void OnMoveInputUpClick(object? sender, RoutedEventArgs e)
	{
		int index = InputFilesList.SelectedIndex;
		if (index <= 0) return;

		(_inputPaths[index - 1], _inputPaths[index]) = (_inputPaths[index], _inputPaths[index - 1]);
		RefreshInputFilesList();
		InputFilesList.SelectedIndex = index - 1;
		ResetAfterInputOrderChange();
	}

	private void OnMoveInputDownClick(object? sender, RoutedEventArgs e)
	{
		int index = InputFilesList.SelectedIndex;
		if (index < 0 || index >= _inputPaths.Count - 1) return;

		(_inputPaths[index + 1], _inputPaths[index]) = (_inputPaths[index], _inputPaths[index + 1]);
		RefreshInputFilesList();
		InputFilesList.SelectedIndex = index + 1;
		ResetAfterInputOrderChange();
	}

	/// <summary>
	///     The loaded summary's telemetry is stitched in the old segment order - rendering it against the
	///     reordered files would put the overlay out of sync with the video, so Get Summary has to run again.
	/// </summary>
	private void ResetAfterInputOrderChange()
	{
		if (_phase != UiPhase.SummaryReady) return;

		ClosePreview();
		SetPhase(UiPhase.Idle);
	}

	private void OnRemoveInputClick(object? sender, RoutedEventArgs e)
	{
		int index = InputFilesList.SelectedIndex;
		if (index < 0) return;

		_inputPaths.RemoveAt(index);
		RefreshInputFilesList();

		ClosePreview();
		SetPhase(UiPhase.Idle);
		ActionButton.IsEnabled = _inputPaths.Count > 0;
	}

	/// <summary>
	///     The loaded recording's sustained top speed as the GPS measured it (SpeedCalibration.SustainedTopSpeedKmh) - processed
	///     like the preview (GPS smoothing included) but without the correction - for the speed calibration.
	/// </summary>
	private double? RecordingTopSpeedKmh()
	{
		if (_summary?.TelemetryFrames is not { Count: > 0 } frames) return null;

		double top = SpeedCalibration.SustainedTopSpeedKmh(TelemetryProcessor.Process(frames, _summary.CameraFormat, _smoothGpsMotion));
		return top > 0 ? top : null;
	}

	/// <summary>The loaded recording's top speed as the overlay shows it: the highest sample, with the correction.</summary>
	private double? RecordingPeakSpeedKmh()
	{
		if (_summary?.TelemetryFrames is not { Count: > 0 } frames) return null;

		double peak = TelemetryProcessor.Process(frames, _summary.CameraFormat, _smoothGpsMotion).Max(f => f.SpeedKmh);
		return peak > 0 ? peak : null;
	}

	private void RefreshInputFilesList()
	{
		InputFilesList.ItemsSource = _inputPaths.Select(Path.GetFileName).ToList();
	}

	private async void OnSettingsClick(object? sender, RoutedEventArgs e)
	{
		OverlaySettings currentSettings = OverlaySettingsStore.Load();
		var settings = new SettingsWindow(_showWatermark, _smoothGpsMotion, RouteIntroSettings.From(currentSettings))
			{ IsRendering = () => _phase == UiPhase.Rendering, RecordingTopSpeedKmh = RecordingTopSpeedKmh() };
		settings.LoadExportSettings(currentSettings);
		await settings.ShowDialog(this);

		// Export options only matter at render time (RenderJob reads them from settings.json itself) and the Interface
		// ones apply at startup or live (the time format), so unlike the settings below they never reopen the preview.
		OverlaySettings beforeExportChanges = OverlaySettingsStore.Load();
		OverlaySettings withExportChanges = settings.ApplyExportSettings(beforeExportChanges);
		if (withExportChanges != beforeExportChanges) OverlaySettingsStore.Save(withExportChanges);
		bool interfaceScaleChanged = Math.Abs(withExportChanges.InterfaceScale - beforeExportChanges.InterfaceScale) > 0.001;
		SetTimeFormat(PreviewTimeFormats.Parse(withExportChanges.PreviewTimeFormat), false);
		ApplyLayerRows(withExportChanges.LayerRowsVisible);
		ApplySecondScreenSettings(withExportChanges);
		ApplyAutoSave(withExportChanges.AutoSaveMinutes);

		if (settings.ShowWatermark != _showWatermark)
		{
			_showWatermark = settings.ShowWatermark;
			OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { ShowWatermark = _showWatermark });
			_previewPlayer.SetShowWatermark(_showWatermark);
		}

		// Saved unless the welcome window (opened from Settings) already saved its own value - then the Speed tab shows that one too.
		if (Math.Abs(settings.SpeedCorrectionPercent - OverlaySettingsStore.Load().SpeedCorrectionPercent) > 1e-9)
			OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { SpeedCorrectionPercent = settings.SpeedCorrectionPercent });

		// Unlike ShowWatermark, GPS smoothing and the speed correction are baked in at OpenAsync, so a change reopens the preview.
		bool needsPreviewReopen = Math.Abs(OverlaySettingsStore.Load().SpeedCorrectionPercent - currentSettings.SpeedCorrectionPercent) > 1e-9;
		if (settings.SmoothGpsMotion != _smoothGpsMotion)
		{
			_smoothGpsMotion = settings.SmoothGpsMotion;
			OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { SmoothGpsMotion = _smoothGpsMotion });
			needsPreviewReopen = true;
		}

		if (_summary is not null) ShowMaxSpeedInfo();

		// Also baked in at OpenAsync (the route-intro card). Loaded fresh, since the blocks above may have saved
		// already; OverlaySettings' structural equality then detects the whole group's change at once.
		OverlaySettings beforeMapAndRouteIntroChanges = OverlaySettingsStore.Load();
		OverlaySettings updatedSettings = beforeMapAndRouteIntroChanges with
		{
			ShowRouteIntro = settings.RouteIntro.Enabled,
			RouteIntroDurationSeconds = settings.RouteIntro.DurationSeconds,
			RouteIntroShowDistance = settings.RouteIntro.ShowDistance,
			RouteIntroShowMaxSpeed = settings.RouteIntro.ShowMaxSpeed,
			RouteIntroShowAvgSpeed = settings.RouteIntro.ShowAvgSpeed,
			RouteIntroShowDate = settings.RouteIntro.ShowDate,
			RouteIntroShowDuration = settings.RouteIntro.ShowDuration,
			RouteIntroShowCameraModel = settings.RouteIntro.ShowCameraModel,
			RouteIntroShowElevationGain = settings.RouteIntro.ShowElevationGain,
			RouteIntroUnits = settings.RouteIntro.Units,
			RouteIntroColorBySpeed = settings.RouteIntro.ColorBySpeed,
			RouteIntroMapProvider = settings.RouteIntro.MapProviderId
		};
		if (updatedSettings != beforeMapAndRouteIntroChanges)
		{
			OverlaySettingsStore.Save(updatedSettings);
			needsPreviewReopen = true;
		}

		if (needsPreviewReopen) await ReopenPreviewAsync();
		// The picker in Settings writes the shared keys itself - the open preview picks them up here.
		else OnMapSourcesShared();
		RefreshMapSourceEditor();

		if (_summary is not null && _phase == UiPhase.SummaryReady)
			PopulateOutputInfo(_summary, _detectedEncoder);

		if (interfaceScaleChanged)
		{
			// UiScale applies only at startup - with nothing loaded there's nothing to lose by restarting right away.
			if (_inputPaths.Count == 0) RestartApp();
			else AppLogger.Notify("The new interface scale applies after restarting the app");
		}
	}

	private static void RestartApp()
	{
		try
		{
			(string path, string[] args) = AppCommand.Current();
			Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = false })?.Dispose();
		}
		catch (Exception ex)
		{
			AppLogger.Error(ex, $"Could not restart the app: {ex.Message}");
			return;
		}

		AppLogger.Info("Restarting to apply the new interface scale");
		(Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
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
		UiPhase previous = _phase;
		_phase = phase;

		// The log while there's no recording on the timeline or a render runs; a freshly loaded summary brings back
		// the user's choice. After a render the log stays, showing how it went.
		if (phase != UiPhase.SummaryReady) SetTimelineExpanded(false, false);
		else if (previous == UiPhase.LoadingSummary) SetTimelineExpanded(_timelineWanted, false);

		bool summaryVisible = phase is UiPhase.SummaryReady or UiPhase.Rendering;
		SummaryPanel.IsVisible = summaryVisible;
		OutputInfoCard.IsVisible = summaryVisible;
		FileInfoCard.IsVisible = phase == UiPhase.SummaryReady;
		TelemetryCard.IsVisible = phase == UiPhase.SummaryReady && _summary?.Telemetry is not null;
		bool overlayReady = summaryVisible && _summary?.HasTelemetry == true && _overlayPresetsLoaded;
		OverlayContent.IsVisible = overlayReady;
		OverlayPlaceholder.IsVisible = !overlayReady;

		CancelButton.IsVisible = phase == UiPhase.Rendering;
		Progress.IsVisible = phase == UiPhase.Rendering;
		ActionButton.IsVisible = phase != UiPhase.Rendering;
		ActionButton.Content = phase == UiPhase.SummaryReady ? "Render" : "Get Summary";
		GreenScreenButton.IsVisible = phase == UiPhase.SummaryReady;

		bool busy = phase is UiPhase.LoadingSummary or UiPhase.Rendering;
		// Editing the input list mid-run would reset the phase under a running summary/render.
		InputPanel.IsEnabled = !busy;
		if (busy)
			ActionButton.IsEnabled = false;
		UpdateReframeControls();
	}

	private void AppendLog(string message, LogLevel level = LogLevel.Info)
	{
		LogBox.AppendLog(LogScroll, message, level);
		_secondScreen?.AppendLog(message, level);
	}

	/// <summary>A line AppLogger already logged - to the LOG panel and the second screen's, not again to the file.</summary>
	private void AppendLogLine(string message, LogLevel level)
	{
		LogBox.AppendLogLine(LogScroll, message, level);
		_secondScreen?.AppendLog(message, level);
	}

	private void ClearLogPanels()
	{
		LogBox.ClearLog();
		_secondScreen?.ClearLog();
	}

	/// <summary>The startup banner (AppBanner) in the LOG panel - and through it in app.log.</summary>
	private void AppendBanner()
	{
		foreach (string line in AppBanner.BuildLines("GUI"))
			AppendLog(line);
		AppendLog("");
	}

	protected override void OnClosed(EventArgs e)
	{
		if (_presetSaveDelay.IsEnabled) SaveOverlayPresets();
		SavePlacement();
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
