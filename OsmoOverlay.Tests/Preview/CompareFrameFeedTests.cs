using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Tests.Preview;

[TestClass]
public sealed class CompareFrameFeedTests
{
	private const int Size = 16;

	private static ComposedPreviewFrame Frame(FrameBufferPool pool, double seconds)
	{
		return new ComposedPreviewFrame(TimeSpan.FromSeconds(seconds), pool.Rent(Size), 2, 2) { Owner = pool };
	}

	[TestMethod]
	public void TakeDueFrame_NothingDueYet_IsNull_AndKeepsTheFrame()
	{
		double now = 0;
		var feed = new CompareFrameFeed(() => now);
		feed.Enqueue(Frame(new FrameBufferPool(4), 1));

		Assert.IsNull(feed.TakeDueFrame());
		Assert.AreEqual(1, feed.Count);
	}

	[TestMethod]
	public void TakeDueFrame_GivesTheNewestDueFrame_AndHandsTheSkippedOnesBack()
	{
		double now = 1.05;
		var pool = new FrameBufferPool(4);
		var feed = new CompareFrameFeed(() => now);
		ComposedPreviewFrame skipped = Frame(pool, 1.0);
		ComposedPreviewFrame due = Frame(pool, 1.04);
		ComposedPreviewFrame later = Frame(pool, 1.08);
		feed.Enqueue(skipped);
		feed.Enqueue(due);
		feed.Enqueue(later);

		ComposedPreviewFrame? taken = feed.TakeDueFrame();

		Assert.AreSame(due, taken);
		Assert.AreEqual(1, feed.Count, "the frame for later stays");
		Assert.AreSame(skipped.Bgra, pool.Rent(Size), "the frame the clock passed is back in the pool");
	}

	[TestMethod]
	public void TakeDueFrame_AFrameAtTheClock_IsDue()
	{
		var feed = new CompareFrameFeed(() => 2.0);
		feed.Enqueue(Frame(new FrameBufferPool(4), 2.0));

		Assert.IsNotNull(feed.TakeDueFrame());
	}

	[TestMethod]
	public void Clear_HandsEveryWaitingFrameBack()
	{
		var pool = new FrameBufferPool(4);
		var feed = new CompareFrameFeed(() => 0);
		ComposedPreviewFrame waiting = Frame(pool, 5);
		feed.Enqueue(waiting);

		feed.Clear();

		Assert.AreEqual(0, feed.Count);
		Assert.AreSame(waiting.Bgra, pool.Rent(Size));
	}

	[TestMethod]
	public void IsPlaying_FollowsSetPlaying()
	{
		var feed = new CompareFrameFeed(() => 0);

		feed.SetPlaying(true);
		Assert.IsTrue(feed.IsPlaying);
		feed.SetPlaying(false);
		Assert.IsFalse(feed.IsPlaying);
	}
}
