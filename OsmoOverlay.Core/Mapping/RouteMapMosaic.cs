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
		(double worldX, double worldY) = WebMercator.LatLonToWorldPixel(lat, lon, Zoom);
		return new SKPoint((float)(worldX - _originWorldX), (float)(worldY - _originWorldY));
	}

	/// <summary>Null when there are no points to map, or every single tile fetch failed (e.g. fully offline).</summary>
	public static async Task<RouteMapMosaic?> BuildAsync(IReadOnlyList<(double Lat, double Lon)> points,
		string urlTemplate, int requestedZoom, CancellationToken ct)
	{
		if (points.Count == 0) return null;

		var minLat = points.Min(p => p.Lat);
		var maxLat = points.Max(p => p.Lat);
		var minLon = points.Min(p => p.Lon);
		var maxLon = points.Max(p => p.Lon);

		var zoom = Math.Clamp(requestedZoom, 2, 19);
		int minTileX, minTileY, maxTileX, maxTileY;
		while (true)
		{
			(double topLeftX, double topLeftY) = WebMercator.LatLonToWorldPixel(maxLat, minLon, zoom);
			(double bottomRightX, double bottomRightY) = WebMercator.LatLonToWorldPixel(minLat, maxLon, zoom);
			(minTileX, minTileY) = WebMercator.WorldPixelToTile(topLeftX, topLeftY);
			(maxTileX, maxTileY) = WebMercator.WorldPixelToTile(bottomRightX, bottomRightY);

			// A tile of padding on every side, so a point right at the bounding-box edge still has
			// map visible around it once the widget crops a window centered on it.
			minTileX--;
			minTileY--;
			maxTileX++;
			maxTileY++;

			var tileCount = (long)(maxTileX - minTileX + 1) * (maxTileY - minTileY + 1);
			if (tileCount <= MaxTiles || zoom <= 2) break;
			zoom--;
		}

		var tilesWide = maxTileX - minTileX + 1;
		var tilesHigh = maxTileY - minTileY + 1;

		var bitmap = new SKBitmap(tilesWide * WebMercator.TileSize, tilesHigh * WebMercator.TileSize);
		var fetchedAny = false;
		using (var canvas = new SKCanvas(bitmap))
		{
			canvas.Clear(new SKColor(30, 32, 36));

			for (var tx = minTileX; tx <= maxTileX; tx++)
			for (var ty = minTileY; ty <= maxTileY; ty++)
			{
				using SKBitmap? tile = await MapTileFetcher.FetchAsync(urlTemplate, zoom, tx, ty, ct);
				if (tile is null) continue;

				fetchedAny = true;
				var dest = SKRect.Create((tx - minTileX) * WebMercator.TileSize, (ty - minTileY) * WebMercator.TileSize,
					WebMercator.TileSize, WebMercator.TileSize);
				canvas.DrawBitmap(tile, dest, SKSamplingOptions.Default);
			}
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
