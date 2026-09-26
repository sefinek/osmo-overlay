using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Tests.Preview;

[TestClass]
public sealed class PreviewFramesTests
{
	private const double Fps = 60000 / 1001.0;

	[TestMethod]
	public void StepPosition_IsItsOwnFrame()
	{
		// Frame stepping seeks a quarter frame before a frame's start (the GUI's SeekToFrame).
		foreach (var frame in new long[] { 1, 59, 3596, 11954 })
			Assert.AreEqual(frame, PreviewFrames.IndexAt((frame - 0.25) / Fps, Fps));
	}

	[TestMethod]
	public void ExactFrameStart_IsThatFrame()
	{
		Assert.AreEqual(3596, PreviewFrames.IndexAt(3596 / Fps, Fps));
	}

	[TestMethod]
	public void SlightlyPastAFrameStart_StillThatFrame()
	{
		Assert.AreEqual(3596, PreviewFrames.IndexAt((3596 + 0.2) / Fps, Fps));
		Assert.AreEqual(3597, PreviewFrames.IndexAt((3596 + 0.3) / Fps, Fps));
	}

	[TestMethod]
	public void Start_IsFrameZero()
	{
		Assert.AreEqual(0, PreviewFrames.IndexAt(0, Fps));
		Assert.AreEqual(0, PreviewFrames.IndexAt(-1, Fps));
	}
}
