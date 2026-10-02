using OsmoOverlay.Core;

namespace OsmoOverlay.Tests.Rendering;

[TestClass]
public sealed class FfmpegErrorSummaryTests
{
	[TestMethod]
	public void RunTimeError_TakesTheLinesAfterTheStreamMapping()
	{
		const string stderr = """
		                      Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'a.MP4':
		                        Stream #0:0[0x1](und): Video: hevc (Main 10)
		                      [in#1/concat @ 0001] Could not find codec parameters for stream 2 (Unknown: none): unknown codec
		                      Stream mapping:
		                        Stream #0:0 (hevc) -> concat
		                        overlay:default -> Stream #0:0 (hevc_nvenc)
		                      [Parsed_xfade_18 @ 0002] First input link main timebase (1/1000000) do not match
		                      [fc#0 @ 0003] Error reinitializing filters!
		                      Conversion failed
		                      """;

		string summary = RenderJob.FfmpegErrorSummary(stderr);

		Assert.AreEqual("[Parsed_xfade_18 @ 0002] First input link main timebase (1/1000000) do not match\n" +
		                "[fc#0 @ 0003] Error reinitializing filters!\nConversion failed", summary);
	}

	[TestMethod]
	public void ErrorBeforeRunning_TakesTheLastLines()
	{
		string stderr = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}"));

		string summary = RenderJob.FfmpegErrorSummary(stderr, 3);

		Assert.AreEqual("line 18\nline 19\nline 20", summary);
	}
}
