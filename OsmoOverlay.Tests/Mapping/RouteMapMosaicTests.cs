using System.Net;
using System.Net.Sockets;
using OsmoOverlay.Core.Mapping;
using SkiaSharp;

namespace OsmoOverlay.Tests.Mapping;

[TestClass]
public sealed class RouteMapMosaicTests
{
	private static readonly List<(double Lat, double Lon)> ShortRoute = [(53.1546, 18.9002), (53.1549, 18.9008), (53.1547, 18.9004)];

	[TestMethod]
	[DataRow(true)]
	[DataRow(false)]
	public async Task BuildAsync_ForARect_CentersTheViewOnTheRoute(bool minimumSize)
	{
		using var server = new TileServer();
		(double, double)? minSize = minimumSize ? (1056, 995) : null;

		using RouteMapMosaic? mosaic = await RouteMapMosaic.BuildAsync(ShortRoute, server.UrlTemplate, minimumSize ? 18 : 17, 0, CancellationToken.None,
			targetAspectRatio: 1.06, cacheMaxAge: TimeSpan.Zero, minSize: minSize);

		Assert.IsNotNull(mosaic);
		SKPoint a = mosaic.GetPixel(53.1546, 18.9002);
		SKPoint b = mosaic.GetPixel(53.1549, 18.9008);
		Assert.AreEqual((a.X + b.X) / 2, mosaic.View.MidX, 2f);
		Assert.AreEqual((a.Y + b.Y) / 2, mosaic.View.MidY, 2f);
		Assert.AreEqual(1.06, mosaic.View.Width / mosaic.View.Height, 0.01);
		Assert.IsTrue(mosaic.View.Left >= 0 && mosaic.View.Top >= 0 && mosaic.View.Right <= mosaic.Image.Width && mosaic.View.Bottom <= mosaic.Image.Height,
			"the tiles cover the whole view");
		if (minimumSize) Assert.IsTrue(mosaic.View.Width >= 1056 - 1, "a short route gets at least the size asked for");
	}

	[TestMethod]
	public async Task BuildAsync_WithCoverage_HasTilesAroundTheView_OrLessWhenTheBudgetRunsOut()
	{
		using var server = new TileServer();

		using RouteMapMosaic? wide = await RouteMapMosaic.BuildAsync(ShortRoute, server.UrlTemplate, 18, 0, CancellationToken.None,
			targetAspectRatio: 1.06, cacheMaxAge: TimeSpan.Zero, minSize: (1056, 995), coverage: 3);
		using RouteMapMosaic? tooWide = await RouteMapMosaic.BuildAsync(ShortRoute, server.UrlTemplate, 18, 0, CancellationToken.None,
			targetAspectRatio: 1.06, cacheMaxAge: TimeSpan.Zero, minSize: (1056, 995), coverage: 40);

		Assert.IsNotNull(wide);
		Assert.AreEqual(3, wide.Coverage);
		Assert.IsTrue(wide.View.Left >= wide.View.Width, "a whole view's width of tiles to the left of it, at least");
		Assert.IsNotNull(tooWide);
		Assert.IsTrue(tooWide.Coverage is > 1 and < 40, "the budget cuts the reach, not the zoom");
		Assert.AreEqual(18, tooWide.Zoom);
	}

	[TestMethod]
	public async Task BuildAsync_WithoutARect_ShowsTheWholeImage()
	{
		using var server = new TileServer();

		using RouteMapMosaic? mosaic = await RouteMapMosaic.BuildAsync(ShortRoute, server.UrlTemplate, 17, 0, CancellationToken.None, cacheMaxAge: TimeSpan.Zero);

		Assert.IsNotNull(mosaic);
		Assert.AreEqual(new SKRect(0, 0, mosaic.Image.Width, mosaic.Image.Height), mosaic.View);
	}

	private sealed class TileServer : IDisposable
	{
		private readonly HttpListener _listener = new();
		private readonly byte[] _tile;
		private readonly CancellationTokenSource _stop = new();

		public TileServer()
		{
			using var bitmap = new SKBitmap(256, 256);
			bitmap.Erase(new SKColor(90, 140, 90));
			using SKData png = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
			_tile = png.ToArray();

			// A free port: a listener on a fixed one would collide with a second test run.
			int port = FreePort();
			UrlTemplate = $"http://127.0.0.1:{port}/{{z}}/{{x}}/{{y}}.png";
			_listener.Prefixes.Add($"http://127.0.0.1:{port}/");
			_listener.Start();
			_ = Task.Run(Serve);
		}

		public string UrlTemplate { get; }

		public void Dispose()
		{
			_stop.Cancel();
			_listener.Close();
			_stop.Dispose();
		}

		private static int FreePort()
		{
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			int port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			return port;
		}

		private async Task Serve()
		{
			while (!_stop.IsCancellationRequested)
			{
				try
				{
					HttpListenerContext context = await _listener.GetContextAsync();
					context.Response.ContentType = "image/png";
					context.Response.ContentLength64 = _tile.Length;
					await context.Response.OutputStream.WriteAsync(_tile);
					context.Response.Close();
				}
				catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
				{
					return;
				}
				catch (HttpListenerException)
				{
				}
			}
		}
	}
}
