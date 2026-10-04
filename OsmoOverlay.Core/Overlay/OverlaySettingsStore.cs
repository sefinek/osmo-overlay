using OsmoOverlay.Core.Localization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Small pieces of overlay-related state that aren't tied to any single preset's layout: which
///     preset is active, whether the attribution watermark is shown, and whether GPS-derived
///     position (Compass trail, Map pan/center) is smoothed between real GPS fixes instead of
///     holding each one for several video frames - see GpsInterpolation. On by default: it only
///     interpolates between two already-real, already-known fixes (never extrapolates into unknown
///     territory), and MapWidget - the widget it benefits most - is itself on by default, so most
///     renders would otherwise ship with the jumpier trail nobody actually prefers.
///     Which tile server a map uses is picked per consumer (MapWidgetElement.MapProviderId, RouteIntroMapProvider) from the
///     one list in MapProviders; what they share is kept here once, so it is the same wherever it's edited: MapApiKeys
///     (one key per MapProvider.KeyGroup, filling a literal "{api_key}" in the URL) and the custom provider's
///     CustomMapUrlTemplate/CustomMapAttribution, and CustomMapCreditBriefly - the user's word that the custom server's
///     terms let its credit be shown only for a while (the built-in providers' rule is their own: MapTerms.Credit).
///     Defaults to satellite imagery free of a key, since it reads better than a street map alongside the rest of the HUD.
///     RouteIntro* configures the optional fullscreen "whole route" card shown for the first
///     RouteIntroDurationSeconds of the render - on by default, same as MapWidget, even though it
///     also needs network access to fetch map tiles. RouteIntroStats picks which stats appear alongside the
///     map, RouteIntroLabels their captions; RouteIntroUnits is its own setting (not per-widget Units, like
///     Elevation/Distance/SpeedGauge use) since the card has no OverlayElement of its own to carry one.
/// </summary>
public sealed record OverlaySettings(
	string? ActivePresetId = null,
	bool ShowWatermark = true,
	bool SmoothGpsMotion = true,
	// Percent added to every speed the camera's GPS gives, for a receiver that reads low (TelemetryProcessor.Process).
	double SpeedCorrectionPercent = 0,
	// The first-run welcome window was shown (Settings can open it again).
	bool WelcomeShown = false,
	// Where a render goes by default; null = next to the source (RenderOptions.DefaultOutputPath).
	string? DefaultOutputFolder = null,
	// How wide (in pixels) the live preview is decoded/composited at - capped down from the source
	// resolution (never upscaled, see MainWindow.OpenPreviewAsync), trading preview sharpness for
	// scrub/playback responsiveness. Does not affect the exported render, which always uses the
	// source's full resolution regardless of this setting.
	int PreviewMaxWidth = 1280,
	Dictionary<string, string>? MapApiKeys = null,
	string? CustomMapUrlTemplate = null,
	string? CustomMapAttribution = null,
	string? RouteIntroMapProvider = null,
	// What a map with no provider of its own shows: null = satellite, "streets" (MapProviders.StreetsAutoId) as the
	// first-run window's Map step chose.
	string? DefaultMapProvider = null,
	bool CustomMapCreditBriefly = false,
	bool ShowEsriMapLabels = true,
	bool RouteIntroShowEsriMapLabels = false,
	bool ShowRouteIntro = true,
	double RouteIntroDurationSeconds = 12.0,
	RouteIntroStats RouteIntroStats = RouteIntroStats.Default,
	// The user's own captions for the card's stats, by RouteIntroStats name (RouteIntroLabels).
	Dictionary<string, string>? RouteIntroLabels = null,
	UnitSystem RouteIntroUnits = UnitSystem.Metric,
	// The overview map's route colored by speed like the compass/map widgets' TrailColorBySpeed - off here by default.
	bool RouteIntroColorBySpeed = false,
	// The trip's first and last positions marked on the overview map, captioned with these (blank = a dot only).
	bool RouteIntroShowStartFinish = true,
	string? RouteIntroStartLabel = RouteIntroSettings.DefaultStartLabel,
	string? RouteIntroFinishLabel = RouteIntroSettings.DefaultFinishLabel,
	bool PreviewSnapToGrid = true,
	// The preview's time readout - the GUI's PreviewTimeFormat as a string, like PreviewGridMode below.
	string PreviewTimeFormat = "Seconds",
	// The expanded timeline (filmstrip + waveform) in the log's place under the window, instead of the compact track.
	bool PreviewTimelineExpanded = false,
	// How the drawn routes cross a part cut out of the render (see RouteJoin).
	RouteJoin RouteAcrossCuts = RouteJoin.Gap,
	// Preview sound: muted until the user turns it up, the volume 0-1 remembered either way.
	bool PreviewAudioMuted = true,
	double PreviewAudioVolume = 0.8,
	// Mirrors the GUI's own PreviewGridMode enum (Off/Thirds/Margin/Both) as a string, since this Core
	// project has no dependency on the Gui project to reference that enum directly.
	string PreviewGridMode = "Both",
	// Export options (see RenderEncodeSettings) - every default reproduces the source 1:1; each one only
	// ever trades away something the user explicitly opted out of. Off by default: the camera's telemetry
	// (DJI's djmd track) holds the GPS route and the camera serial number, which a video meant for sharing shouldn't
	// carry unless asked to.
	bool PreserveCameraMetadata = false,
	// What PreserveCameraMetadata keeps, see CameraMetadataSelection. Serial number off by default even
	// when the rest is kept - it identifies the physical camera and nothing in the app needs it.
	bool MetadataKeepTelemetry = true,
	bool MetadataKeepDebugTrack = true,
	bool MetadataKeepThumbnails = true,
	bool MetadataKeepSerialNumber = false,
	string NvencPreset = "p7",
	bool HardwareDecoding = true,
	double OutputBitrateMultiplier = 1.0,
	// What the render writes, when not the source's own (OutputVideo): the short side in pixels (never larger than the source's),
	// "h264" or "hevc", and 8-bit for a 10-bit source. Null/false = as the source - the default.
	int? OutputResolution = null,
	string? OutputCodec = null,
	bool OutputEightBit = false,
	bool FastStart = false,
	// Measured render speed (frames/s) per RenderSpeedHistory.Key - feeds the GUI's pre-render estimate.
	Dictionary<string, double>? RenderFpsHistory = null,
	// An app release the user declined at startup - not offered there again (Settings' About tab still does).
	string? SkippedAppUpdate = null,
	// The GUI's scale on top of the system's display scaling, applied at startup (see the GUI's UiScale).
	double InterfaceScale = 1.0,
	// The GUI's language (UiLanguages code), applied at startup; null = the system's language when supported, else English.
	string? UiLanguage = null,
	// The GUI's color theme (its AppThemes): "Dark", "Blue" or "Amoled"; anything else reads as Dark.
	string AppTheme = "Dark",
	// Off: the main window always starts maximized. On: it comes back as MainWindowPlacement left it.
	bool RestoreWindowPlacement = true,
	WindowPlacement? MainWindowPlacement = null,
	// How many layer tracks the expanded timeline shows before the list scrolls (the GUI clamps it to its own range).
	int LayerRowsVisible = 4,
	// The monitor the full screen preview opens on - the GUI's MonitorChoice.KeyOf; null = the one the main window is on.
	string? PreviewMonitor = null,
	// A window on another monitor showing the log, and the preview while it plays (the GUI's SecondScreenWindow).
	// SecondScreenMonitor null = the first screen the main window isn't on.
	bool SecondScreenEnabled = false,
	string? SecondScreenMonitor = null,
	// Startup: the project last saved or opened comes back, unless the app was started with one.
	bool ReopenLastProject = false,
	string? LastProject = null,
	// A backup copy of the open project every this many minutes (0 = off), kept apart from the project's own file.
	int AutoSaveMinutes = 0,
	// Cached summaries and waveforms no recording has used for this many days are deleted at startup (0 = never).
	int CacheCleanupDays = 30,
	// Playback loops from the start of a session (the toolbar's Loop button still toggles it).
	bool LoopByDefault = false,
	// Off: the preview leaves the widgets' shadows out - most of the overlay's drawing time - on a slow computer. A render
	// draws them unless DisableShadows.
	bool PreviewShadows = true,
	// Closing the window mid-render asks first.
	bool ConfirmCloseWhileRendering = true,
	// No widget's shadow anywhere - preview, snapshot and render - whatever the widgets' own shadow settings, which are kept.
	bool DisableShadows = false)
{
	/// <summary>Whether the preview draws the widgets' shadows: on in the preview and not turned off everywhere.</summary>
	[JsonIgnore]
	public bool PreviewDrawsShadows => PreviewShadows && !DisableShadows;
}

/// <summary>
///     The main window's last normal (not maximized) bounds and whether it was maximized. X/Y are the window's position
///     in physical pixels, null (and Width/Height 0) when it was never in its normal state; Width/Height are its client
///     size in layout units without the interface scale, so a changed scale sizes the window with it.
/// </summary>
public sealed record WindowPlacement(int? X, int? Y, double Width, double Height, bool Maximized);

/// <summary>
///     Render speed remembered per "shape" of render (resolution, frame rate, encoder, preset), from the
///     last completed render of that shape - there's no way to predict it up front, it depends on the
///     machine, the GPU and the footage itself.
/// </summary>
public static class RenderSpeedHistory
{
	public static string Key(int width, int height, double fps, string encoder, string nvencPreset)
	{
		return FormattableString.Invariant($"{width}x{height}@{fps:0.##}|{encoder}{(FfmpegPipeline.IsGpuEncoder(encoder) ? "|" + nvencPreset : "")}");
	}

	public static double? TryGet(string key)
	{
		return OverlaySettingsStore.Load().RenderFpsHistory?.TryGetValue(key, out double fps) == true ? fps : null;
	}

	public static void Record(string key, double fps)
	{
		if (!(fps > 0) || !double.IsFinite(fps)) return;

		OverlaySettings settings = OverlaySettingsStore.Load();
		Dictionary<string, double> history = settings.RenderFpsHistory is { } existing ? new Dictionary<string, double>(existing) : [];
		history[key] = Math.Round(fps, 2);
		OverlaySettingsStore.Save(settings with { RenderFpsHistory = history });
	}
}

/// <summary>
///     Persists OverlaySettings to one general settings.json, shared between the GUI and the CLI the
///     same way overlay presets are.
/// </summary>
public static class OverlaySettingsStore
{
	private static readonly string StoreDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay");

	private static string StorePath => Path.Combine(StoreDir, "settings.json");

	public static OverlaySettings Load()
	{
		try
		{
			if (File.Exists(StorePath))
			{
				OverlaySettings? settings = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(StorePath));
				if (settings is not null) return settings;
			}
		}
		catch (Exception ex)
		{
			// Best-effort: a corrupt or unreadable settings file falls back to the defaults.
			AppLogger.Warn(ex, CoreStrings.Settings_Unreadable);
		}

		return new OverlaySettings();
	}

	public static void Save(OverlaySettings settings)
	{
		try
		{
			Directory.CreateDirectory(StoreDir);
			AtomicFile.WriteAllText(StorePath, JsonSerializer.Serialize(settings));
		}
		catch (Exception ex)
		{
			// Best-effort cache: a failed write should not break the editing flow.
			AppLogger.Warn(ex, CoreStrings.Settings_WriteFailed);
		}
	}

	/// <summary>
	///     The settings file's own last-write timestamp - not a field inside OverlaySettings itself,
	///     since the filesystem already tracks this accurately for every Save() call site (there are
	///     many, scattered across the GUI) without each one needing to remember to stamp it manually.
	/// </summary>
	public static DateTime? GetLastUpdatedUtc()
	{
		return File.Exists(StorePath) ? File.GetLastWriteTimeUtc(StorePath) : null;
	}
}
