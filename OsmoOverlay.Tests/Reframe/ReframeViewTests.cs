using OsmoOverlay.Core.Reframe;

namespace OsmoOverlay.Tests.Reframe;

[TestClass]
public sealed class ReframeViewTests
{
	[TestMethod]
	public void Normalized_WrapsAnglesAndClampsTheRest()
	{
		ReframeView view = new ReframeView(190, 120, -200, 500).Normalized();

		Assert.AreEqual(-170, view.Yaw, 1e-9);
		Assert.AreEqual(90, view.Pitch);
		Assert.AreEqual(160, view.Roll, 1e-9);
		Assert.AreEqual(ReframeView.MaxFov, view.FovDegrees);
	}

	[TestMethod]
	public void TryParse_ReadsWhatToArgumentWrites()
	{
		var view = new ReframeView(12.5, -30, 4, 90);

		Assert.IsTrue(ReframeView.TryParse(view.ToArgument(), out ReframeView parsed));
		Assert.AreEqual(view, parsed);
		Assert.IsTrue(ReframeView.TryParse("10, 20, 0", out ReframeView withoutFov));
		Assert.AreEqual(new ReframeView().FovDegrees, withoutFov.FovDegrees);
		Assert.IsFalse(ReframeView.TryParse("10,20", out _));
		Assert.IsFalse(ReframeView.TryParse("a,b,c", out _));
	}

	[TestMethod]
	[DataRow(1920, 1920, 1080)]
	[DataRow(2880, 2880, 1620)]
	[DataRow(832, 832, 468)]
	public void OutputSize_IsSixteenByNineAsWideAsALens(int lens, int width, int height)
	{
		Assert.AreEqual((width, height), Reframing.OutputSize(new DualFisheye(FisheyeLayout.TwoStreams, lens)));
	}
}
