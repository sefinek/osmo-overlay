using OsmoOverlay.Core;

namespace OsmoOverlay.Tests;

[TestClass]
public sealed class CutListTests
{
	private const long Total = 1000;

	private static FrameRange R(long start, long end)
	{
		return new FrameRange(start, end);
	}

	[TestMethod]
	public void Normalize_SortsClampsAndDropsEmpty()
	{
		List<FrameRange> cuts = CutList.Normalize([R(900, 1200), R(-5, 10), R(50, 50), R(400, 300)], Total);

		CollectionAssert.AreEqual(new[] { R(0, 10), R(900, 1000) }, cuts);
	}

	[TestMethod]
	public void Normalize_MergesOverlappingAndTouching()
	{
		List<FrameRange> cuts = CutList.Normalize([R(100, 200), R(150, 250), R(250, 300), R(400, 500), R(120, 130)], Total);

		CollectionAssert.AreEqual(new[] { R(100, 300), R(400, 500) }, cuts);
	}

	[TestMethod]
	public void Add_MergesWithTheCutsItTouches()
	{
		CollectionAssert.AreEqual(new[] { R(0, 100), R(200, 450) }, CutList.Add([R(0, 100), R(200, 300), R(400, 450)], R(250, 400), Total));
	}

	[TestMethod]
	public void RemovedFrames_CountsOverlapOnce()
	{
		Assert.AreEqual(250, CutList.RemovedFrames([R(100, 200), R(150, 300), R(900, 950)], Total));
	}

	[TestMethod]
	public void RemovesEverything_OnlyWhenNothingIsLeft()
	{
		Assert.IsTrue(CutList.RemovesEverything([R(0, 600), R(500, Total)], Total));
		Assert.IsFalse(CutList.RemovesEverything([R(0, 600), R(601, Total)], Total));
	}

	[TestMethod]
	public void TimeRanges_ResolveBackToTheSameFrames()
	{
		const double fps = 60000 / 1001.0;
		const long total = 11955;
		FrameRange[] cuts = [R(0, 3596), R(5000, 5001), R(11000, total)];

		RenderPlan plan = RenderPlan.Resolve(null, null, CutList.ToTimeRanges(cuts, fps), null, fps, total);

		CollectionAssert.AreEqual(new[] { new RenderPiece(3596, 5000 - 3596), new RenderPiece(5001, 11000 - 5001) },
			plan.Pieces.ToArray());
	}
}
