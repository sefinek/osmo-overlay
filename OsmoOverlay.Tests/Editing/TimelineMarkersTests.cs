using OsmoOverlay.Core;

namespace OsmoOverlay.Tests;

[TestClass]
public sealed class TimelineMarkersTests
{
	private const long Total = 1000;

	private static SortedSet<long> Markers(FrameRange? selection = null)
	{
		return TimelineMarkers.Collect(Total, [new FrameRange(100, 200), new FrameRange(900, Total)], selection, [new FrameRange(500, 550)]);
	}

	[TestMethod]
	public void Collect_HasTheEndsAndEveryEdge()
	{
		CollectionAssert.AreEqual(new long[] { 0, 100, 200, 500, 550, 900, 999 }, Markers().ToArray());
	}

	[TestMethod]
	public void Collect_IncludesTheSelection()
	{
		CollectionAssert.Contains(Markers(new FrameRange(300, 351)).ToArray(), 300L);
		CollectionAssert.Contains(Markers(new FrameRange(300, 351)).ToArray(), 351L);
	}

	[TestMethod]
	public void Next_IsTheFirstMarkerAfterTheFrame()
	{
		Assert.AreEqual(100, TimelineMarkers.Next(Markers(), 0));
		Assert.AreEqual(200, TimelineMarkers.Next(Markers(), 100));
		Assert.AreEqual(500, TimelineMarkers.Next(Markers(), 350));
		Assert.IsNull(TimelineMarkers.Next(Markers(), 999));
	}

	[TestMethod]
	public void Previous_IsTheLastMarkerBeforeTheFrame()
	{
		Assert.AreEqual(900, TimelineMarkers.Previous(Markers(), 999));
		Assert.AreEqual(200, TimelineMarkers.Previous(Markers(), 350));
		Assert.AreEqual(100, TimelineMarkers.Previous(Markers(), 200));
		Assert.IsNull(TimelineMarkers.Previous(Markers(), 0));
	}
}
