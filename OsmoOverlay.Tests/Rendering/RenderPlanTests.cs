using OsmoOverlay.Core;

namespace OsmoOverlay.Tests;

[TestClass]
public sealed class RenderPlanTests
{
	private const double Fps = 60000 / 1001.0;
	private const long SourceFrames = 11955;

	private static RenderPlan Resolve(double? from = null, double? to = null, int? frameLimit = null, params TimeRange[] cuts)
	{
		return RenderPlan.Resolve(from, to, cuts, frameLimit, Fps, SourceFrames);
	}

	private static long F(double seconds)
	{
		return (long)Math.Round(seconds * Fps);
	}

	[TestMethod]
	public void NoRange_IsTheWholeRecording()
	{
		RenderPlan plan = Resolve();

		CollectionAssert.AreEqual(new[] { new RenderPiece(0, SourceFrames) }, plan.Pieces.ToArray());
		Assert.IsFalse(plan.IsPartial);
	}

	[TestMethod]
	public void Start_RoundsToNearestFrame()
	{
		// 60 s * 59.94 = 3596.4 -> frame 3596, the value the CLI render was verified with.
		CollectionAssert.AreEqual(new[] { new RenderPiece(3596, SourceFrames - 3596) }, Resolve(60).Pieces.ToArray());
		Assert.IsTrue(Resolve(60).IsPartial);
	}

	[TestMethod]
	public void EndPastTheRecording_IsClamped()
	{
		Assert.IsFalse(Resolve(to: 10_000).IsPartial);
	}

	[TestMethod]
	public void Cut_SplitsTheRangeIntoTwoPieces()
	{
		// The single-file render verified frame-exact against the source: 1:00-1:20 minus 1:05-1:10.
		RenderPlan plan = Resolve(60, 80, null, new TimeRange(65, 70));

		CollectionAssert.AreEqual(new[] { new RenderPiece(F(60), F(65) - F(60)), new RenderPiece(F(70), F(80) - F(70)) }, plan.Pieces.ToArray());
		Assert.AreEqual(899, plan.TotalFrames);
	}

	[TestMethod]
	public void OverlappingAndTouchingCuts_Merge()
	{
		RenderPlan plan = Resolve(null, null, null, new TimeRange(30, 40), new TimeRange(35, 50), new TimeRange(50, 55), new TimeRange(100, 110));

		CollectionAssert.AreEqual(new[]
		{
			new RenderPiece(0, F(30)), new RenderPiece(F(55), F(100) - F(55)), new RenderPiece(F(110), SourceFrames - F(110))
		}, plan.Pieces.ToArray());
	}

	[TestMethod]
	public void CutsOutsideTheRange_AreIgnored_AndOnesAcrossItsEdgesTrimIt()
	{
		RenderPlan plan = Resolve(60, 120, null, new TimeRange(10, 20), new TimeRange(50, 70), new TimeRange(110, 150), new TimeRange(180, 190));

		CollectionAssert.AreEqual(new[] { new RenderPiece(F(70), F(110) - F(70)) }, plan.Pieces.ToArray());
	}

	[TestMethod]
	public void FrameLimit_CapsTheTotal_AcrossPieces()
	{
		RenderPlan plan = Resolve(60, 120, (int)(F(65) - F(60)) + 10, new TimeRange(65, 70));

		CollectionAssert.AreEqual(new[] { new RenderPiece(F(60), F(65) - F(60)), new RenderPiece(F(70), 10) }, plan.Pieces.ToArray());
	}

	[TestMethod]
	public void FrameLimit_LargerThanTheRange_ChangesNothing()
	{
		Assert.IsFalse(Resolve(frameLimit: 1_000_000).IsPartial);
	}

	[TestMethod]
	[DataRow(10.0, 10.0)]
	[DataRow(20.0, 10.0)]
	[DataRow(500.0, null)]
	public void EmptyRange_Throws(double from, double? to)
	{
		Assert.ThrowsExactly<InvalidOperationException>(() => Resolve(from, to));
	}

	[TestMethod]
	public void CutCoveringEverything_Throws()
	{
		Assert.ThrowsExactly<InvalidOperationException>(() => Resolve(60, 70, null, new TimeRange(50, 80)));
	}
}
