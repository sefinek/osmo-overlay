using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Ffmpeg;

[TestClass]
public sealed class OutputVideoTests
{
	private static readonly VideoInfo Osmo = new("hevc", "Main 10", 3840, 2160, "60000/1001", "yuv420p10le", "bt709", "bt709", "bt709", "tv",
		73_000_000, 156, 60, null, true);

	[TestMethod]
	public void Default_IsTheSourceItself()
	{
		Assert.AreSame(Osmo, OutputVideo.For(Osmo, new OverlaySettings()));
		Assert.AreEqual("", FfmpegPipeline.ResizeFilter(Osmo, Osmo), "a default render's graph stays as it was");
	}

	[TestMethod]
	public void Resolution_ScalesTheShortSide_AndTheBitrateWithThePixels()
	{
		VideoInfo output = OutputVideo.For(Osmo, new OverlaySettings { OutputResolution = 1080 });

		Assert.AreEqual((1920, 1080), (output.Width, output.Height));
		Assert.AreEqual(73_000_000 / 4, output.BitRate);
		Assert.AreEqual("yuv420p10le", output.PixFmt, "the depth stays unless asked");
		Assert.AreEqual(0, output.Level, "the source's level is for its own size - the encoder picks one");
		Assert.IsNull(output.HighTier);
		Assert.AreEqual("scale=1920:1080:flags=lanczos:threads=0,format=yuv420p10le,", FfmpegPipeline.ResizeFilter(Osmo, output));
	}

	[TestMethod]
	public void Resolution_NeverUpscales_AndKeepsAPortraitPortrait()
	{
		Assert.AreSame(Osmo, OutputVideo.For(Osmo, new OverlaySettings { OutputResolution = 2160 }));

		VideoInfo portrait = Osmo with { Width = 2160, Height = 3840 };
		VideoInfo output = OutputVideo.For(portrait, new OverlaySettings { OutputResolution = 720 });
		Assert.AreEqual((720, 1280), (output.Width, output.Height));
	}

	[TestMethod]
	[DataRow(2704, 1520, 1080, 1922, 1080)]
	[DataRow(2688, 1512, 720, 1280, 720)]
	public void Scaled_KeepsBothSidesEven(int width, int height, int shortSide, int expectedWidth, int expectedHeight)
	{
		Assert.AreEqual((expectedWidth, expectedHeight), OutputVideo.Scaled(width, height, shortSide));
	}

	[TestMethod]
	public void H264_IsAlways8Bit_WithMoreBitrateThanHevc()
	{
		VideoInfo output = OutputVideo.For(Osmo, new OverlaySettings { OutputCodec = "h264" });

		Assert.AreEqual("h264", output.CodecName);
		Assert.AreEqual("yuv420p", output.PixFmt);
		Assert.AreEqual(73_000_000 * 1.5, output.BitRate);
		Assert.IsTrue(FfmpegPipeline.Encodes("h264_nvenc", output));
		Assert.IsFalse(FfmpegPipeline.Encodes("hevc_nvenc", output), "an encoder picked for the source no longer fits");
	}

	[TestMethod]
	public void EightBit_OnlyChangesATenBitSource()
	{
		VideoInfo output = OutputVideo.For(Osmo, new OverlaySettings { OutputEightBit = true });
		Assert.AreEqual("yuv420p", output.PixFmt);
		Assert.AreEqual(156, output.Level, "same size and codec - the source's level still fits");
		Assert.AreEqual("scale=3840:2160:flags=lanczos:threads=0,format=yuv420p,", FfmpegPipeline.ResizeFilter(Osmo, output));

		VideoInfo eightBitSource = Osmo with { PixFmt = "yuv420p" };
		Assert.AreSame(eightBitSource, OutputVideo.For(eightBitSource, new OverlaySettings { OutputEightBit = true }));
	}
}
