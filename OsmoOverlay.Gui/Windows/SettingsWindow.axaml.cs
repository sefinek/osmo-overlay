using System.Reflection;
using System.Security;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Updates;
using OsmoOverlay.Gui.Native;

namespace OsmoOverlay.Gui;

public partial class SettingsWindow : Window
{
	private static readonly List<ChoiceOption<string>> NvencPresetOptions =
	[
		new(Strings.Settings_NvencP7, "p7"),
		new("P6", "p6"),
		new(Strings.Settings_NvencP5, "p5"),
		new(Strings.Settings_NvencP4, "p4")
	];

	private static readonly List<ChoiceOption<double>> BitrateOptions =
	[
		new(Strings.Settings_BitrateSame, 1.0),
		new(string.Format(Strings.Settings_BitrateTimes, 1.25), 1.25),
		new(string.Format(Strings.Settings_BitrateTimes, 1.5), 1.5),
		new(string.Format(Strings.Settings_BitrateTimes, 2), 2.0)
	];

	private static readonly List<ChoiceOption<int?>> OutputResolutionOptions =
	[
		new(Strings.Settings_SameAsSource, null),
		.. OutputVideo.Resolutions.Select(r => new ChoiceOption<int?>(r == 2160 ? "2160p (4K)" : $"{r}p", r))
	];

	private static readonly List<ChoiceOption<string?>> OutputCodecOptions =
	[
		new(Strings.Settings_CodecSameAsSource, null),
		new("H.264", "h264"),
		new("HEVC (H.265)", "hevc")
	];

	private static readonly List<ChoiceOption<double>> InterfaceScaleOptions =
	[
		new("75%", 0.75),
		new(Strings.Settings_Scale100, 1.0),
		new("125%", 1.25),
		new("150%", 1.5),
		new("175%", 1.75),
		new("200%", 2.0)
	];

	private static readonly List<ChoiceOption<int>> AutoSaveOptions =
	[
		new(Strings.Settings_AutoSaveOff, 0),
		new(Plural.Format(Strings.Settings_AutoSaveEvery, 1), 1),
		new(Plural.Format(Strings.Settings_AutoSaveEvery, 2), 2),
		new(Plural.Format(Strings.Settings_AutoSaveEvery, 5), 5),
		new(Plural.Format(Strings.Settings_AutoSaveEvery, 10), 10)
	];

	private static readonly List<ChoiceOption<int>> CacheCleanupOptions =
	[
		new(Strings.Settings_CacheCleanupNever, 0),
		new(Plural.Format(Strings.Settings_CacheCleanupAfter, 7), 7),
		new(Plural.Format(Strings.Settings_CacheCleanupAfter, 14), 14),
		new(string.Format(Strings.Settings_CacheCleanupDefault, 30), 30),
		new(Plural.Format(Strings.Settings_CacheCleanupAfter, 90), 90)
	];

	private static readonly List<ChoiceOption<string?>> LanguageOptions =
	[
		new(string.Format(Strings.Settings_LanguageSystem, UiLanguages.NativeName(UiLanguages.Resolve(null))), null),
		.. UiLanguages.Supported.Select(code => new ChoiceOption<string?>(UiLanguages.NativeName(code), code))
	];

	/// <summary>The loaded recording's measured cruising speed (no correction), for the speed calibration; null when none is loaded.</summary>
	public double? RecordingCruisingSpeedKmh { get; init; }

	/// <summary>Whether the main window is rendering - an FFmpeg update that restarts the app is held off until it isn't.</summary>
	public Func<bool> IsRendering { get; init; } = () => false;

	/// <summary>Opens the performance test over the given window - MainWindow's, which knows the loaded recording.</summary>
	public Func<Window, Task>? OpenBenchmark { get; init; }

	/// <summary>For the first-run window opened from here: the Map widget's own provider now, and how to give it back to the default style.</summary>
	public Func<string?>? MapWidgetProvider { get; init; }

	public Action? ClearMapWidgetProvider { get; init; }

	public SettingsWindow()
	{
		InitializeComponent();
		NvencPresetCombo.ItemsSource = NvencPresetOptions;
		BitrateCombo.ItemsSource = BitrateOptions;
		OutputResolutionCombo.ItemsSource = OutputResolutionOptions;
		OutputCodecCombo.ItemsSource = OutputCodecOptions;
		InterfaceScaleCombo.ItemsSource = InterfaceScaleOptions;
		LanguageCombo.ItemsSource = LanguageOptions;
		ThemeCombo.ItemsSource = AppThemes.Options;
		TimeFormatCombo.ItemsSource = PreviewTimeFormats.Options;
		AutoSaveCombo.ItemsSource = AutoSaveOptions;
		// The preview's own switch has nothing to change while every shadow is off.
		DisableShadowsCheck.IsCheckedChanged += (_, _) => PreviewShadowsCheck.IsEnabled = DisableShadowsCheck.IsChecked != true;
		CacheCleanupCombo.ItemsSource = CacheCleanupOptions;
		_pages = [RenderingPanel, SpeedPanel, RouteIntroPanel, InterfacePanel, BehaviorPanel, AboutPanel];
		RouteIntroMapSource.SelectionChanged += provider => _routeIntroProvider = provider;

		string appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
		AppVersionText.Text = SidebarVersionText.Text = $"OsmoOverlay v{appVersion}";

		string coreVersion = typeof(RenderJob).Assembly.GetName().Version?.ToString(3) ?? "?";
		CoreVersionText.Text = string.Format(Strings.Settings_CoreVersion, coreVersion);
		DependencyStatusRows.ShowChecking(DependencyStatusGrid, RequiredTools.All);

		DateTime? configLastUpdatedUtc = OverlaySettingsStore.GetLastUpdatedUtc();
		ConfigLastUpdatedText.Text = configLastUpdatedUtc is { } utc
			? string.Format(Strings.Settings_ConfigLastUpdatedAt, utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
			: Strings.Settings_ConfigNeverUpdated;
	}

	/// <summary>Fills every page from the settings. RecordingCruisingSpeedKmh has to be set before.</summary>
	public void Load(OverlaySettings settings)
	{
		ShowWatermarkCheck.IsChecked = settings.ShowWatermark;
		SmoothGpsMotionCheck.IsChecked = settings.SmoothGpsMotion;
		SpeedEditor.Load(settings.SpeedCorrectionPercent, RecordingCruisingSpeedKmh);
		LoadRouteIntro(settings);
		LoadExportSettings(settings);
	}

	/// <summary>The settings with everything edited here written in - the reverse of Load.</summary>
	public OverlaySettings Apply(OverlaySettings settings)
	{
		return RouteIntro.ApplyTo(ApplyExportSettings(settings)) with
		{
			ShowWatermark = ShowWatermarkCheck.IsChecked == true,
			SmoothGpsMotion = SmoothGpsMotionCheck.IsChecked == true,
			SpeedCorrectionPercent = SpeedEditor.Percent
		};
	}

	private void LoadRouteIntro(OverlaySettings settings)
	{
		var routeIntro = RouteIntroSettings.From(settings);
		_routeIntroProvider = routeIntro.MapProviderId;
		RouteIntroMapSource.Load(routeIntro.MapProviderId, settings);
		ShowRouteIntroCheck.IsChecked = routeIntro.Enabled;
		RouteIntroOptionsPanel.IsEnabled = routeIntro.Enabled;
		RouteIntroDurationBox.Value = (decimal)routeIntro.DurationSeconds;
		LoadRouteIntroStats(routeIntro);
		RouteIntroMetricRadio.IsChecked = routeIntro.Units == UnitSystem.Metric;
		RouteIntroImperialRadio.IsChecked = routeIntro.Units == UnitSystem.Imperial;
		RouteIntroColorBySpeedCheck.IsChecked = routeIntro.ColorBySpeed;
		RouteIntroStartFinishCheck.IsChecked = routeIntro.ShowStartFinish;
		RouteIntroEndLabels.IsEnabled = routeIntro.ShowStartFinish;
		RouteIntroStartLabelBox.Text = routeIntro.StartLabel;
		RouteIntroFinishLabelBox.Text = routeIntro.FinishLabel;
	}

	private void LoadExportSettings(OverlaySettings settings)
	{
		NvencPresetCombo.SelectedItem = NvencPresetOptions.FirstOrDefault(o => o.Value == settings.NvencPreset) ?? NvencPresetOptions[0];
		BitrateCombo.SelectedItem = BitrateOptions.FirstOrDefault(o => Math.Abs(o.Value - settings.OutputBitrateMultiplier) < 0.001)
		                            ?? BitrateOptions[0];
		OutputResolutionCombo.SelectedItem = OutputResolutionOptions.FirstOrDefault(o => o.Value == settings.OutputResolution) ?? OutputResolutionOptions[0];
		OutputCodecCombo.SelectedItem = OutputCodecOptions.FirstOrDefault(o => o.Value == settings.OutputCodec) ?? OutputCodecOptions[0];
		OutputEightBitCheck.IsChecked = settings.OutputEightBit;
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
		LanguageCombo.SelectedItem = LanguageOptions.FirstOrDefault(o => o.Value == settings.UiLanguage) ?? LanguageOptions[0];
		PreviewTimeFormat timeFormat = PreviewTimeFormats.Parse(settings.PreviewTimeFormat);
		TimeFormatCombo.SelectedItem = PreviewTimeFormats.Options.First(o => o.Value == timeFormat);
		RestoreWindowPlacementCheck.IsChecked = settings.RestoreWindowPlacement;
		LayerRowsBox.Value = Math.Clamp(settings.LayerRowsVisible, LayerTimeline.MinVisibleTracks, LayerTimeline.MaxVisibleTracks);
		LoadMonitors(PreviewMonitorCombo, Strings.Settings_MonitorSameAsMain, settings.PreviewMonitor);
		LoadMonitors(SecondScreenMonitorCombo, Strings.Settings_MonitorFirstOther, settings.SecondScreenMonitor);
		SecondScreenCheck.IsChecked = settings.SecondScreenEnabled;
		SecondScreenMonitorCombo.IsEnabled = settings.SecondScreenEnabled;
		ReopenLastProjectCheck.IsChecked = settings.ReopenLastProject;
		AutoSaveCombo.SelectedItem = AutoSaveOptions.FirstOrDefault(o => o.Value == settings.AutoSaveMinutes) ?? AutoSaveOptions[0];
		CacheCleanupCombo.SelectedItem = CacheCleanupOptions.FirstOrDefault(o => o.Value == settings.CacheCleanupDays) ??
		                                 CacheCleanupOptions.First(o => o.Value == new OverlaySettings().CacheCleanupDays);
		LoopByDefaultCheck.IsChecked = settings.LoopByDefault;
		PreviewShadowsCheck.IsChecked = settings.PreviewShadows;
		DisableShadowsCheck.IsChecked = settings.DisableShadows;
		ConfirmCloseWhileRenderingCheck.IsChecked = settings.ConfirmCloseWhileRendering;
		ProjectFilesGroup.IsVisible = ProjectFileAssociation.IsSupported;
		ShowProjectAssociation();
	}

	/// <summary>The screens connected now - a chosen one that's gone (see MonitorChoice) shows as the default.</summary>
	private void LoadMonitors(ComboBox combo, string defaultLabel, string? chosen)
	{
		List<ChoiceOption<string?>> options = [new(defaultLabel, null)];
		options.AddRange(Screens.All.Select((screen, index) => new ChoiceOption<string?>(MonitorChoice.Describe(screen, index), MonitorChoice.KeyOf(screen))));
		combo.ItemsSource = options;
		combo.SelectedItem = options.FirstOrDefault(o => o.Value == chosen) ?? options[0];
	}

	private void OnSecondScreenChanged(object? sender, RoutedEventArgs e)
	{
		SecondScreenMonitorCombo.IsEnabled = SecondScreenCheck.IsChecked == true;
	}

	private void OnTimeFormatChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (TimeFormatCombo.SelectedItem is not ChoiceOption<PreviewTimeFormat> option) return;

		TimeFormatHint.Text = option.Value switch
		{
			PreviewTimeFormat.Milliseconds => Strings.Settings_TimeFormatMillisecondsHint,
			PreviewTimeFormat.Timecode => Strings.Settings_TimeFormatTimecodeHint,
			PreviewTimeFormat.CameraTimecode => Strings.Settings_TimeFormatCameraTimecodeHint,
			_ => Strings.Settings_TimeFormatSecondsHint
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
		if (MetadataTelemetryCheck.IsChecked == true) kept.Add(Strings.Settings_KeepTelemetry);
		if (MetadataSerialCheck is { IsChecked: true, IsEnabled: true }) kept.Add(Strings.Settings_KeepSerial);
		if (MetadataDebugCheck.IsChecked == true) kept.Add(Strings.Settings_KeepDebugTrack);
		if (MetadataThumbnailsCheck.IsChecked == true) kept.Add(Strings.Settings_KeepThumbnails);
		string summary = kept.Count > 0 ? string.Join(", ", kept) : Strings.Settings_KeepNothing;
		MetadataPartsExpander.Header = string.Format(Strings.Settings_WhatToKeep, summary);
		MetadataPartsExpander.IsEnabled = enabled;
	}

	private OverlaySettings ApplyExportSettings(OverlaySettings settings)
	{
		return settings with
		{
			NvencPreset = (NvencPresetCombo.SelectedItem as ChoiceOption<string> ?? NvencPresetOptions[0]).Value,
			OutputBitrateMultiplier = (BitrateCombo.SelectedItem as ChoiceOption<double> ?? BitrateOptions[0]).Value,
			OutputResolution = (OutputResolutionCombo.SelectedItem as ChoiceOption<int?>)?.Value,
			OutputCodec = (OutputCodecCombo.SelectedItem as ChoiceOption<string?>)?.Value,
			OutputEightBit = OutputEightBitCheck.IsChecked == true,
			HardwareDecoding = HardwareDecodingCheck.IsChecked == true,
			FastStart = FastStartCheck.IsChecked == true,
			PreserveCameraMetadata = PreserveCameraMetadataCheck.IsChecked == true,
			MetadataKeepTelemetry = MetadataTelemetryCheck.IsChecked == true,
			MetadataKeepSerialNumber = MetadataSerialCheck.IsChecked == true,
			MetadataKeepDebugTrack = MetadataDebugCheck.IsChecked == true,
			MetadataKeepThumbnails = MetadataThumbnailsCheck.IsChecked == true,
			AppTheme = (ThemeCombo.SelectedItem as ChoiceOption<string>)?.Value ?? AppThemes.Default,
			InterfaceScale = (InterfaceScaleCombo.SelectedItem as ChoiceOption<double> ?? InterfaceScaleOptions[1]).Value,
			UiLanguage = (LanguageCombo.SelectedItem as ChoiceOption<string?>)?.Value,
			PreviewTimeFormat = (TimeFormatCombo.SelectedItem as ChoiceOption<PreviewTimeFormat> ?? PreviewTimeFormats.Options[0]).Value.ToString(),
			RestoreWindowPlacement = RestoreWindowPlacementCheck.IsChecked == true,
			PreviewMonitor = (PreviewMonitorCombo.SelectedItem as ChoiceOption<string?>)?.Value,
			SecondScreenEnabled = SecondScreenCheck.IsChecked == true,
			SecondScreenMonitor = (SecondScreenMonitorCombo.SelectedItem as ChoiceOption<string?>)?.Value,
			ReopenLastProject = ReopenLastProjectCheck.IsChecked == true,
			AutoSaveMinutes = (AutoSaveCombo.SelectedItem as ChoiceOption<int> ?? AutoSaveOptions[0]).Value,
			CacheCleanupDays = (CacheCleanupCombo.SelectedItem as ChoiceOption<int>)?.Value ?? settings.CacheCleanupDays,
			LoopByDefault = LoopByDefaultCheck.IsChecked == true,
			PreviewShadows = PreviewShadowsCheck.IsChecked == true,
			DisableShadows = DisableShadowsCheck.IsChecked == true,
			ConfirmCloseWhileRendering = ConfirmCloseWhileRenderingCheck.IsChecked == true,
			LayerRowsVisible = Math.Clamp((int)(LayerRowsBox.Value ?? LayerTimeline.DefaultVisibleTracks), LayerTimeline.MinVisibleTracks, LayerTimeline.MaxVisibleTracks)
		};
	}

	// Live, so the choice can be judged on the window it's made in; saved with the rest when Settings closes.
	private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (ThemeCombo.SelectedItem is ChoiceOption<string> option) AppThemes.Apply(option.Value);
	}

	private RouteIntroSettings RouteIntro => new(
		ShowRouteIntroCheck.IsChecked == true,
		(double)(RouteIntroDurationBox.Value ?? 12),
		_routeIntroStatRows.Where(r => r.Value.Check.IsChecked == true).Aggregate(RouteIntroStats.None, (all, r) => all | r.Key),
		RouteIntroImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric,
		RouteIntroColorBySpeedCheck.IsChecked == true,
		_routeIntroProvider,
		RouteIntroStartFinishCheck.IsChecked == true,
		RouteIntroStartLabelBox.Text?.Trim() ?? "",
		RouteIntroFinishLabelBox.Text?.Trim() ?? "",
		RouteIntroLabels.From(_routeIntroStatRows.ToDictionary(r => r.Key.ToString(), r => r.Value.Label.Text ?? "")));

	// One row per stat the card can show: whether it does, and its caption on the film (blank = the default, shown faded).
	private readonly Dictionary<RouteIntroStats, (CheckBox Check, TextBox Label)> _routeIntroStatRows = [];

	private void LoadRouteIntroStats(RouteIntroSettings routeIntro)
	{
		if (_routeIntroStatRows.Count == 0)
		{
			foreach (RouteIntroStats stat in RouteIntroStat.All)
			{
				int row = RouteIntroStatsGrid.RowDefinitions.Count;
				RouteIntroStatsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
				var check = new CheckBox { Content = StatName(stat), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
				var label = new TextBox { PlaceholderText = RouteIntroStat.DefaultLabel(stat), MaxLength = 40 };
				check.IsCheckedChanged += (_, _) => label.IsEnabled = check.IsChecked == true;
				Grid.SetRow(check, row);
				Grid.SetRow(label, row);
				Grid.SetColumn(label, 1);
				RouteIntroStatsGrid.Children.Add(check);
				RouteIntroStatsGrid.Children.Add(label);
				_routeIntroStatRows[stat] = (check, label);
			}
		}

		foreach ((RouteIntroStats stat, (CheckBox check, TextBox label)) in _routeIntroStatRows)
		{
			check.IsChecked = routeIntro.Shows(stat);
			label.IsEnabled = routeIntro.Shows(stat);
			label.Text = routeIntro.Labels?.Texts.GetValueOrDefault(stat.ToString());
		}
	}

	private static string StatName(RouteIntroStats stat)
	{
		return stat switch
		{
			RouteIntroStats.Distance => Strings.Common_TotalDistance,
			RouteIntroStats.ElevationGain => Strings.Settings_ElevationGain,
			RouteIntroStats.MaxSpeed => Strings.Common_MaxSpeed,
			RouteIntroStats.AverageSpeed => Strings.Settings_AverageSpeed,
			RouteIntroStats.Date => Strings.Settings_Date,
			RouteIntroStats.Duration => Strings.Settings_RecordingDuration,
			RouteIntroStats.MovingTime => Strings.Editor_StatMovingTime,
			RouteIntroStats.ElevationLoss => Strings.Editor_StatElevationLoss,
			RouteIntroStats.HighestPoint => Strings.Settings_HighestPoint,
			RouteIntroStats.LowestPoint => Strings.Settings_LowestPoint,
			RouteIntroStats.MaxLean => Strings.Settings_MaxLean,
			RouteIntroStats.MaxGForce => Strings.Settings_MaxGForce,
			RouteIntroStats.CameraModel => Strings.Common_CameraModel,
			_ => stat.ToString()
		};
	}

	// What the route overview's picker last reported - the provider belongs to the card; the keys and the credit switch are shared and saved by the picker itself.
	private string? _routeIntroProvider;

	// Set once the About tab showed a check - switching back to it later doesn't show it again.
	private bool _updatesShown;

	private AppRelease? _latestRelease;

	// The category pages in CategoryList's order - null while InitializeComponent runs (see OnCategoryChanged).
	private readonly ScrollViewer[]? _pages;

	/// <summary>
	///     Each category is its own ScrollViewer stacked in the same Grid cell (see SettingsWindow.axaml)
	///     - switching category just swaps which one is visible instead of reparenting content.
	///     CategoryList's SelectedIndex="0" in XAML fires this event during InitializeComponent, before
	///     the pages are known - harmless to skip then, since RenderingPanel is already the one visible by
	///     default in XAML (every other panel starts with IsVisible="False"), matching SelectedIndex 0.
	/// </summary>
	private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_pages is null) return;

		for (int i = 0; i < _pages.Length; i++) _pages[i].IsVisible = i == CategoryList.SelectedIndex;

		if (AboutPanel.IsVisible && !_updatesShown)
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
		AppUpdateText.Text = Strings.Settings_CheckingForANewVersion;

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
			AppUpdateText.Text = Strings.Settings_UpdateCheckFailed;
			return;
		}

		if (!result.AppUpdateAvailable)
		{
			AppUpdateText.Text = Strings.Settings_UpToDate;
			return;
		}

		AppRelease release = result.App!;
		AppUpdateText.Text = release.PublishedAt is { } published
			? string.Format(Strings.Settings_VersionAvailableReleased, release.Version, published.ToLocalTime().ToString("yyyy-MM-dd"))
			: string.Format(Strings.Settings_VersionAvailable, release.Version);
		AppUpdateButton.Content = AppUpdates.CanUpdateInPlace(release)
			? string.Format(Strings.Settings_UpdateTo, release.Version)
			: Strings.Settings_DownloadFromGitHub;
		AppUpdateButton.IsVisible = true;
	}

	private async void OnAppUpdateClick(object? sender, RoutedEventArgs e)
	{
		if (_latestRelease is null) return;

		AppUpdateButton.IsEnabled = false;
		bool updating = await AppUpdateFlow.UpdateAsync(this, _latestRelease, IsRendering, status => AppUpdateText.Text = status,
			share => AppUpdateText.Text = string.Format(Strings.Settings_Downloading, _latestRelease.Version, share * 100));
		if (!updating) AppUpdateButton.IsEnabled = true;
	}

	private async void OnBenchmarkClick(object? sender, PointerPressedEventArgs e)
	{
		if (OpenBenchmark is null) return;

		await OpenBenchmark(this);

		// The test saved what it applied itself; the switches would otherwise write the old state back on close.
		OverlaySettings saved = OverlaySettingsStore.Load();
		HardwareDecodingCheck.IsChecked = saved.HardwareDecoding;
		PreviewShadowsCheck.IsChecked = saved.PreviewShadows;
	}

	private async void OnWelcomeClick(object? sender, RoutedEventArgs e)
	{
		await new WelcomeWindow(false, RecordingCruisingSpeedKmh)
			{ OpenBenchmark = OpenBenchmark, MapWidgetProvider = MapWidgetProvider?.Invoke(), ClearMapWidgetProvider = ClearMapWidgetProvider }.ShowDialog(this);

		// The welcome window saved these itself; the checkboxes would otherwise write the old state back on close.
		OverlaySettings saved = OverlaySettingsStore.Load();
		ShowWatermarkCheck.IsChecked = saved.ShowWatermark;
		SmoothGpsMotionCheck.IsChecked = saved.SmoothGpsMotion;
		SpeedEditor.Load(saved.SpeedCorrectionPercent, RecordingCruisingSpeedKmh);
		// The performance test it can open applies its suggestions straight to settings.json too.
		HardwareDecodingCheck.IsChecked = saved.HardwareDecoding;
		PreviewShadowsCheck.IsChecked = saved.PreviewShadows;
		ShowProjectAssociation();
	}

	private void ShowProjectAssociation()
	{
		bool registered = ProjectFileAssociation.IsRegistered();
		ProjectAssociationButton.Content = registered ? Strings.Settings_RemoveAssociation : Strings.Settings_Associate;
		ProjectAssociationText.Text = registered ? Strings.Settings_AssociationRegistered : Strings.Settings_AssociationNotRegistered;
	}

	private void OnProjectAssociationClick(object? sender, RoutedEventArgs e)
	{
		try
		{
			if (ProjectFileAssociation.IsRegistered()) ProjectFileAssociation.Unregister();
			else ProjectFileAssociation.Register();
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
		{
			AppLogger.Error(ex, string.Format(Strings.Settings_AssociationFailed, ex.Message));
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

	private void OnRouteIntroStartFinishChanged(object? sender, RoutedEventArgs e)
	{
		RouteIntroEndLabels.IsEnabled = RouteIntroStartFinishCheck.IsChecked == true;
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
