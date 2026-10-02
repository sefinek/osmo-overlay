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
	public void ErrorWhileEncoding_SkipsTheOutputDescriptionAndProgress()
	{
		string stderr = "Stream mapping:\n  Stream #0:0 (hevc) -> overlay\nPress [q] to stop, [?] for help\n" +
		                "Output #0, mp4, to 'out.mp4':\n  Metadata:\n    encoder         : Lavf61.7.100\n" +
		                "  Stream #0:0: Video: hevc (hvc1), yuv420p10le, 3840x2160\n" +
		                "frame=  120 fps= 60 q=-0.0 size=    1024KiB time=00:00:02.00 bitrate=4194.3kbits/s speed=1x\r" +
		                "frame=  240 fps= 60 q=-0.0 size=    2048KiB time=00:00:04.00 bitrate=4194.3kbits/s speed=1x\r\n" +
		                "[hevc_nvenc @ 0001] EncodeAPI: out of memory\n" +
		                "[vost#0:0/hevc_nvenc @ 0002] Error submitting video frame to the encoder\n" +
		                "Conversion failed!\n";

		string summary = RenderJob.FfmpegErrorSummary(stderr);

		Assert.AreEqual("[hevc_nvenc @ 0001] EncodeAPI: out of memory\n" +
		                "[vost#0:0/hevc_nvenc @ 0002] Error submitting video frame to the encoder\nConversion failed!", summary);
	}

	[TestMethod]
	public void ErrorBeforeRunning_TakesTheLastLines()
	{
		string stderr = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}"));

		string summary = RenderJob.FfmpegErrorSummary(stderr, 3);

		Assert.AreEqual("line 18\nline 19\nline 20", summary);
	}
}
