using Avalonia;
using Avalonia.Controls;
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

	// Default true (nothing greyed out) until a file's actually been read - RefreshWidgetList only
	// starts using these once _summary is set, so the default only matters for that brief gap.
	private bool _hasContainerTime = true;
	private bool _hasGpsFix = true;
	private bool _hasGpsTimestamp = true;
	private string? _hoveredElementId;
	private TimeSpan _previewPosition;
	private List<OverlayPreset> _overlayPresets = [];
	// Guards a real race: SetPhase(SummaryReady) can run before LoadOverlayPresets (an earlier await
	// in OpenPreviewAsync) has populated _overlayPresets. Without this, the Overlay panel's "New"/
	// "Duplicate" buttons become clickable in that gap and can create a preset against an empty list,
	// which OverlayPresetStore.Save's cleanup then treats as authoritative - deleting every other
	// preset file on disk, including default.json. Keeping OverlayContent hidden until this is true
	// closes the race at the source.
	private bool _overlayPresetsLoaded;
	private UiPhase _phase = UiPhase.Idle;
	// The preview's frame size in pixels - null while no recording is open in the preview.
	private PixelSize? _previewFrameSize;
	private int _previewMaxWidth;
	private string? _selectedElementId;
	private PreviewGridMode _gridMode;
	private bool _snapToGuides;
	private bool _preciseTime;
	private string? _resizingElementId;
	private float _resizeStartScale = 1f;
	private double _resizeStartDistance = 1;
	private bool _showWatermark;
	private bool _smoothGpsMotion;
	private FileSummary? _summary;
	private bool _suppressOverlayEvents;
	private bool _suppressTimelineEvent;
	private bool _timelineScrubbing;

	public MainWindow()
	{
		InitializeComponent();
		Opened += OnWindowOpened;

		OverlaySettings settings = OverlaySettingsStore.Load();
		_previewMaxWidth = settings.PreviewMaxWidth;
		_gridMode = Enum.TryParse(settings.PreviewGridMode, out PreviewGridMode loadedGridMode) ? loadedGridMode : PreviewGridMode.Both;
		_snapToGuides = settings.PreviewSnapToGrid;
		_preciseTime = settings.PreviewPreciseTime;
		_audioMuted = settings.PreviewAudioMuted;
		_audioVolume = settings.PreviewAudioVolume;
		_showWatermark = settings.ShowWatermark;
		_smoothGpsMotion = settings.SmoothGpsMotion;

		DateTimeFormatCombo.ItemsSource = DateFormatOptions;
		DateTimeLocaleCombo.ItemsSource = LocaleOptions;
		UtcTimeFormatCombo.ItemsSource = DateFormatOptions;
		UtcTimeLocaleCombo.ItemsSource = LocaleOptions;

		ElementTimingEditor[] timingEditors =
		[
			DateTimeTiming, UtcTimeTiming, CameraInfoTiming, CompassTiming, MapTiming,
			SpeedTiming, PitchTiming, SunTiming, GMeterTiming, ElapsedTimeTiming,
			CameraModelTiming, TripProgressBarTiming, ElevationTiming, GradientTiming, DistanceTiming
		];
		foreach (ElementTimingEditor timing in timingEditors) timing.TimingChanged += OnElementTimingChanged;

		// Compass/MapWidget/TripProgressBar have no text of their own to style, so no Style editor.
		ElementStyleEditor[] styleEditors =
		[
			DateTimeStyle, UtcTimeStyle, CameraInfoStyle, SpeedStyle, PitchStyle, SunStyle,
			GMeterStyle, ElapsedTimeStyle, CameraModelStyle, ElevationStyle, GradientStyle, DistanceStyle
		];
		foreach (ElementStyleEditor style in styleEditors) style.StyleChanged += OnElementStyleChanged;

		// Bounds pulled from Core's own clamps (RouteMapMosaic.BuildAsync, OverlayRenderer's
		// MapDynamicZoomMaxFactorMin/Max) instead of separate hardcoded Minimum/Maximum literals in
		// XAML, so the two can't silently drift apart if either constant ever changes.
		MapZoomBox.Minimum = RouteMapMosaic.MinZoom;
		MapZoomBox.Maximum = RouteMapMosaic.MaxZoom;
		MapZoomOutMaxBox.Minimum = (decimal)OverlayRenderer.MapDynamicZoomMaxFactorMin;
		MapZoomOutMaxBox.Maximum = (decimal)OverlayRenderer.MapDynamicZoomMaxFactorMax;

		WireCuts();
		WirePreviewTimeline();
		WireAudio();
		WirePreviewQuality();
		WireSpeed();
		WireTimelineTracks();
		WirePreviewZoom();
		WirePreviewShortcuts();

		_previewPlayer.FrameReady += OnPreviewFrameReady;
		_previewPlayer.PlaybackStarted += OnPreviewPlaybackStarted;
		PreviewVideo.PlaybackFrameShown += OnPlaybackFrameShown;
		_previewPlayer.PlaybackStopped += OnPreviewPlaybackStopped;
		// Map tile progress arrives from thread-pool threads (see OverlayRenderer.BuildMapMosaicAsync).
		_previewPlayer.Message += message => Dispatcher.UIThread.Post(() => AppendLog(message));

		// See AppLogger.Notified for the general contract. Concretely: ffmpeg/ffprobe/exiftool
		// invocations (from this window, ToolsWindow, or CompareVideosWindow), one-off status lines,
		// and every Warn/Error - a failed map tile fetch, a corrupt preset file, a failed render.
		// AppendLogLine (not AppendLog) skips re-logging
		// to AppLogger, since the call that raised Notified already did its own logging.
		AppLogger.Notified += (message, level) => Dispatcher.UIThread.Post(() =>
			LogBox.AppendLogLine(LogScroll, message, level switch
			{
				AppLogLevel.Warn => LogLevel.Warn,
				AppLogLevel.Error => LogLevel.Error,
				_ => LogLevel.Info
			}));

		// The rule-of-thirds/safe-margin guide lines are positioned in absolute canvas pixels, so a
		// window resize (which resizes OverlayDragCanvas itself, independent of when a new preview
		// bitmap loads) needs to redraw them.
		OverlayDragCanvas.SizeChanged += (_, _) => ApplyPreviewLayout();

		// XAML hardcodes the "Both"/snap-on look as a starting point for the designer - reconcile the
		// toolbar buttons with whatever was actually loaded from settings.json above.
		ApplyGridModeButtonClasses();
		ToggleSnapButton.Classes.Set("active", _snapToGuides);
		TogglePreciseTimeButton.Classes.Set("active", _preciseTime);
	}

	private async void OnWindowOpened(object? sender, EventArgs e)
	{
		Opened -= OnWindowOpened;

		AppendBanner();
		_ = Task.Run(FfmpegPipeline.DeleteStaleTempFiles);

		IReadOnlyList<ExternalTool> missing = DependencyChecker.FindMissing(RequiredTools.All);
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
		// The same file twice would get stitched (and rendered) twice.
		_inputPaths.AddRange(files.Select(f => f.Path.LocalPath)
			.Where(p => !_inputPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
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
		ResetAfterInputOrderChange();
	}

	private void OnMoveInputDownClick(object? sender, RoutedEventArgs e)
	{
		var index = InputFilesList.SelectedIndex;
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
		var settings = new SettingsWindow(_showWatermark, _smoothGpsMotion,
			currentSettings.MapTileUrlTemplate, currentSettings.MapAttribution, currentSettings.MapShowAttribution,
			currentSettings.MapApiKey, RouteIntroSettings.From(currentSettings)) { IsRendering = () => _phase == UiPhase.Rendering };
		settings.LoadExportSettings(currentSettings);
		await settings.ShowDialog(this);

		// Export options only matter at render time (RenderJob reads them from settings.json itself), so
		// unlike the preview-affecting settings below they never need the preview reopened.
		OverlaySettings beforeExportChanges = OverlaySettingsStore.Load();
		OverlaySettings withExportChanges = settings.ApplyExportSettings(beforeExportChanges);
		if (withExportChanges != beforeExportChanges) OverlaySettingsStore.Save(withExportChanges);

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

		// Same "baked in at OpenAsync, no live-swap path" category as SmoothGpsMotion
		// above - the map tile source and route-intro card are both fetched/laid out when the preview
		// (or a render) starts, not per frame. Reloaded fresh (not the pre-dialog currentSettings)
		// since the ShowWatermark/SmoothGpsMotion blocks above may have already saved
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
			RouteIntroUnits = settings.RouteIntro.Units,
			RouteIntroColorBySpeed = settings.RouteIntro.ColorBySpeed
		};
		if (updatedSettings != beforeMapAndRouteIntroChanges)
		{
			OverlaySettingsStore.Save(updatedSettings);
			needsPreviewReopen = true;
		}

		if (needsPreviewReopen) await ReopenPreviewAsync();

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

		var busy = phase is UiPhase.LoadingSummary or UiPhase.Rendering;
		// Editing the input list mid-run would reset the phase under a running summary/render.
		InputPanel.IsEnabled = !busy;
		if (busy)
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
