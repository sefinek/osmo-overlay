using System.Diagnostics;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;
using SkiaSharp;

namespace OsmoOverlay.Tests.Telemetry.External;

[TestClass]
public sealed class TmpBackdropPerf
{
	[TestMethod]
	public void Measure()
	{
		int size = 8192;
		SKBitmap Make()
		{
		var bitmap = new SKBitmap(size, size);
		using (var canvas = new SKCanvas(bitmap))
		{
			canvas.Clear(new SKColor(70, 100, 60));
			using var stripe = new SKPaint { Color = new SKColor(90, 120, 80) };
			for (int x = 0; x < size; x += 128) canvas.DrawRect(x, 0, 64, size, stripe);
		}

			return bitmap;
		}

		List<DerivedFrame> frames = [];
		int zoom = 14;
		for (int i = 0; i <= 6000; i++)
		{
			double n = 256 * Math.Pow(2, zoom);
			double lon = (800 + i * 0.9) / n * 360 - 180;
			double lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (1024 + 400 * Math.Sin(i / 300.0)) / n))) * 180 / Math.PI;
			var raw = new TelemetryFrame(i, i / 10.0, lat, lon, 0, null, 0, 0, 1, HasGpsFix: true, DroneAttitude: new DroneAttitude(5, -10, 90 + i * 0.1));
			frames.Add(new DerivedFrame(raw, 20 + 20 * Math.Sin(i / 100.0), 0, 0, 0, 0, 0, default, 0, 0, 1, 0, 0));
		}

		var report = new System.Text.StringBuilder();
		foreach ((bool follow, bool trail) in new[] { (true, true), (true, false), (false, true) })
		{
			var mosaic = new RouteMapMosaic(Make(), 0, 0, zoom, new GeoBounds(0, 1, 0, 1), new SKRect(0, 0, size, size), 1);
			using var backdrop = new TrackBackdrop(mosaic, new TrackMapSettings(Follow: follow, ShowTrail: trail), 1920, frames);
			byte[] buf = new byte[1920 * 1080 * 4];
			for (int i = 0; i < 5; i++) backdrop.DrawInto(buf, 1920, 1080, 100 + i / 50.0, frames);
			var sw = Stopwatch.StartNew();
			for (int i = 0; i < 100; i++) backdrop.DrawInto(buf, 1920, 1080, 100 + i / 50.0, frames);
			report.AppendLine($"follow={follow} trail={trail}: {sw.Elapsed.TotalMilliseconds / 100:F2} ms/frame");
		}

		File.WriteAllText("D:/perf.txt", report.ToString());
	}
}
