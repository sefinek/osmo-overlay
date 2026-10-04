using OsmoOverlay.Core;

namespace OsmoOverlay.Tests;

[TestClass]
public sealed class RenderDiskSpaceTests
{
	[TestMethod]
	public void EstimateOutputBytes_FollowsTheShareOfFramesKept()
	{
		long whole = RenderDiskSpace.EstimateOutputBytes(1_000_000_000, 3000, 3000);
		long half = RenderDiskSpace.EstimateOutputBytes(1_000_000_000, 3000, 1500);

		Assert.AreEqual((long)(1_000_000_000 * RenderDiskSpace.Headroom), whole);
		Assert.AreEqual((long)(500_000_000 * RenderDiskSpace.Headroom), half);
	}

	[TestMethod]
	public void EstimateOutputBytes_IsZeroWithoutASize_AndNeverMoreThanTheWholeSource()
	{
		Assert.AreEqual(0, RenderDiskSpace.EstimateOutputBytes(0, 3000, 3000));
		Assert.AreEqual(0, RenderDiskSpace.EstimateOutputBytes(1000, 0, 3000));
		Assert.AreEqual(RenderDiskSpace.EstimateOutputBytes(1000, 3000, 3000), RenderDiskSpace.EstimateOutputBytes(1000, 3000, 6000));
	}

	[TestMethod]
	public void RequiredBytes_LeavesRoomForTheFinishingCopy()
	{
		Assert.AreEqual(2000, RenderDiskSpace.RequiredBytes(1000));
	}

	[TestMethod]
	public void IsUnder_MatchesWholeFolderNamesOnly()
	{
		char s = Path.DirectorySeparatorChar;
		string root = $"{s}mnt{s}d";

		Assert.IsTrue(RenderDiskSpace.IsUnder($"{root}{s}video.mp4", root));
		Assert.IsTrue(RenderDiskSpace.IsUnder($"{root}{s}video.mp4", $"{root}{s}"));
		Assert.IsFalse(RenderDiskSpace.IsUnder($"{s}mnt{s}data{s}video.mp4", root));
	}

	[TestMethod]
	public void AvailableBytes_FindsTheDriveOfATempFile()
	{
		Assert.IsNotNull(RenderDiskSpace.AvailableBytes(Path.Combine(Path.GetTempPath(), "render.mp4")));
	}
}
