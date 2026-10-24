using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Tests.Ffmpeg;

[TestClass]
public sealed class EncoderProbeTests
{
	private static readonly VideoInfo Hevc10 = new("hevc", "Main 10", 3840, 2160, "60000/1001", "yuv420p10le", null, null, null, null, 90_000_000);

	[TestMethod]
	public void ProbeArguments_EncodeThreeBlackFramesOfTheOutputsSizeToNowhere()
	{
		List<string> args = FfmpegPipeline.ProbeArguments("libx265", Hevc10);

		CollectionAssert.Contains(args, "color=c=black:s=3840x2160:r=60000/1001");
		Assert.AreEqual("3", args[args.IndexOf("-frames:v") + 1]);
		CollectionAssert.Contains(args, "libx265");
		CollectionAssert.AreEqual(new[] { "-f", "null", "-" }, args.TakeLast(3).ToArray());
	}

	[TestMethod]
	public void ProbeArguments_UseTheRendersOwnEncoderOptions()
	{
		List<string> args = FfmpegPipeline.ProbeArguments("hevc_nvenc", Hevc10);

		Assert.AreEqual("hevc_nvenc", args[args.IndexOf("-c:v") + 1]);
		Assert.AreEqual("main10", args[args.IndexOf("-profile:v") + 1]);
		Assert.AreEqual("p7", args[args.IndexOf("-preset") + 1]);
	}

	[TestMethod]
	public void ProbeArguments_ReportOnlyErrors()
	{
		List<string> args = FfmpegPipeline.ProbeArguments("libx265", Hevc10);

		Assert.AreEqual("error", args[args.IndexOf("-loglevel") + 1]);
	}
}
