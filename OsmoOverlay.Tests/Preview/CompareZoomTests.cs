using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Tests.Preview;

[TestClass]
public sealed class CompareZoomTests
{
	[TestMethod]
	public void Whole_IsTheWholePictureCentered()
	{
		Assert.AreEqual(new CompareZoom(1, 0.5, 0.5), CompareZoom.Whole);
	}

	[TestMethod]
	public void ZoomAt_KeepsThePointUnderThePointerWhereItWas()
	{
		CompareZoom zoom = CompareZoom.Whole.ZoomAt(2, 0.25, 0.75);

		// The picture point at (0.25, 0.75) of the view before was the picture's own (0.25, 0.75): it is still there.
		Assert.AreEqual(0.25, zoom.CenterX + (0.25 - 0.5) / zoom.Scale, 1e-9);
		Assert.AreEqual(0.75, zoom.CenterY + (0.75 - 0.5) / zoom.Scale, 1e-9);
		Assert.AreEqual(2, zoom.Scale, 1e-9);
	}

	[TestMethod]
	public void ZoomAt_TheMiddle_ZoomsIntoTheMiddle()
	{
		CompareZoom zoom = CompareZoom.Whole.ZoomAt(4, 0.5, 0.5);

		Assert.AreEqual(0.5, zoom.CenterX, 1e-9);
		Assert.AreEqual(0.5, zoom.CenterY, 1e-9);
	}

	[TestMethod]
	public void ZoomAt_StaysWithinTheScaleLimits()
	{
		Assert.AreEqual(CompareZoom.MaxScale, CompareZoom.Whole.ZoomAt(1000, 0.5, 0.5).Scale);
		Assert.AreEqual(CompareZoom.MinScale, new CompareZoom(3, 0.5, 0.5).ZoomAt(0.001, 0.5, 0.5).Scale);
	}

	[TestMethod]
	public void ZoomOut_ToTheWhole_Recenters()
	{
		CompareZoom zoom = CompareZoom.Whole.ZoomAt(4, 0.9, 0.1).ZoomAt(0.01, 0.9, 0.1);

		Assert.AreEqual(CompareZoom.Whole, zoom);
	}

	[TestMethod]
	public void Pan_MovesThePictureWithThePointer()
	{
		var zoom = new CompareZoom(2, 0.5, 0.5);

		CompareZoom moved = zoom.Pan(0.1, -0.2);

		Assert.AreEqual(0.5 - 0.05, moved.CenterX, 1e-9);
		Assert.AreEqual(0.5 + 0.1, moved.CenterY, 1e-9);
	}

	[TestMethod]
	public void Pan_CantLeaveThePicture()
	{
		var zoom = new CompareZoom(2, 0.5, 0.5);

		CompareZoom corner = zoom.Pan(5, 5);

		Assert.AreEqual(0.25, corner.CenterX, 1e-9);
		Assert.AreEqual(0.25, corner.CenterY, 1e-9);
	}

	[TestMethod]
	public void Pan_OfTheWholePicture_StaysPut()
	{
		Assert.AreEqual(CompareZoom.Whole, CompareZoom.Whole.Pan(0.3, 0.3));
	}
}
