using System.Security.Cryptography;
using System.Text;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Updates;
using SkiaSharp;

namespace OsmoOverlay.Core.Mapping;

/// <summary>
///     Downloads XYZ map tiles and caches them on disk indefinitely (a given z/x/y tile doesn't
///     change), so re-rendering the same or an overlapping route doesn't re-fetch what's already on
///     disk. Identifies itself with a real User-Agent per OpenStreetMap's tile usage policy
///     (https://operations.osmfoundation.org/policies/tiles/) for the built-in OSM source; a custom
///     tile URL (OverlaySettings.CustomMapUrlTemplate) is the user's own responsibility to use within its provider's terms.
/// </summary>
public static class MapTileFetcher
{
	// Named after the source, not "Default" - the default provider is the satellite one below. Also the fallback
	// for an empty custom URL (MapSources.UrlTemplate).
	public const string OpenStreetMapUrlTemplate = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
	public const string OpenStreetMapAttribution = "© OpenStreetMap contributors";

	// The default provider (MapProviders.DefaultId) rather than OpenStreetMapUrlTemplate -
	// satellite imagery reads better than a street map over HUD-style overlays at a glance.
	public const string SatelliteUrlTemplate =
		"https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";
	public const string SatelliteAttribution = "Esri, Maxar, Earthstar Geographics";

	private static readonly HttpClient Http = CreateHttpClient();

	private static readonly string CacheDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "map-tiles");

	private static HttpClient CreateHttpClient()
	{
		var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd($"OsmoOverlay/{AppUpdates.CurrentVersion} (+{AppUpdates.RepositoryUrl})");
		return client;
	}

	// A mosaic fetches dozens to low hundreds of tiles at once (bounded by RouteMapMosaic.MaxTiles),
	// MaxConcurrentFetches of them in flight together - a public tile server occasionally rate-limits
	// or times out a request under that burst even though the tile itself is perfectly fine, and
	// without a retry that tile just stays a permanent hole in the mosaic (very visible once something
	// draws the whole mosaic at once, e.g. the route-intro overview, not just MapWidget's small
	// per-frame crop). Short, since a real outage/offline shouldn't make every single tile wait this
	// out three times over.
	private const int MaxFetchAttempts = 3;

	/// <summary>Null after MaxFetchAttempts failures (offline, 404, timeout, corrupt image) - callers draw a placeholder instead of failing the render.</summary>
	public static async Task<SKBitmap?> FetchAsync(string urlTemplate, int zoom, int x, int y, CancellationToken ct)
	{
		string cachePath = CachePath(urlTemplate, zoom, x, y);
		if (File.Exists(cachePath))
		{
			// Not a `using` declaration: ownership of the decoded bitmap transfers to the caller
			// (every call site disposes it), so disposing it here before returning would hand back
			// an already-disposed SKBitmap on every cache hit - i.e. every render after the first
			// one for a given route.
			var cached = SKBitmap.Decode(cachePath);
			if (cached is not null) return cached;
		}

		string url = urlTemplate.Replace("{z}", zoom.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());

		for (int attempt = 1; attempt <= MaxFetchAttempts; attempt++)
		{
			try
			{
				byte[] bytes = await Http.GetByteArrayAsync(url, ct);

				// Decoded before caching - a provider answering 200 with an error page/JSON instead of an
				// image must count as a failed attempt, not get written to disk as a "tile" forever.
				SKBitmap bitmap = SKBitmap.Decode(bytes)
				                  ?? throw new InvalidDataException($"Tile response ({bytes.Length} bytes) is not a decodable image.");

				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
					await AtomicFile.WriteAllBytesAsync(cachePath, bytes, ct);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					// The tile itself is fine - a failed cache write only means it's fetched again next time.
					AppLogger.Warn(ex, string.Format(CoreStrings.Map_CacheTileFailed, $"z={zoom} x={x} y={y}"));
				}
				catch
				{
					bitmap.Dispose();
					throw;
				}

				return bitmap;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				if (attempt == MaxFetchAttempts)
				{
					AppLogger.Warn(ex, string.Format(CoreStrings.Map_FetchTileFailed, $"z={zoom} x={x} y={y}", MaxFetchAttempts));
					return null;
				}

				await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct);
			}
		}

		return null;
	}

	/// <summary>
	///     Tiles are cached per tile-server (hashed from the URL template, since two templates could
	///     otherwise collide on the same z/x/y path) under a directory tree mirroring the usual
	///     {z}/{x}/{y} layout, so the cache stays inspectable on disk.
	/// </summary>
	private static string CachePath(string urlTemplate, int zoom, int x, int y)
	{
		string serverId = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(urlTemplate)))[..8];
		return Path.Combine(CacheDir, serverId, zoom.ToString(), x.ToString(), $"{y}.png");
	}
}
