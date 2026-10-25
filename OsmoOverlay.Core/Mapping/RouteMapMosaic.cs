using System.Diagnostics;
using OsmoOverlay.Core.Localization;
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

	// Room around the route in a view, as a share of its extent.
	private const double ViewMargin = 1.12;

	// The requested zoom (OverlayElement.MapZoom, edited via the GUI's "Zoom" NumericUpDown) is
	// clamped to this range before use - public so the GUI can bound the control to the same range
	// instead of duplicating these numbers as separate hardcoded Minimum/Maximum literals in XAML.
	public const int MinZoom = 2;
	public const int MaxZoom = 19;

	// A handful of tiles in flight at once meaningfully cuts down the round-trip-latency-bound wait
	// for a route needing hundreds of tiles, without turning into the kind of bulk/heavy parallel
	// hammering OpenStreetMap's tile usage policy (see MapTileFetcher) asks providers' servers not to
	// be subjected to.
	private const int MaxConcurrentFetches = 4;

	private readonly double _originWorldX;
	private readonly double _originWorldY;

	private readonly SKBitmap _bitmap;

	private RouteMapMosaic(SKBitmap bitmap, double originWorldX, double originWorldY, int zoom, GeoBounds route, SKRect view, double coverage)
	{
		// Drawing a mutable SKBitmap makes Skia snapshot (copy) its pixels on every single draw call - for
		// a mosaic of up to MaxTiles tiles that's tens of MB copied per rendered frame. Frozen once here,
		// the SKImage shares the bitmap's pixels instead.
		bitmap.SetImmutable();
		_bitmap = bitmap;
		Image = SKImage.FromBitmap(bitmap);
		_originWorldX = originWorldX;
		_originWorldY = originWorldY;
		Zoom = zoom;
		Route = route;
		View = view;
		Coverage = coverage;
	}

	public SKImage Image { get; }
	public int Zoom { get; }

	/// <summary>The part of the image meant to be shown, in its pixels: all of it, or for a mosaic built for a rect's aspect the window centered on the route.</summary>
	public SKRect View { get; }

	/// <summary>How many times wider than the view the tiles reach, around it: what was asked for, or less where the tile budget ran out. 1 = the view only.</summary>
	public double Coverage { get; }

	/// <summary>The bounding box of the points the mosaic was built around - its padding counts on it.</summary>
	public GeoBounds Route { get; }

	public void Dispose()
	{
		Image.Dispose();
		_bitmap.Dispose();
	}

	/// <summary>Pixel position of the given lat/lon within Bitmap.</summary>
	public SKPoint GetPixel(double lat, double lon)
	{
		(double worldX, double worldY) = WebMercator.LatLonToWorldPixel(lat, lon, Zoom);
		return new SKPoint((float)(worldX - _originWorldX), (float)(worldY - _originWorldY));
	}

	/// <summary>Null when there are no points to map, or every single tile fetch failed (e.g. fully offline).</summary>
	public static async Task<RouteMapMosaic?> BuildAsync(IReadOnlyList<(double Lat, double Lon)> points,
		string urlTemplate, int requestedZoom, int paddingTiles, CancellationToken ct,
		Action<int, int>? onProgress = null, double? targetAspectRatio = null, TimeSpan? cacheMaxAge = null, string? labelsUrlTemplate = null,
		(double Width, double Height)? minSize = null, double coverage = 1)
	{
		if (points.Count == 0) return null;

		(double minLat, double maxLat, double minLon, double maxLon) = GeoBounds.Of(points);

		int zoom = Math.Clamp(requestedZoom, MinZoom, MaxZoom);
		int minTileX, minTileY, maxTileX, maxTileY;
		(double Left, double Top, double Width, double Height)? viewWorld = null;
		double reach = 1;
		while (true)
		{
			(double topLeftX, double topLeftY) = WebMercator.LatLonToWorldPixel(maxLat, minLon, zoom);
			(double bottomRightX, double bottomRightY) = WebMercator.LatLonToWorldPixel(minLat, maxLon, zoom);
			(minTileX, minTileY) = WebMercator.WorldPixelToTile(topLeftX, topLeftY);
			(maxTileX, maxTileY) = WebMercator.WorldPixelToTile(bottomRightX, bottomRightY);

			// Padding on every side, so a point right at the bounding-box edge still has map visible
			// around it once the widget crops a window centered on it (more padding when the caller
			// needs room for a wider dynamic-zoom crop, not just the static 1x window).
			minTileX -= paddingTiles;
			minTileY -= paddingTiles;
			maxTileX += paddingTiles;
			maxTileY += paddingTiles;

			// A caller that draws the mosaic over a rect (RouteIntro) gets a view: the window of the bitmap in that rect's
			// shape, centered on the route, at least minSize pixels (what it tolerates upscaled - a short route would otherwise
			// be a couple of tiles blown up several times over) and with a margin around the route. The tiles cover the
			// view, not the other way round, so the route sits in the middle whatever the tile grid does.
			if (targetAspectRatio is { } targetAspect)
			{
				double viewWidth = Math.Max(bottomRightX - topLeftX, 1) * ViewMargin;
				double viewHeight = Math.Max(bottomRightY - topLeftY, 1) * ViewMargin;
				if (viewWidth / viewHeight < targetAspect) viewWidth = viewHeight * targetAspect;
				else viewHeight = viewWidth / targetAspect;

				if (minSize is { } min)
				{
					double grow = Math.Max(1, Math.Max(min.Width / viewWidth, min.Height / viewHeight));
					viewWidth *= grow;
					viewHeight *= grow;
				}

				double viewLeft = (topLeftX + bottomRightX) / 2 - viewWidth / 2;
				double viewTop = (topLeftY + bottomRightY) / 2 - viewHeight / 2;
				viewWorld = (viewLeft, viewTop, viewWidth, viewHeight);

				// Tiles for more than the view (a caller zooming in on it) - less of it, before the zoom backs off, when the
				// tile budget doesn't allow all of it.
				reach = Math.Max(1, coverage);
				while (true)
				{
					double extraX = viewWidth * (reach - 1) / 2;
					double extraY = viewHeight * (reach - 1) / 2;
					(minTileX, minTileY) = WebMercator.WorldPixelToTile(viewLeft - extraX, viewTop - extraY);
					(maxTileX, maxTileY) = WebMercator.WorldPixelToTile(viewLeft + viewWidth + extraX, viewTop + viewHeight + extraY);
					if (reach <= 1 || (long)(maxTileX - minTileX + 1) * (maxTileY - minTileY + 1) <= MaxTiles) break;
					reach = Math.Max(1, reach * 0.9);
				}
			}

			long tileCount = (long)(maxTileX - minTileX + 1) * (maxTileY - minTileY + 1);
			if (tileCount <= MaxTiles || zoom <= 2) break;
			zoom--;
		}

		int tilesWide = maxTileX - minTileX + 1;
		int tilesHigh = maxTileY - minTileY + 1;
		int totalTiles = tilesWide * tilesHigh;

		var bitmap = new SKBitmap(tilesWide * WebMercator.TileSize, tilesHigh * WebMercator.TileSize);
		bool fetchedAny = false;
		int fetchedCount = 0;
		var drawLock = new Lock();
		var progressLock = new Lock();
		// Reported at most a few times a second (plus always the final tile), not on a fixed tile-count
		// interval - cached tiles resolve from disk in milliseconds, so a count-based throttle (e.g.
		// every 10 tiles) still spams a dozen near-simultaneous log lines once a route is fully cached;
		// this only reports again once meaningful wall-clock time has actually passed.
		var progressStopwatch = Stopwatch.StartNew();
		long lastReportedMs = 0L;
		const int reportIntervalMs = 250;
		try
		{
			using var canvas = new SKCanvas(bitmap);
			canvas.Clear(new SKColor(30, 32, 36));

			// Fetches (network + disk cache I/O) run with bounded concurrency; the actual SKCanvas
			// draw and the fetchedCount bookkeeping stay serialized under drawLock, since
			// SKCanvas isn't safe to call into from more than one thread at a time.
			var coordinates = new List<(int X, int Y)>(totalTiles);
			for (int tx = minTileX; tx <= maxTileX; tx++)
			for (int ty = minTileY; ty <= maxTileY; ty++)
				coordinates.Add((tx, ty));

			var options = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentFetches, CancellationToken = ct };
			await Parallel.ForEachAsync(coordinates, options, FetchAndDrawAsync);

			async ValueTask FetchAndDrawAsync((int X, int Y) coordinate, CancellationToken token)
			{
				(int tileX, int tileY) = coordinate;
					// The labels come in parallel with the tile, not after it.
					Task<SKBitmap?> tileTask = MapTileFetcher.FetchAsync(urlTemplate, zoom, tileX, tileY, cacheMaxAge, token);
					Task<SKBitmap?> labelsTask = labelsUrlTemplate is null
						? Task.FromResult<SKBitmap?>(null)
						: MapTileFetcher.FetchAsync(labelsUrlTemplate, zoom, tileX, tileY, cacheMaxAge, token);
					SKBitmap? tile = null;
					SKBitmap? labels = null;
					try
					{
						try
						{
							await Task.WhenAll(tileTask, labelsTask);
						}
						catch
						{
							// Both have finished by now - the one that didn't fail still owns a bitmap.
							if (tileTask.IsCompletedSuccessfully) tileTask.Result?.Dispose();
							if (labelsTask.IsCompletedSuccessfully) labelsTask.Result?.Dispose();
							throw;
						}

						tile = tileTask.Result;
						labels = labelsTask.Result;
						lock (drawLock)
						{
							if (tile is not null)
							{
								fetchedAny = true;
								var dest = SKRect.Create((tileX - minTileX) * WebMercator.TileSize,
									(tileY - minTileY) * WebMercator.TileSize, WebMercator.TileSize, WebMercator.TileSize);
								canvas.DrawBitmap(tile, dest, SKSamplingOptions.Default);
								if (labels is not null) canvas.DrawBitmap(labels, dest, SKSamplingOptions.Default);
							}

							fetchedCount++;
						}

						// Not under drawLock: a slow callback (the GUI's log) mustn't hold up the other tiles' drawing.
						// Its own lock keeps the reports in order, so the last one is always the full count.
						if (onProgress is not null)
							lock (progressLock)
							{
								int done = fetchedCount;
								if (done == totalTiles || progressStopwatch.ElapsedMilliseconds - lastReportedMs >= reportIntervalMs)
								{
									lastReportedMs = progressStopwatch.ElapsedMilliseconds;
									onProgress(done, totalTiles);
								}
							}
					}
					finally
					{
						tile?.Dispose();
						labels?.Dispose();
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
			AppLogger.Warn(CoreStrings.Map_AllTilesFailed);
			return null;
		}

		double originWorldX = minTileX * (double)WebMercator.TileSize;
		double originWorldY = minTileY * (double)WebMercator.TileSize;
		return new RouteMapMosaic(bitmap, originWorldX, originWorldY, zoom, new GeoBounds(minLat, maxLat, minLon, maxLon),
			viewWorld is { } view
				? SKRect.Create((float)(view.Left - originWorldX), (float)(view.Top - originWorldY), (float)view.Width, (float)view.Height)
				: new SKRect(0, 0, bitmap.Width, bitmap.Height),
			viewWorld is null ? 1 : reach);
	}
}

/// <summary>A latitude/longitude bounding box.</summary>
public readonly record struct GeoBounds(double MinLat, double MaxLat, double MinLon, double MaxLon)
{
	public static GeoBounds Of(IEnumerable<(double Lat, double Lon)> points)
	{
		double minLat = double.MaxValue, maxLat = double.MinValue, minLon = double.MaxValue, maxLon = double.MinValue;
		foreach ((double lat, double lon) in points)
		{
			minLat = Math.Min(minLat, lat);
			maxLat = Math.Max(maxLat, lat);
			minLon = Math.Min(minLon, lon);
			maxLon = Math.Max(maxLon, lon);
		}

		return new GeoBounds(minLat, maxLat, minLon, maxLon);
	}

	public bool Contains(GeoBounds other)
	{
		return other.MinLat >= MinLat && other.MaxLat <= MaxLat && other.MinLon >= MinLon && other.MaxLon <= MaxLon;
	}
}
