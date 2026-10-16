using OsmoOverlay.Core;

namespace OsmoOverlay.Tests.Rendering;

[TestClass]
public sealed class RenderOptionsTests
{
	[TestMethod]
	public void RangeOutputPath_PutsThePartsTimesInTheName()
	{
		string path = RenderOptions.RangeOutputPath(Path.Combine("out", "clip_overlay.mp4"), 90.4, 130.9);

		Assert.AreEqual(Path.Combine("out", "clip_01m30s-02m10s.mp4"), path);
	}

	[TestMethod]
	public void RangeOutputPath_HoursAndAPathWithoutTheOverlaySuffix()
	{
		Assert.AreEqual(Path.Combine("out", "ride_00m00s-1h02m03s.mp4"), RenderOptions.RangeOutputPath(Path.Combine("out", "ride.mp4"), 0, 3723));
		Assert.AreEqual(Path.Combine("out", "ride_00m00s-00m00s.mp4"), RenderOptions.RangeOutputPath(Path.Combine("out", "ride.mp4"), -5, 0.4));
	}

	[TestMethod]
	public void RangeOutputPath_IsNeverTheWholeRendersPath()
	{
		string whole = Path.Combine("out", "clip_overlay.mp4");

		Assert.AreNotEqual(whole, RenderOptions.RangeOutputPath(whole, 0, 10));
	}
}
