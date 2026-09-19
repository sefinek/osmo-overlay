using System.Text.Json;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Small pieces of overlay-related state that aren't tied to any single preset's layout: which
///     preset is active, whether the attribution watermark is shown, and whether GPS-derived
///     position (Compass trail, Map pan/center) is smoothed between real GPS fixes instead of
///     holding each one for several video frames - see GpsInterpolation. Off by default: it's a
///     cosmetic touch-up, not a correctness fix, so it shouldn't silently change what a render looks
///     like for someone who hasn't opted in.
///     Map* is the tile source shared by every map-based widget (MapWidget, the route-intro
///     overview) - one place to set it instead of a per-widget field, so it can't drift between
///     widgets. Defaults to satellite imagery since it reads better than a street map alongside the
///     rest of the HUD. MapShowAttribution should normally stay on - most tile providers require
///     visible credit wherever the map is shown, and turning it off moves that responsibility onto
///     the user. MapApiKey fills a literal "{api_key}" placeholder for providers that need one (e.g.
///     CARTO), and is a no-op otherwise.
///     RouteIntro* configures the optional fullscreen "whole route" card shown for the first
///     RouteIntroDurationSeconds of the render - off by default like MapWidget, since it also needs
///     network access to fetch map tiles. The RouteIntroShow* flags let the user pick which stats
///     appear alongside the map; RouteIntroUnits is its own setting (not per-widget Units, like
///     Elevation/Distance/SpeedGauge use) since the card has no OverlayElement of its own to carry one.
/// </summary>
public sealed record OverlaySettings(
	string? ActivePresetId = null,
	bool ShowWatermark = true,
	bool SmoothGpsMotion = false,
	// How wide (in pixels) the live preview is decoded/composited at - capped down from the source
	// resolution (never upscaled, see MainWindow.OpenPreviewAsync), trading preview sharpness for
	// scrub/playback responsiveness. Does not affect the exported render, which always uses the
	// source's full resolution regardless of this setting.
	int PreviewMaxWidth = 1280,
	string? MapTileUrlTemplate = MapTileFetcher.SatelliteUrlTemplate,
	string? MapAttribution = MapTileFetcher.SatelliteAttribution,
	bool MapShowAttribution = true,
	string? MapApiKey = null,
	bool ShowRouteIntro = false,
	double RouteIntroDurationSeconds = 10.0,
	bool RouteIntroShowDistance = true,
	bool RouteIntroShowMaxSpeed = true,
	bool RouteIntroShowAvgSpeed = true,
	bool RouteIntroShowDate = true,
	bool RouteIntroShowDuration = true,
	bool RouteIntroShowCameraModel = true,
	bool RouteIntroShowElevationGain = true,
	UnitSystem RouteIntroUnits = UnitSystem.Metric);

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
}
