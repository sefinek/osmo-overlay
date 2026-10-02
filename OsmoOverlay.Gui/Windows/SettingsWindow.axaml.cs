using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Updates;
using OsmoOverlay.Gui.Native;

namespace OsmoOverlay.Gui;

public partial class SettingsWindow : Window
{
	private static readonly List<ChoiceOption<string>> NvencPresetOptions =
	[
		new("P7 - best quality (default)", "p7"),
		new("P6", "p6"),
		new("P5 - faster", "p5"),
		new("P4 - fastest reasonable", "p4")
	];

	private static readonly List<ChoiceOption<double>> BitrateOptions =
	[
		new("Same as source (default)", 1.0),
		new("1.25x source", 1.25),
		new("1.5x source", 1.5),
		new("2x source", 2.0)
	];

	private static readonly List<ChoiceOption<double>> InterfaceScaleOptions =
	[
		new("75%", 0.75),
		new("100% (default)", 1.0),
		new("125%", 1.25),
		new("150%", 1.5),
		new("175%", 1.75),
		new("200%", 2.0)
	];

	private static readonly List<ChoiceOption<int>> AutoSaveOptions =
	[
		new("Off (default)", 0),
		new("Every minute", 1),
		new("Every 2 minutes", 2),
		new("Every 5 minutes", 5),
		new("Every 10 minutes", 10)
	];

	/// <summary>The loaded recording's measured cruising speed (no correction), for the speed calibration; null when none is loaded.</summary>
	public double? RecordingCruisingSpeedKmh { get; init; }

	/// <summary>The speed correction as edited on the Speed tab - the main window saves it.</summary>
	public double SpeedCorrectionPercent => SpeedEditor.Percent;

	/// <summary>Whether the main window is rendering - an FFmpeg update that restarts the app is held off until it isn't.</summary>
	public Func<bool> IsRendering { get; init; } = () => false;

	public SettingsWindow()
	{
		InitializeComponent();
		NvencPresetCombo.ItemsSource = NvencPresetOptions;
		BitrateCombo.ItemsSource = BitrateOptions;
		InterfaceScaleCombo.ItemsSource = InterfaceScaleOptions;
		ThemeCombo.ItemsSource = AppThemes.Options;
		TimeFormatCombo.ItemsSource = PreviewTimeFormats.Options;
		AutoSaveCombo.ItemsSource = AutoSaveOptions;

		string appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
		AppVersionText.Text = $"OsmoOverlay v{appVersion}";
		SidebarVersionText.Text = $"OsmoOverlay v{appVersion}";

		string coreVersion = typeof(RenderJob).Assembly.GetName().Version?.ToString(3) ?? "?";
		CoreVersionText.Text = $"Core v{coreVersion}";
		DependencyStatusRows.ShowChecking(DependencyStatusGrid, RequiredTools.All);

		DateTime? configLastUpdatedUtc = OverlaySettingsStore.GetLastUpdatedUtc();
		ConfigLastUpdatedText.Text = configLastUpdatedUtc is { } utc
			? $"Config last updated: {utc.ToLocalTime():yyyy-MM-dd HH:mm}"
			: "Config last updated: never";
	}

	public SettingsWindow(bool showWatermark, bool smoothGpsMotion, RouteIntroSettings routeIntro) : this()
	{
		ShowWatermarkCheck.IsChecked = showWatermark;
		SmoothGpsMotionCheck.IsChecked = smoothGpsMotion;

		_routeIntroProvider = routeIntro.MapProviderId;
		RouteIntroMapSource.Load(routeIntro.MapProviderId, OverlaySettingsStore.Load());
		RouteIntroMapSource.SelectionChanged += provider => _routeIntroProvider = provider;
		ShowRouteIntroCheck.IsChecked = routeIntro.Enabled;
		RouteIntroOptionsPanel.IsEnabled = routeIntro.Enabled;
		RouteIntroDurationBox.Value = (decimal)routeIntro.DurationSeconds;
		RouteIntroDistanceCheck.IsChecked = routeIntro.ShowDistance;
		RouteIntroMaxSpeedCheck.IsChecked = routeIntro.ShowMaxSpeed;
		RouteIntroAvgSpeedCheck.IsChecked = routeIntro.ShowAvgSpeed;
		RouteIntroDateCheck.IsChecked = routeIntro.ShowDate;
		RouteIntroDurationCheck.IsChecked = routeIntro.ShowDuration;
		RouteIntroElevationGainCheck.IsChecked = routeIntro.ShowElevationGain;
		RouteIntroCameraModelCheck.IsChecked = routeIntro.ShowCameraModel;
		RouteIntroMetricRadio.IsChecked = routeIntro.Units == UnitSystem.Metric;
		RouteIntroImperialRadio.IsChecked = routeIntro.Units == UnitSystem.Imperial;
		RouteIntroColorBySpeedCheck.IsChecked = routeIntro.ColorBySpeed;
	}

	/// <summary>Fills the export options (Rendering category) - kept separate from the constructor's already long parameter list.</summary>
	public void LoadExportSettings(OverlaySettings settings)
	{
		SpeedEditor.Load(settings.SpeedCorrectionPercent, RecordingCruisingSpeedKmh);
		NvencPresetCombo.SelectedItem = NvencPresetOptions.FirstOrDefault(o => o.Value == settings.NvencPreset) ?? NvencPresetOptions[0];
		BitrateCombo.SelectedItem = BitrateOptions.FirstOrDefault(o => Math.Abs(o.Value - settings.OutputBitrateMultiplier) < 0.001)
		                            ?? BitrateOptions[0];
		HardwareDecodingCheck.IsChecked = settings.HardwareDecoding;
		FastStartCheck.IsChecked = settings.FastStart;
		MetadataTelemetryCheck.IsChecked = settings.MetadataKeepTelemetry;
		MetadataSerialCheck.IsChecked = settings.MetadataKeepSerialNumber;
		MetadataDebugCheck.IsChecked = settings.MetadataKeepDebugTrack;
		MetadataThumbnailsCheck.IsChecked = settings.MetadataKeepThumbnails;
		PreserveCameraMetadataCheck.IsChecked = settings.PreserveCameraMetadata;
		UpdateMetadataOptionsEnabled();
		string theme = AppThemes.Normalize(settings.AppTheme);
		ThemeCombo.SelectedItem = AppThemes.Options.First(o => o.Value == theme);
		InterfaceScaleCombo.SelectedItem = InterfaceScaleOptions.FirstOrDefault(o => Math.Abs(o.Value - settings.InterfaceScale) < 0.001)
		                                   ?? InterfaceScaleOptions[1];
		PreviewTimeFormat timeFormat = PreviewTimeFormats.Parse(settings.PreviewTimeFormat);
		TimeFormatCombo.SelectedItem = PreviewTimeFormats.Options.First(o => o.Value == timeFormat);
		RestoreWindowPlacementCheck.IsChecked = settings.RestoreWindowPlacement;
		LayerRowsBox.Value = Math.Clamp(settings.LayerRowsVisible, LayerTimeline.MinVisibleTracks, LayerTimeline.MaxVisibleTracks);
		LoadPreviewMonitors(settings.PreviewMonitor);
		LoadSecondScreenMonitors(settings.SecondScreenMonitor);
		SecondScreenCheck.IsChecked = settings.SecondScreenEnabled;
		SecondScreenMonitorCombo.IsEnabled = settings.SecondScreenEnabled;
		ReopenLastProjectCheck.IsChecked = settings.ReopenLastProject;
		AutoSaveCombo.SelectedItem = AutoSaveOptions.FirstOrDefault(o => o.Value == settings.AutoSaveMinutes) ?? AutoSaveOptions[0];
		LoopByDefaultCheck.IsChecked = settings.LoopByDefault;
		ConfirmCloseWhileRenderingCheck.IsChecked = settings.ConfirmCloseWhileRendering;
		ProjectFilesGroup.IsVisible = ProjectFileAssociation.IsSupported;
		ShowProjectAssociation();
	}

	/// <summary>The screens connected now - a chosen one that's gone (see MonitorChoice) shows as the main window's.</summary>
	private void LoadPreviewMonitors(string? chosen)
	{
		List<ChoiceOption<string?>> options = [new("Same screen as the main window (default)", null)];
		options.AddRange(Screens.All.Select((screen, index) => new ChoiceOption<string?>(MonitorChoice.Describe(screen, index), MonitorChoice.KeyOf(screen))));
		PreviewMonitorCombo.ItemsSource = options;
		PreviewMonitorCombo.SelectedItem = options.FirstOrDefault(o => o.Value == chosen) ?? options[0];
	}

	private void OnSecondScreenChanged(object? sender, RoutedEventArgs e)
	{
		SecondScreenMonitorCombo.IsEnabled = SecondScreenCheck.IsChecked == true;
	}

	private void LoadSecondScreenMonitors(string? chosen)
	{
		List<ChoiceOption<string?>> options = [new("First screen other than the main window's (default)", null)];
		options.AddRange(Screens.All.Select((screen, index) => new ChoiceOption<string?>(MonitorChoice.Describe(screen, index), MonitorChoice.KeyOf(screen))));
		SecondScreenMonitorCombo.ItemsSource = options;
		SecondScreenMonitorCombo.SelectedItem = options.FirstOrDefault(o => o.Value == chosen) ?? options[0];
	}

	private void OnTimeFormatChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (TimeFormatCombo.SelectedItem is not ChoiceOption<PreviewTimeFormat> option) return;

		TimeFormatHint.Text = option.Value switch
		{
			PreviewTimeFormat.Milliseconds => "The time readout next to the timeline, e.g. 01:23.456 - the format the cut editor uses.",
			PreviewTimeFormat.Timecode =>
				"The time readout next to the timeline as hours:minutes:seconds:frames from 00:00:00:00, e.g. 00:01:23:12 - drop-frame (;) at 29.97 and 59.94 fps, as an NLE numbers its timeline.",
			PreviewTimeFormat.CameraTimecode =>
				"The time readout next to the timeline as the timecode the camera wrote, the one an NLE shows as the clip's own - e.g. 09:59:56;00. A recording without one counts from 00:00:00:00.",
			_ => "The time readout next to the timeline, e.g. 01:23. Clicking the readout switches between the formats."
		};
	}

	private void OnMetadataOptionChanged(object? sender, RoutedEventArgs e)
	{
		UpdateMetadataOptionsEnabled();
	}

	/// <summary>
	///     The parts only apply while the master checkbox is on, and the serial number/device ID only exist
	///     inside the telemetry track and the thumbnails/info block - with neither kept there's nothing for
	///     that checkbox to keep or remove.
	/// </summary>
	private void UpdateMetadataOptionsEnabled()
	{
		bool enabled = PreserveCameraMetadataCheck.IsChecked == true;
		MetadataSerialCheck.IsEnabled = enabled &&
		                                (MetadataTelemetryCheck.IsChecked == true || MetadataThumbnailsCheck.IsChecked == true);

		List<string> kept = [];
		if (MetadataTelemetryCheck.IsChecked == true) kept.Add("telemetry");
		if (MetadataSerialCheck is { IsChecked: true, IsEnabled: true }) kept.Add("serial number");
		if (MetadataDebugCheck.IsChecked == true) kept.Add("debug track");
		if (MetadataThumbnailsCheck.IsChecked == true) kept.Add("thumbnails");
		string summary = kept.Count > 0 ? string.Join(", ", kept) : "nothing selected";
		MetadataPartsExpander.Header = $"What to keep: {summary}";
		MetadataPartsExpander.IsEnabled = enabled;
	}

	public OverlaySettings ApplyExportSettings(OverlaySettings settings)
	{
		return settings with
		{
			NvencPreset = (NvencPresetCombo.SelectedItem as ChoiceOption<string> ?? NvencPresetOptions[0]).Value,
			OutputBitrateMultiplier = (BitrateCombo.SelectedItem as ChoiceOption<double> ?? BitrateOptions[0]).Value,
			HardwareDecoding = HardwareDecodingCheck.IsChecked == true,
			FastStart = FastStartCheck.IsChecked == true,
			PreserveCameraMetadata = PreserveCameraMetadataCheck.IsChecked == true,
			MetadataKeepTelemetry = MetadataTelemetryCheck.IsChecked == true,
			MetadataKeepSerialNumber = MetadataSerialCheck.IsChecked == true,
			MetadataKeepDebugTrack = MetadataDebugCheck.IsChecked == true,
			MetadataKeepThumbnails = MetadataThumbnailsCheck.IsChecked == true,
			AppTheme = (ThemeCombo.SelectedItem as ChoiceOption<string>)?.Value ?? AppThemes.Default,
			InterfaceScale = (InterfaceScaleCombo.SelectedItem as ChoiceOption<double> ?? InterfaceScaleOptions[1]).Value,
			PreviewTimeFormat = (TimeFormatCombo.SelectedItem as ChoiceOption<PreviewTimeFormat> ?? PreviewTimeFormats.Options[0]).Value.ToString(),
			RestoreWindowPlacement = RestoreWindowPlacementCheck.IsChecked == true,
			PreviewMonitor = (PreviewMonitorCombo.SelectedItem as ChoiceOption<string?>)?.Value,
			SecondScreenEnabled = SecondScreenCheck.IsChecked == true,
			SecondScreenMonitor = (SecondScreenMonitorCombo.SelectedItem as ChoiceOption<string?>)?.Value,
			ReopenLastProject = ReopenLastProjectCheck.IsChecked == true,
			AutoSaveMinutes = (AutoSaveCombo.SelectedItem as ChoiceOption<int> ?? AutoSaveOptions[0]).Value,
			LoopByDefault = LoopByDefaultCheck.IsChecked == true,
			ConfirmCloseWhileRendering = ConfirmCloseWhileRenderingCheck.IsChecked == true,
			LayerRowsVisible = Math.Clamp((int)(LayerRowsBox.Value ?? LayerTimeline.DefaultVisibleTracks), LayerTimeline.MinVisibleTracks, LayerTimeline.MaxVisibleTracks)
		};
	}

	// Live, so the choice can be judged on the window it's made in; saved with the rest when Settings closes.
	private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (ThemeCombo.SelectedItem is ChoiceOption<string> option) AppThemes.Apply(option.Value);
	}

	public bool ShowWatermark => ShowWatermarkCheck.IsChecked == true;
	public bool SmoothGpsMotion => SmoothGpsMotionCheck.IsChecked == true;

	public RouteIntroSettings RouteIntro => new(
		ShowRouteIntroCheck.IsChecked == true,
		(double)(RouteIntroDurationBox.Value ?? 12),
		RouteIntroDistanceCheck.IsChecked == true,
		RouteIntroMaxSpeedCheck.IsChecked == true,
		RouteIntroAvgSpeedCheck.IsChecked == true,
		RouteIntroDateCheck.IsChecked == true,
		RouteIntroDurationCheck.IsChecked == true,
		RouteIntroCameraModelCheck.IsChecked == true,
		RouteIntroElevationGainCheck.IsChecked == true,
		RouteIntroImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric,
		RouteIntroColorBySpeedCheck.IsChecked == true,
		_routeIntroProvider);

	// What the route overview's picker last reported - the provider belongs to the card; the keys and the credit switch are shared and saved by the picker itself.
	private string? _routeIntroProvider;

	// Set once the About tab showed a check - switching back to it later doesn't show it again.
	private bool _updatesShown;

	private AppRelease? _latestRelease;

	/// <summary>
	///     Each category is its own ScrollViewer stacked in the same Grid cell (see SettingsWindow.axaml)
	///     - switching category just swaps which one is shown instead of reparenting content.
	///     CategoryList's SelectedIndex="0" in XAML fires this event during InitializeComponent, before
	///     the panel fields further down the visual tree have been assigned yet - harmless to skip then,
	///     since RenderingPanel is already the one shown by default in XAML (every other panel starts
	///     with the "hidden" class), matching SelectedIndex 0 without this handler's help.
	/// </summary>
	private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (RenderingPanel is null || SpeedPanel is null || RouteIntroPanel is null || InterfacePanel is null || BehaviorPanel is null ||
		    AboutPanel is null)
			return;

		// "hidden" fades and slides a page out (SettingsWindow.axaml); every page stays laid out, so nothing jumps.
		ScrollViewer[] pages = [RenderingPanel, SpeedPanel, RouteIntroPanel, InterfacePanel, BehaviorPanel, AboutPanel];
		for (int i = 0; i < pages.Length; i++) pages[i].Classes.Set("hidden", i != CategoryList.SelectedIndex);

		if (CategoryList.SelectedIndex == 5 && !_updatesShown)
		{
			_updatesShown = true;
			_ = ShowUpdatesAsync(UpdateChecks.Latest);
		}
	}

	private void OnCheckForUpdatesClick(object? sender, RoutedEventArgs e)
	{
		_ = ShowUpdatesAsync(UpdateChecks.RefreshAsync());
	}

	/// <summary>
	///     Shows an update check (UpdateChecks) - usually the one from startup, already done. The dependencies get no status
	///     line, the table shows each tool's installed/latest side by side; the app's status sits next to the button, which
	///     stays disabled while a check runs. Per-tool detail reaches the main window's LOG panel (AppLogger.Notify).
	/// </summary>
	private async Task ShowUpdatesAsync(Task<UpdateCheckResult> check)
	{
		CheckForUpdatesButton.IsEnabled = false;
		AppUpdateButton.IsVisible = false;
		AppUpdateText.Text = "Checking for a new version...";

		try
		{
			UpdateCheckResult result = await check;
			DependencyStatusRows.Populate(this, DependencyStatusGrid, result.Dependencies, IsRendering);
			ShowAppUpdate(result);
		}
		finally
		{
			CheckForUpdatesButton.IsEnabled = true;
		}
	}

	private void ShowAppUpdate(UpdateCheckResult result)
	{
		_latestRelease = result.App;
		if (result.AppCheckFailed)
		{
			AppUpdateText.Text = "Could not check for a new version.";
			return;
		}

		if (!result.AppUpdateAvailable)
		{
			AppUpdateText.Text = "You're using the latest version.";
			return;
		}

		AppRelease release = result.App!;
		AppUpdateText.Text = $"Version {release.Version} is available" +
		                     (release.PublishedAt is { } published ? $" (released {published.ToLocalTime():yyyy-MM-dd})." : ".");
		AppUpdateButton.Content = AppUpdates.CanUpdateInPlace(release) ? $"Update to {release.Version}" : "Download from GitHub";
		AppUpdateButton.IsVisible = true;
	}

	private async void OnAppUpdateClick(object? sender, RoutedEventArgs e)
	{
		if (_latestRelease is null) return;

		AppUpdateButton.IsEnabled = false;
		bool updating = await AppUpdateFlow.UpdateAsync(this, _latestRelease, IsRendering, status => AppUpdateText.Text = status,
			share => AppUpdateText.Text = $"Downloading OsmoOverlay {_latestRelease.Version}... {share * 100:0}%");
		if (!updating) AppUpdateButton.IsEnabled = true;
	}

	private async void OnWelcomeClick(object? sender, RoutedEventArgs e)
	{
		await new WelcomeWindow(false, RecordingCruisingSpeedKmh).ShowDialog(this);

		// The welcome window saved these itself; the checkboxes would otherwise write the old state back on close.
		OverlaySettings saved = OverlaySettingsStore.Load();
		ShowWatermarkCheck.IsChecked = saved.ShowWatermark;
		SmoothGpsMotionCheck.IsChecked = saved.SmoothGpsMotion;
		SpeedEditor.Load(saved.SpeedCorrectionPercent, RecordingCruisingSpeedKmh);
		ShowProjectAssociation();
	}

	private void ShowProjectAssociation()
	{
		bool registered = ProjectFileAssociation.IsRegistered();
		ProjectAssociationButton.Content = registered ? "Remove association" : "Associate .ovproj files";
		ProjectAssociationText.Text = registered ? "Registered for this app" : "Not registered";
	}

	private void OnProjectAssociationClick(object? sender, RoutedEventArgs e)
	{
		try
		{
			if (ProjectFileAssociation.IsRegistered()) ProjectFileAssociation.Unregister();
			else ProjectFileAssociation.Register();
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
		{
			AppLogger.Error(ex, $"Could not change the .ovproj association: {ex.Message}");
		}

		ShowProjectAssociation();
	}

	/// <summary>Every TextBlock.link on the About tab - the address is in its Tag.</summary>
	private void OnLinkClick(object? sender, PointerPressedEventArgs e)
	{
		if ((sender as Control)?.Tag is string url) AppUpdateFlow.OpenInBrowser(url);
	}

	private void OnShowRouteIntroChanged(object? sender, RoutedEventArgs e)
	{
		RouteIntroOptionsPanel.IsEnabled = ShowRouteIntroCheck.IsChecked == true;
	}
}

/// <summary>A labelled value for a settings ComboBox - ToString is what the ComboBox displays.</summary>
internal sealed record ChoiceOption<T>(string Label, T Value)
{
	public override string ToString()
	{
		return Label;
	}
}
