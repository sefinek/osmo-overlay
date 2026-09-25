using System.Text.Json;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Small pieces of overlay-related state that aren't tied to any single preset's layout: which
///     preset is active, whether the attribution watermark is shown, and whether GPS-derived
///     position (Compass trail, Map pan/center) is smoothed between real GPS fixes instead of
///     holding each one for several video frames - see GpsInterpolation. On by default: it only
///     interpolates between two already-real, already-known fixes (never extrapolates into unknown
///     territory), and MapWidget - the widget it benefits most - is itself on by default, so most
///     renders would otherwise ship with the jumpier trail nobody actually prefers.
///     Map* is the tile source shared by every map-based widget (MapWidget, the route-intro
///     overview) - one place to set it instead of a per-widget field, so it can't drift between
///     widgets. Defaults to satellite imagery since it reads better than a street map alongside the
///     rest of the HUD. MapShowAttribution should normally stay on - most tile providers require
///     visible credit wherever the map is shown, and turning it off moves that responsibility onto
///     the user. MapApiKey fills a literal "{api_key}" placeholder for providers that need one (e.g.
///     CARTO), and is a no-op otherwise.
///     RouteIntro* configures the optional fullscreen "whole route" card shown for the first
///     RouteIntroDurationSeconds of the render - on by default, same as MapWidget, even though it
///     also needs network access to fetch map tiles. The RouteIntroShow* flags let the user pick which stats
///     appear alongside the map; RouteIntroUnits is its own setting (not per-widget Units, like
///     Elevation/Distance/SpeedGauge use) since the card has no OverlayElement of its own to carry one.
/// </summary>
public sealed record OverlaySettings(
	string? ActivePresetId = null,
	bool ShowWatermark = true,
	bool SmoothGpsMotion = true,
	// How wide (in pixels) the live preview is decoded/composited at - capped down from the source
	// resolution (never upscaled, see MainWindow.OpenPreviewAsync), trading preview sharpness for
	// scrub/playback responsiveness. Does not affect the exported render, which always uses the
	// source's full resolution regardless of this setting.
	int PreviewMaxWidth = 1280,
	string? MapTileUrlTemplate = MapTileFetcher.SatelliteUrlTemplate,
	string? MapAttribution = MapTileFetcher.SatelliteAttribution,
	bool MapShowAttribution = true,
	string? MapApiKey = null,
	bool ShowRouteIntro = true,
	double RouteIntroDurationSeconds = 12.0,
	bool RouteIntroShowDistance = true,
	bool RouteIntroShowMaxSpeed = true,
	bool RouteIntroShowAvgSpeed = true,
	bool RouteIntroShowDate = true,
	bool RouteIntroShowDuration = true,
	bool RouteIntroShowCameraModel = false,
	bool RouteIntroShowElevationGain = true,
	UnitSystem RouteIntroUnits = UnitSystem.Metric,
	// The overview map's route colored by speed like the compass/map widgets' TrailColorBySpeed - off here by default.
	bool RouteIntroColorBySpeed = false,
	bool PreviewSnapToGrid = true,
	// The preview's time readout with milliseconds instead of whole seconds (the toolbar's stopwatch toggle).
	bool PreviewPreciseTime = false,
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
	// ever trades away something the user explicitly opted out of. Off by default: the camera's djmd
	// track holds the GPS route and the camera serial number, which a video meant for sharing shouldn't
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
	bool FastStart = false,
	// Measured render speed (frames/s) per RenderSpeedHistory.Key - feeds the GUI's pre-render estimate.
	Dictionary<string, double>? RenderFpsHistory = null,
	// An app release the user declined at startup - not offered there again (Settings' About tab still does).
	string? SkippedAppUpdate = null);

/// <summary>
///     Render speed remembered per "shape" of render (resolution, frame rate, encoder, preset), from the
///     last completed render of that shape - there's no way to predict it up front, it depends on the
///     machine, the GPU and the footage itself.
/// </summary>
public static class RenderSpeedHistory
{
	public static string Key(int width, int height, double fps, string encoder, string nvencPreset)
	{
		return FormattableString.Invariant($"{width}x{height}@{fps:0.##}|{encoder}{(encoder == "hevc_nvenc" ? "|" + nvencPreset : "")}");
	}

	public static double? TryGet(string key)
	{
		return OverlaySettingsStore.Load().RenderFpsHistory?.TryGetValue(key, out var fps) == true ? fps : null;
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
				var settings = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(StorePath));
				if (settings is not null) return settings;
			}
		}
		catch (Exception ex)
		{
			// Best-effort: a corrupt or unreadable settings file falls back to the defaults.
			AppLogger.Warn(ex, "Overlay settings file is corrupt or unreadable, falling back to defaults");
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
			AppLogger.Warn(ex, "Failed to write overlay settings file");
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
