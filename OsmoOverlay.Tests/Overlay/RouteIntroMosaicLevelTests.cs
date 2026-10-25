using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class RouteIntroMosaicLevelTests
{
	[TestMethod]
	[DataRow(2f, 0)]
	[DataRow(1f, 0)]
	[DataRow(0.9f, 0)]
	[DataRow(0.5f, 1)]
	[DataRow(0.34f, 1)]
	[DataRow(0.25f, 2)]
	[DataRow(0.1f, 3)]
	[DataRow(0.001f, 4)]
	public void LevelFor_KeepsTheDrawnScaleOverHalf(float scale, int expected)
	{
		Assert.AreEqual(expected, OverlayRenderer.RouteIntroMosaicLevelFor(scale));
		Assert.IsTrue(scale * Math.Pow(2, expected) > 0.5 || expected == 4 || scale >= 1);
	}

	[TestMethod]
	[DataRow(0f)]
	[DataRow(-1f)]
	[DataRow(float.NaN)]
	[DataRow(float.PositiveInfinity)]
	public void LevelFor_ANonsenseScale_UsesTheFullImage(float scale)
	{
		Assert.AreEqual(0, OverlayRenderer.RouteIntroMosaicLevelFor(scale));
	}
}
