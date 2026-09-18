using System.Diagnostics;
using OsmoOverlay.Core.Logging;
using SkiaSharp;

namespace OsmoOverlay.Core.Mapping;

/// <summary>
///     A single stitched-together image covering the whole route's bounding box at a fixed zoom,
///     fetched once up front - not per frame. The MapWidget then just crops/pans a small window of
///     this in-memory bitmap per frame (GetPixel), which is what makes a "live moving map" over a
///     multi-thousand-frame render cheap: one round of tile downloads total, however long the video.
/// </summary>
public sealed class RouteMapMosaic : IDisposable
{
	// Tile count doubles roughly x4 per zoom level - this keeps a single mosaic (and the one-time
	// download it costs) bounded even for a long route, backing off zoom automatically rather than
	// silently trying to fetch thousands of tiles.
	private const int MaxTiles = 300;

	// The requested zoom (OverlayElement.MapZoom, edited via the GUI's "Zoom" NumericUpDown) is
	// clamped to this range before use - public so the GUI can bound the control to the same range
	// instead of duplicating these numbers as separate hardcoded Minimum/Maximum literals in XAML.
	public const int MinZoom = 2;
	public const int MaxZoom = 19;

	// A handful of tiles in flight at once meaningfully cuts down the round-trip-latency-bound wait
	// for a route needing hundreds of tiles, without turning into the kind of bulk/heavy parallel
	// hammering OpenStreetMap's tile usage policy (see MapTileFetcher) asks providers' servers not to
	// be subjected to - a couple of browsers' old per-host connection limits land in this same range.
	private const int MaxConcurrentFetches = 4;

	private readonly double _originWorldX;
	private readonly double _originWorldY;

	private RouteMapMosaic(SKBitmap bitmap, double originWorldX, double originWorldY, int zoom)
	{
		Bitmap = bitmap;
		_originWorldX = originWorldX;
		_originWorldY = originWorldY;
		Zoom = zoom;
	}

	public SKBitmap Bitmap { get; }
	public int Zoom { get; }

	public void Dispose()
	{
		Bitmap.Dispose();
	}

	/// <summary>Pixel position of the given lat/lon within Bitmap.</summary>
	public SKPoint GetPixel(double lat, double lon)
	{
		var (worldX, worldY) = WebMercator.LatLonToWorldPixel(lat, lon, Zoom);
		return new SKPoint((float)(worldX - _originWorldX), (float)(worldY - _originWorldY));
	}

	/// <summary>Null when there are no points to map, or every single tile fetch failed (e.g. fully offline).</summary>
	public static async Task<RouteMapMosaic?> BuildAsync(IReadOnlyList<(double Lat, double Lon)> points,
		string urlTemplate, int requestedZoom, int paddingTiles, CancellationToken ct,
		Action<int, int>? onProgress = null)
	{
		if (points.Count == 0) return null;

		var minLat = points.Min(p => p.Lat);
		var maxLat = points.Max(p => p.Lat);
		var minLon = points.Min(p => p.Lon);
		var maxLon = points.Max(p => p.Lon);

		var zoom = Math.Clamp(requestedZoom, MinZoom, MaxZoom);
		int minTileX, minTileY, maxTileX, maxTileY;
		while (true)
		{
			var (topLeftX, topLeftY) = WebMercator.LatLonToWorldPixel(maxLat, minLon, zoom);
			var (bottomRightX, bottomRightY) = WebMercator.LatLonToWorldPixel(minLat, maxLon, zoom);
			(minTileX, minTileY) = WebMercator.WorldPixelToTile(topLeftX, topLeftY);
			(maxTileX, maxTileY) = WebMercator.WorldPixelToTile(bottomRightX, bottomRightY);

			// Padding on every side, so a point right at the bounding-box edge still has map visible
			// around it once the widget crops a window centered on it (more padding when the caller
			// needs room for a wider dynamic-zoom crop, not just the static 1x window).
			minTileX -= paddingTiles;
			minTileY -= paddingTiles;
			maxTileX += paddingTiles;
			maxTileY += paddingTiles;

			var tileCount = (long)(maxTileX - minTileX + 1) * (maxTileY - minTileY + 1);
			if (tileCount <= MaxTiles || zoom <= 2) break;
			zoom--;
		}

		var tilesWide = maxTileX - minTileX + 1;
		var tilesHigh = maxTileY - minTileY + 1;
		var totalTiles = tilesWide * tilesHigh;

		var bitmap = new SKBitmap(tilesWide * WebMercator.TileSize, tilesHigh * WebMercator.TileSize);
		var fetchedAny = false;
		var fetchedCount = 0;
		var drawLock = new Lock();
		// Reported at most a few times a second (plus always the final tile), not on a fixed tile-count
		// interval - cached tiles resolve from disk in milliseconds, so a count-based throttle (e.g.
		// every 10 tiles) still spams a dozen near-simultaneous log lines once a route is fully cached;
		// this only reports again once meaningful wall-clock time has actually passed.
		var progressStopwatch = Stopwatch.StartNew();
		var lastReportedMs = 0L;
		const int reportIntervalMs = 250;
		try
		{
			using var canvas = new SKCanvas(bitmap);
			canvas.Clear(new SKColor(30, 32, 36));

			// Fetches (network + disk cache I/O) run with bounded concurrency; the actual SKCanvas
			// draw and the fetchedCount/onProgress bookkeeping stay serialized under drawLock, since
			// SKCanvas isn't safe to call into from more than one thread at a time.
			using var throttle = new SemaphoreSlim(MaxConcurrentFetches);
			var tasks = new List<Task>(totalTiles);
			for (var tx = minTileX; tx <= maxTileX; tx++)
			for (var ty = minTileY; ty <= maxTileY; ty++)
				tasks.Add(FetchAndDrawAsync(tx, ty));

			await Task.WhenAll(tasks);

			async Task FetchAndDrawAsync(int tileX, int tileY)
			{
				await throttle.WaitAsync(ct);
				try
				{
					SKBitmap? tile = await MapTileFetcher.FetchAsync(urlTemplate, zoom, tileX, tileY, ct);
					try
					{
						lock (drawLock)
						{
							if (tile is not null)
							{
								fetchedAny = true;
								var dest = SKRect.Create((tileX - minTileX) * WebMercator.TileSize,
									(tileY - minTileY) * WebMercator.TileSize, WebMercator.TileSize, WebMercator.TileSize);
								canvas.DrawBitmap(tile, dest, SKSamplingOptions.Default);
							}

							fetchedCount++;
							if (fetchedCount == totalTiles || progressStopwatch.ElapsedMilliseconds - lastReportedMs >= reportIntervalMs)
							{
								lastReportedMs = progressStopwatch.ElapsedMilliseconds;
								onProgress?.Invoke(fetchedCount, totalTiles);
							}
						}
					}
					finally
					{
						tile?.Dispose();
					}
				}
				finally
				{
					throttle.Release();
				}
			}
		}
		catch
		{
			// Covers cancellation too (FetchAsync only swallows non-cancellation failures) - without
			// this, a render cancelled mid-fetch would leak the whole mosaic bitmap.
			bitmap.Dispose();
			throw;
		}

		if (!fetchedAny)
		{
			bitmap.Dispose();
			AppLogger.Warn("Map widget: every tile fetch failed (offline, or the tile server is unreachable) - skipping the map.");
			return null;
		}

		var originWorldX = minTileX * (double)WebMercator.TileSize;
		var originWorldY = minTileY * (double)WebMercator.TileSize;
		return new RouteMapMosaic(bitmap, originWorldX, originWorldY, zoom);
	}
}
