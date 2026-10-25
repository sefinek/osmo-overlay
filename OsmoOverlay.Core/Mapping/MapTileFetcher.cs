using System.Net;
using System.Security.Cryptography;
using System.Text;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Updates;
using SkiaSharp;

namespace OsmoOverlay.Core.Mapping;

/// <summary>
///     Downloads XYZ map tiles and caches them on disk, so re-rendering the same or an overlapping route doesn't re-fetch
///     what's already there - for as long as the provider's terms allow (MapTerms.CacheDays: CARTO 30 days), else for good;
///     a provider whose terms forbid storing its data (CacheDays 0, a zero max age: Esri) is never written or read.
///     Identifies itself with a real User-Agent, as tile servers ask; a custom tile URL (OverlaySettings.CustomMapUrlTemplate)
///     is the user's own responsibility to use within its provider's terms.
/// </summary>
public static class MapTileFetcher
{
	private static readonly HttpClient Http = CreateHttpClient();

	private static readonly string CacheDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "map-tiles");

	private static HttpClient CreateHttpClient()
	{
		var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd($"OsmoOverlay/{AppUpdates.CurrentVersion} (+{AppUpdates.RepositoryUrl})");
		return client;
	}

	// A public tile server sometimes rate-limits or times out one of a burst of requests - without a retry that tile
	// stays a hole in the mosaic (plain to see on the route overview). Few, so being offline doesn't take long.
	private const int MaxFetchAttempts = 3;

	/// <summary>Null after MaxFetchAttempts failures (offline, 404, timeout, corrupt image) - callers draw a placeholder instead of failing the render.</summary>
	public static async Task<SKBitmap?> FetchAsync(string urlTemplate, int zoom, int x, int y, TimeSpan? cacheMaxAge, CancellationToken ct)
	{
		bool store = cacheMaxAge != TimeSpan.Zero;
		string cachePath = CachePath(urlTemplate, zoom, x, y);
		var cacheFile = new FileInfo(cachePath);
		// An older one is fetched again and written over.
		if (store && cacheFile.Exists && (cacheMaxAge is not { } maxAge || cacheFile.LastWriteTimeUtc > DateTime.UtcNow - maxAge))
		{
			// Not `using`: the caller owns and disposes the bitmap.
			var cached = SKBitmap.Decode(cachePath);
			if (cached is not null) return cached;
		}

		for (int attempt = 1; attempt <= MaxFetchAttempts; attempt++)
		{
			try
			{
				(SKBitmap bitmap, byte[]? bytes) = await DownloadTileAsync(urlTemplate, zoom, x, y, store, ct);

				if (bytes is not null)
				{
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
				}

				return bitmap;
			}
			catch (Exception ex) when (!ct.IsCancellationRequested)
			{
				if (attempt == MaxFetchAttempts || !IsTransient(ex))
				{
					AppLogger.Warn(ex, string.Format(CoreStrings.Map_FetchTileFailed, $"z={zoom} x={x} y={y}", MaxFetchAttempts));
					return null;
				}

				await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct);
			}
		}

		return null;
	}

	// A timeout is a TaskCanceledException too, hence the token check in FetchAsync rather than the exception type.
	private static bool IsTransient(Exception ex)
	{
		return ex is not HttpRequestException { StatusCode: { } status } || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
	}

	/// <summary>
	///     The tile and the bytes to cache for it. Decoded before caching - a provider answering 200 with an error page/JSON
	///     instead of an image must count as a failed attempt, not get written to disk as a "tile" forever. A template with
	///     {hz}/{hx}/{hy} serves 512 px tiles a zoom level lower (ArcGIS Static Basemap Tiles): one covers this tile and its
	///     three neighbours, so this one is its quarter, at the same resolution - and the four share one download. The
	///     quarter is encoded for the cache only when it's stored.
	/// </summary>
	private static async Task<(SKBitmap Bitmap, byte[]? Bytes)> DownloadTileAsync(string urlTemplate, int zoom, int x, int y, bool store,
		CancellationToken ct)
	{
		if (!urlTemplate.Contains("{hz}"))
		{
			string url = urlTemplate.Replace("{z}", zoom.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
			byte[] bytes = store ? await Http.GetByteArrayAsync(url, ct) : await DownloadSharedAsync(url, ct);
			SKBitmap bitmap = SKBitmap.Decode(bytes) ?? throw NotAnImage(bytes);
			return (bitmap, store ? bytes : null);
		}

		string halfUrl = urlTemplate.Replace("{hz}", (zoom - 1).ToString()).Replace("{hx}", (x >> 1).ToString()).Replace("{hy}", (y >> 1).ToString());
		byte[] whole = await DownloadSharedAsync(halfUrl, ct);
		using SKBitmap big = SKBitmap.Decode(whole) ?? throw NotAnImage(whole);
		int size = big.Width / 2;
		using var subset = new SKBitmap();
		if (!big.ExtractSubset(subset, SKRectI.Create((x & 1) * size, (y & 1) * size, size, size))) throw NotAnImage(whole);
		SKBitmap quarter = subset.Copy() ?? throw NotAnImage(whole);
		if (!store) return (quarter, null);

		using var image = SKImage.FromBitmap(quarter);
		using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
		return (quarter, png.ToArray());
	}

	private static InvalidDataException NotAnImage(byte[] bytes)
	{
		return new InvalidDataException($"Tile response ({bytes.Length} bytes) is not a decodable image.");
	}

	// Downloads kept in memory only, never on disk: one download per 512 px tile for the four quarters fetched at once, and
	// for a provider that forbids storing (no disk cache) the tiles two mosaics share, or one fetched again after a cancelled
	// fetch. Short-lived and bounded; a failed download is dropped so a retry asks again.
	private static readonly TimeSpan SharedDownloadLifetime = TimeSpan.FromMinutes(5);
	private const long SharedDownloadBudgetBytes = 48L * 1024 * 1024;
	private static readonly Dictionary<string, (Task<byte[]> Download, DateTime Added)> SharedDownloads = new();
	private static readonly Lock SharedDownloadsLock = new();

	private static async Task<byte[]> DownloadSharedAsync(string url, CancellationToken ct)
	{
		Task<byte[]> download;
		lock (SharedDownloadsLock)
		{
			TrimSharedDownloads();
			if (!SharedDownloads.TryGetValue(url, out var entry))
			{
				entry = (Http.GetByteArrayAsync(url, CancellationToken.None), DateTime.UtcNow);
				SharedDownloads[url] = entry;
			}

			download = entry.Download;
		}

		try
		{
			return await download.WaitAsync(ct);
		}
		catch when (!ct.IsCancellationRequested)
		{
			lock (SharedDownloadsLock)
			{
				if (SharedDownloads.TryGetValue(url, out var entry) && entry.Download == download) SharedDownloads.Remove(url);
			}

			throw;
		}
	}

	// Under SharedDownloadsLock. Only finished downloads are ever dropped - one still running is what the others wait on.
	private static void TrimSharedDownloads()
	{
		DateTime expired = DateTime.UtcNow - SharedDownloadLifetime;
		long total = 0;
		List<KeyValuePair<string, (Task<byte[]> Download, DateTime Added)>> finished = [];
		foreach (var pair in SharedDownloads)
		{
			if (!pair.Value.Download.IsCompleted) continue;
			if (pair.Value.Added < expired || !pair.Value.Download.IsCompletedSuccessfully)
			{
				SharedDownloads.Remove(pair.Key);
				continue;
			}

			total += pair.Value.Download.Result.Length;
			finished.Add(pair);
		}

		if (total <= SharedDownloadBudgetBytes) return;

		foreach (var pair in finished.OrderBy(p => p.Value.Added))
		{
			SharedDownloads.Remove(pair.Key);
			total -= pair.Value.Download.Result.Length;
			if (total <= SharedDownloadBudgetBytes) break;
		}
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
