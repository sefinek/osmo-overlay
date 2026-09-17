using System.Security.Cryptography;
using System.Text;
using OsmoOverlay.Core.Logging;
using SkiaSharp;

namespace OsmoOverlay.Core.Mapping;

/// <summary>
///     Downloads XYZ map tiles and caches them on disk indefinitely (a given z/x/y tile doesn't
///     change), so re-rendering the same or an overlapping route doesn't re-fetch what's already on
///     disk. Identifies itself with a real User-Agent per OpenStreetMap's tile usage policy
///     (https://operations.osmfoundation.org/policies/tiles/) for the built-in OSM source; a custom
///     MapTileUrlTemplate is the user's own responsibility to use within its provider's terms.
/// </summary>
public static class MapTileFetcher
{
	// Named after the source, not "Default" - OverlayPreset.CreateDefault actually points new Map
	// widgets at SatelliteUrlTemplate below. Used as the lower-level fallback wherever an
	// OverlayElement's own MapTileUrlTemplate is null (see MainWindow.TileProviderOptions).
	public const string OpenStreetMapUrlTemplate = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
	public const string OpenStreetMapAttribution = "© OpenStreetMap contributors";

	// OverlayPreset.CreateDefault points a brand-new preset's Map widget at this (rather than
	// OpenStreetMapUrlTemplate) - satellite imagery reads better than a street map over HUD-style
	// overlays at a glance. Shared here (not just duplicated in the GUI's provider list) so Core
	// and the GUI can't drift on what "the satellite option" actually points to.
	public const string SatelliteUrlTemplate =
		"https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";
	public const string SatelliteAttribution = "Esri, Maxar, Earthstar Geographics";

	private static readonly HttpClient Http = CreateHttpClient();

	private static readonly string CacheDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "map-tiles");

	private static HttpClient CreateHttpClient()
	{
		var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd("OsmoOverlay/1.0 (+https://github.com/sefinek/osmo-overlay)");
		return client;
	}

	/// <summary>Null on any failure (offline, 404, timeout, corrupt image) - callers draw a placeholder instead of failing the render.</summary>
	public static async Task<SKBitmap?> FetchAsync(string urlTemplate, int zoom, int x, int y, CancellationToken ct)
	{
		var cachePath = CachePath(urlTemplate, zoom, x, y);
		try
		{
			if (File.Exists(cachePath))
			{
				// Not a `using` declaration: ownership of the decoded bitmap transfers to the caller
				// (every call site disposes it), so disposing it here before returning would hand back
				// an already-disposed SKBitmap on every cache hit - i.e. every render after the first
				// one for a given route.
				SKBitmap? cached = SKBitmap.Decode(cachePath);
				if (cached is not null) return cached;
			}

			var url = urlTemplate.Replace("{z}", zoom.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
			var bytes = await Http.GetByteArrayAsync(url, ct);

			Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
			// Write-then-rename so a cancelled/crashed write can never leave a truncated PNG at
			// cachePath itself - only ever an orphaned .tmp file next to it.
			var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
			await File.WriteAllBytesAsync(tempPath, bytes, ct);
			File.Move(tempPath, cachePath, true);

			return SKBitmap.Decode(bytes);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			AppLogger.Warn(ex, $"Failed to fetch map tile z={zoom} x={x} y={y}");
			return null;
		}
	}

	/// <summary>
	///     Tiles are cached per tile-server (hashed from the URL template, since two templates could
	///     otherwise collide on the same z/x/y path) under a directory tree mirroring the usual
	///     {z}/{x}/{y} layout, so the cache stays inspectable on disk.
	/// </summary>
	private static string CachePath(string urlTemplate, int zoom, int x, int y)
	{
		var serverId = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(urlTemplate)))[..8];
		return Path.Combine(CacheDir, serverId, zoom.ToString(), x.ToString(), $"{y}.png");
	}
}
