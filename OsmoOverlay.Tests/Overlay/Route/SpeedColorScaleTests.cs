using OsmoOverlay.Core.Overlay;
using SkiaSharp;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class SpeedColorScaleTests
{
	[TestMethod]
	public void SlowIsTheTrailColor_TopIsRed()
	{
		var trail = new SKColor(0x46, 0xDC, 0x6E);
		SKColor[] colors = SpeedColorScale.Colors(trail);
		Assert.AreEqual(trail, colors[0]);
		Assert.AreEqual(trail, colors[SpeedColorScale.Bucket(0.15)]);
		Assert.AreEqual(new SKColor(0xF0, 0x28, 0x3C), colors[^1]);
	}

	[TestMethod]
	[DataRow(-1.0, 0)]
	[DataRow(0.0, 0)]
	[DataRow(1.0, SpeedColorScale.Buckets - 1)]
	[DataRow(3.0, SpeedColorScale.Buckets - 1)]
	[DataRow(double.NaN, 0)]
	public void Bucket_ClampsToTheScale(double fraction, int expected)
	{
		Assert.AreEqual(expected, SpeedColorScale.Bucket(fraction));
	}
}
