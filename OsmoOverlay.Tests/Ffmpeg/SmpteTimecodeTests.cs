using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Tests.Ffmpeg;

[TestClass]
public sealed class SmpteTimecodeTests
{
	private const double Ntsc60 = 60000 / 1001.0;
	private const double Ntsc30 = 30000 / 1001.0;

	[TestMethod]
	// The two range renders checked against a real Osmo recording (timecode 09:59:56;00).
	[DataRow("09:59:56;00", 3596L, Ntsc60, "10:00:55;56")]
	[DataRow("09:59:56;00", 11808L, Ntsc60, "10:03:13;00")]
	// Drop-frame: frames ;00-;03 (59.94) / ;00-;01 (29.97) don't exist at the start of a minute...
	[DataRow("00:00:59;59", 1L, Ntsc60, "00:01:00;04")]
	[DataRow("00:00:59;29", 1L, Ntsc30, "00:01:00;02")]
	// ...except every tenth minute.
	[DataRow("00:09:59;59", 1L, Ntsc60, "00:10:00;00")]
	[DataRow("00:09:59;29", 1L, Ntsc30, "00:10:00;00")]
	// Non-drop-frame counts every frame.
	[DataRow("00:00:59:59", 1L, 60.0, "00:01:00:00")]
	[DataRow("01:02:03:04", 25L, 25.0, "01:02:04:04")]
	[DataRow("23:59:59:59", 1L, 60.0, "00:00:00:00")]
	[DataRow("10:00:00;00", 0L, Ntsc60, "10:00:00;00")]
	public void AddFrames(string timecode, long frames, double fps, string expected)
	{
		Assert.AreEqual(expected, SmpteTimecode.AddFrames(timecode, frames, fps));
	}

	[TestMethod]
	[DataRow("not a timecode", Ntsc60)]
	[DataRow("25:00:00;00", Ntsc60)]
	[DataRow("10:00:00;60", Ntsc60)]
	[DataRow("10:00:00;00", 25.0)]
	public void AddFrames_InvalidInput_ReturnsNull(string timecode, double fps)
	{
		Assert.IsNull(SmpteTimecode.AddFrames(timecode, 1, fps));
	}

	[TestMethod]
	public void AddFrames_IsAdditive_AcrossDropFrameMinutes()
	{
		// Adding in two steps must land where one step does - catches an off-by-drop at minute boundaries.
		for (long a = 0; a < 20_000; a += 997)
		for (long b = 0; b < 20_000; b += 1_499)
		{
			var twoSteps = SmpteTimecode.AddFrames(SmpteTimecode.AddFrames("09:58:31;17", a, Ntsc60)!, b, Ntsc60);
			Assert.AreEqual(SmpteTimecode.AddFrames("09:58:31;17", a + b, Ntsc60), twoSteps, $"a={a}, b={b}");
		}
	}
}
