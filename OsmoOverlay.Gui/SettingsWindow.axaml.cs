using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Updates;

namespace OsmoOverlay.Gui;

public partial class SettingsWindow : Window
{
	// Curated tile sources with their required attribution text, filled in when picked (see
	// OnMapProviderChanged). "{api_key}" is this app's own placeholder token (see
	// OverlayRenderer.ResolveUrlTemplate), baked into whichever position a provider's URL expects it
	// and simply absent for templates that don't need one. "Custom..." (kept last) reveals a free-text
	// URL box instead. "(default)" is labeled on Esri, listed first, since it's OverlaySettings' own
	// default MapTileUrlTemplate - OpenStreetMapUrlTemplate is a separate, lower-level fallback that
	// setting's default never leaves in effect, so it isn't the picker's own default either.
	private static readonly List<TileProviderOption> TileProviderOptions =
	[
		new("Esri World Imagery (satellite, default)", MapTileFetcher.SatelliteUrlTemplate, MapTileFetcher.SatelliteAttribution),
		new("OpenStreetMap", MapTileFetcher.OpenStreetMapUrlTemplate, MapTileFetcher.OpenStreetMapAttribution),
		new("OpenTopoMap", "https://a.tile.opentopomap.org/{z}/{x}/{y}.png", "© OpenStreetMap contributors, SRTM | © OpenTopoMap (CC-BY-SA)"),
		new("CARTO Positron (light) - requires API key", "https://basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png?key={api_key}", "© OpenStreetMap, © CARTO", IsCarto: true),
		new("CARTO Dark Matter - requires API key", "https://basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png?key={api_key}", "© OpenStreetMap, © CARTO", IsCarto: true),
		new("CARTO Voyager", "https://basemaps.cartocdn.com/rastertiles/voyager/{z}/{x}/{y}.png?key={api_key}", "© OpenStreetMap, © CARTO", IsCarto: true),
		TileProviderOption.Custom
	];

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

	/// <summary>Whether the main window is rendering - an FFmpeg update that restarts the app is held off until it isn't.</summary>
	public Func<bool> IsRendering { get; init; } = () => false;

	public SettingsWindow()
	{
		InitializeComponent();
		NvencPresetCombo.ItemsSource = NvencPresetOptions;
		BitrateCombo.ItemsSource = BitrateOptions;
		MapProviderCombo.ItemsSource = TileProviderOptions;

		var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
		AppVersionText.Text = $"OsmoOverlay v{appVersion}";

		var coreVersion = typeof(RenderJob).Assembly.GetName().Version?.ToString(3) ?? "?";
		CoreVersionText.Text = $"Core v{coreVersion}";
		DependencyStatusRows.ShowChecking(DependencyStatusGrid, RequiredTools.All);

		DateTime? configLastUpdatedUtc = OverlaySettingsStore.GetLastUpdatedUtc();
		ConfigLastUpdatedText.Text = configLastUpdatedUtc is { } utc
			? $"Config last updated: {utc.ToLocalTime():yyyy-MM-dd HH:mm}"
			: "Config last updated: never";
	}

	public SettingsWindow(bool showWatermark, bool smoothGpsMotion,
		string? mapTileUrlTemplate, string? mapAttribution, bool mapShowAttribution, string? mapApiKey,
		RouteIntroSettings routeIntro) : this()
	{
		ShowWatermarkCheck.IsChecked = showWatermark;
		SmoothGpsMotionCheck.IsChecked = smoothGpsMotion;

		var effectiveUrl = mapTileUrlTemplate ?? MapTileFetcher.OpenStreetMapUrlTemplate;
		TileProviderOption provider = TileProviderOptions.FirstOrDefault(p => !p.IsCustom && p.UrlTemplate == effectiveUrl)
		                              ?? TileProviderOption.Custom;
		MapProviderCombo.SelectedItem = provider;
		MapTileUrlBox.Text = provider.IsCustom ? effectiveUrl : null;
		MapTileUrlBox.IsVisible = provider.IsCustom;
		MapCustomUrlLabel.IsVisible = provider.IsCustom;

		MapApiKeyBox.Text = mapApiKey;
		UpdateMapApiKeyVisibility(provider.IsCustom ? "" : provider.UrlTemplate);
		UpdateMapApiKeyValidation();

		MapShowAttributionCheck.IsChecked = mapShowAttribution;
		MapAttributionLabel.IsVisible = mapShowAttribution;
		MapAttributionBox.IsVisible = mapShowAttribution;
		MapAttributionBox.Text = mapAttribution;

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
		var enabled = PreserveCameraMetadataCheck.IsChecked == true;
		MetadataSerialCheck.IsEnabled = enabled &&
		                                (MetadataTelemetryCheck.IsChecked == true || MetadataThumbnailsCheck.IsChecked == true);

		List<string> kept = [];
		if (MetadataTelemetryCheck.IsChecked == true) kept.Add("telemetry");
		if (MetadataSerialCheck is { IsChecked: true, IsEnabled: true }) kept.Add("serial number");
		if (MetadataDebugCheck.IsChecked == true) kept.Add("debug track");
		if (MetadataThumbnailsCheck.IsChecked == true) kept.Add("thumbnails");
		var summary = kept.Count > 0 ? string.Join(", ", kept) : "nothing selected";
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
			MetadataKeepThumbnails = MetadataThumbnailsCheck.IsChecked == true
		};
	}

	public bool ShowWatermark => ShowWatermarkCheck.IsChecked == true;
	public bool SmoothGpsMotion => SmoothGpsMotionCheck.IsChecked == true;

	public string? MapTileUrlTemplate =>
		MapProviderCombo.SelectedItem is TileProviderOption { IsCustom: true }
			? string.IsNullOrWhiteSpace(MapTileUrlBox.Text) ? null : MapTileUrlBox.Text.Trim()
			: (MapProviderCombo.SelectedItem as TileProviderOption)?.UrlTemplate;

	public string? MapAttribution => string.IsNullOrWhiteSpace(MapAttributionBox.Text) ? null : MapAttributionBox.Text.Trim();
	public bool MapShowAttribution => MapShowAttributionCheck.IsChecked == true;
	public string? MapApiKey => string.IsNullOrWhiteSpace(MapApiKeyBox.Text) ? null : MapApiKeyBox.Text.Trim();

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
		RouteIntroColorBySpeedCheck.IsChecked == true);

	// Set once the About tab showed a check - switching back to it later doesn't show it again.
	private bool _updatesShown;

	private AppRelease? _latestRelease;

	/// <summary>
	///     Each category is its own ScrollViewer stacked in the same Grid cell (see SettingsWindow.axaml)
	///     - switching category just swaps which one is visible instead of reparenting content.
	///     CategoryList's SelectedIndex="0" in XAML fires this event during InitializeComponent, before
	///     the panel fields further down the visual tree have been assigned yet - harmless to skip then,
	///     since RenderingPanel is already the one visible by default in XAML (every other panel starts
	///     with IsVisible="False"), matching SelectedIndex 0 without this handler's help.
	/// </summary>
	private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (RenderingPanel is null || MapPanel is null || RouteIntroPanel is null || AboutPanel is null)
			return;

		RenderingPanel.IsVisible = CategoryList.SelectedIndex == 0;
		MapPanel.IsVisible = CategoryList.SelectedIndex == 1;
		RouteIntroPanel.IsVisible = CategoryList.SelectedIndex == 2;
		AboutPanel.IsVisible = CategoryList.SelectedIndex == 3;

		if (CategoryList.SelectedIndex == 3 && !_updatesShown)
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
			DependencyStatusRows.Populate(this, DependencyStatusGrid, [.. result.Dependencies.Where(s => s.InstalledVersion is not null)],
				IsRendering);
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
		var updating = await AppUpdateFlow.UpdateAsync(this, _latestRelease, IsRendering, status => AppUpdateText.Text = status,
			share => AppUpdateText.Text = $"Downloading OsmoOverlay {_latestRelease.Version}... {share * 100:0}%");
		if (!updating) AppUpdateButton.IsEnabled = true;
	}

	/// <summary>Every TextBlock.link on the About tab - the address is in its Tag.</summary>
	private void OnLinkClick(object? sender, PointerPressedEventArgs e)
	{
		if ((sender as Control)?.Tag is string url) AppUpdateFlow.OpenInBrowser(url);
	}

	/// <summary>
	///     Picking a built-in provider sets both the URL template and its correct required attribution
	///     in one go (and hides the free-text URL box, since it's fixed); picking "Custom..." instead
	///     reveals that box for a hand-entered template, leaving Attribution as whatever's already there
	///     for the user to fill in themselves.
	/// </summary>
	private void OnMapProviderChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (MapProviderCombo.SelectedItem is not TileProviderOption provider) return;

		MapTileUrlBox.IsVisible = provider.IsCustom;
		MapCustomUrlLabel.IsVisible = provider.IsCustom;
		UpdateMapApiKeyVisibility(provider.IsCustom ? "" : provider.UrlTemplate);
		UpdateMapApiKeyValidation();

		if (provider.IsCustom) MapTileUrlBox.Text = "";
		else MapAttributionBox.Text = provider.Attribution;
	}

	/// <summary>Shows the API key field only for a template that actually references "{api_key}" - harmless to fill in otherwise, but pointless to show.</summary>
	private void UpdateMapApiKeyVisibility(string effectiveUrl)
	{
		var needsApiKey = effectiveUrl.Contains("{api_key}");
		MapApiKeyLabel.IsVisible = needsApiKey;
		MapApiKeyBox.IsVisible = needsApiKey;
	}

	// CARTO's own basemap keys follow a fixed "<id>_<id>_<n>_<24 hex chars>" shape.
	[GeneratedRegex(@"^[a-z0-9]+_[a-z0-9]+_\d+_[0-9a-f]{24}$", RegexOptions.IgnoreCase)]
	private static partial Regex CartoApiKeyPattern();

	/// <summary>
	///     Flags an API key that doesn't look like a CARTO key while a CARTO provider is selected - a
	///     soft hint (a render will still be attempted either way), not a hard block, since the only
	///     real validation is CARTO's own server rejecting the key on the next tile fetch. Doesn't
	///     apply to "Custom..." - a hand-entered template could be pointing at an entirely different
	///     provider with its own, unrelated key format.
	/// </summary>
	private void UpdateMapApiKeyValidation()
	{
		var isCarto = MapProviderCombo.SelectedItem is TileProviderOption { IsCarto: true };
		var key = MapApiKeyBox.Text;
		MapApiKeyHint.IsVisible = isCarto && !string.IsNullOrWhiteSpace(key) && !CartoApiKeyPattern().IsMatch(key.Trim());
	}

	private void OnMapTileUrlChanged(object? sender, RoutedEventArgs e)
	{
		UpdateMapApiKeyVisibility(MapTileUrlBox.Text ?? "");
		UpdateMapApiKeyValidation();
	}

	private void OnMapApiKeyChanged(object? sender, RoutedEventArgs e)
	{
		UpdateMapApiKeyValidation();
	}

	private void OnMapShowAttributionChanged(object? sender, RoutedEventArgs e)
	{
		var show = MapShowAttributionCheck.IsChecked == true;
		MapAttributionLabel.IsVisible = show;
		MapAttributionBox.IsVisible = show;
	}

	private void OnShowRouteIntroChanged(object? sender, RoutedEventArgs e)
	{
		RouteIntroOptionsPanel.IsEnabled = ShowRouteIntroCheck.IsChecked == true;
	}

	private sealed record TileProviderOption(string Display, string UrlTemplate, string Attribution, bool IsCustom = false, bool IsCarto = false)
	{
		public static readonly TileProviderOption Custom = new("Custom...", "", "", true);

		public override string ToString()
		{
			return Display;
		}
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
