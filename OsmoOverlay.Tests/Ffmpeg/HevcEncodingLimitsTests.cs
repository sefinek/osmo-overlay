using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Tests.Ffmpeg;

[TestClass]
public sealed class HevcEncodingLimitsTests
{
	private static VideoInfo Source(int level, bool? highTier) =>
		new("hevc", "Main", 3840, 2160, "60000/1001", "yuv420p", "bt709", "bt709", "bt709", "tv", 0, level, HighTier: highTier);

	[TestMethod]
	public void MainTierAboveItsLimit_GoesToHighTierOfTheSameLevel()
	{
		// A Mavic Air 2 declares level 5.1 Main Tier and writes 122 Mbps: Main allows 40 Mbps there, High 160.
		VideoInfo result = HevcEncodingLimits.For(Source(153, false), 122_000_000);

		Assert.AreEqual(153, result.Level);
		Assert.AreEqual(true, result.HighTier);
	}

	[TestMethod]
	public void BitrateWithinTheDeclaredTier_ChangesNothing()
	{
		VideoInfo result = HevcEncodingLimits.For(Source(153, false), 30_000_000);

		Assert.AreEqual(153, result.Level);
		Assert.AreEqual(false, result.HighTier);
	}

	[TestMethod]
	public void BitrateAboveEvenHighTier_GoesToTheNextLevelThatHoldsIt()
	{
		VideoInfo result = HevcEncodingLimits.For(Source(153, false), 200_000_000);

		Assert.AreEqual(156, result.Level);
		Assert.AreEqual(true, result.HighTier);
	}

	[TestMethod]
	public void ADeclaredHighTier_IsKept()
	{
		VideoInfo result = HevcEncodingLimits.For(Source(153, true), 30_000_000);

		Assert.AreEqual(153, result.Level);
		Assert.AreEqual(true, result.HighTier);
	}

	[TestMethod]
	public void LevelsWithoutAHighTier_SkipToOneThatHasIt()
	{
		// Level 3.1 has no High Tier and holds 10 Mbps in Main; 12 Mbps needs level 4.0.
		VideoInfo result = HevcEncodingLimits.For(Source(93, false), 12_000_000);

		Assert.AreEqual(120, result.Level);
		Assert.AreEqual(false, result.HighTier);
	}

	[TestMethod]
	public void AnUnknownLevel_IsLeftAlone()
	{
		VideoInfo unknown = Source(0, null);

		Assert.AreSame(unknown, HevcEncodingLimits.For(unknown, 500_000_000));
	}

	[TestMethod]
	public void BitrateBeyondEveryLevel_LeavesTheLevelToTheEncoder()
	{
		VideoInfo result = HevcEncodingLimits.For(Source(183, false), 2_000_000_000);

		Assert.AreEqual(0, result.Level);
		Assert.AreEqual(true, result.HighTier);
	}
}
