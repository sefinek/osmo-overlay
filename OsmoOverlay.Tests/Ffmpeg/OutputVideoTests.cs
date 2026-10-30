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
	public void FilterGraph_IsTheRendersOwn()
	{
		// As logged by a real render of an Osmo Action 6 recording.
		Assert.AreEqual("[0:v]format=yuv420p10le,setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv[main];" +
		                "[1:v]scale=out_color_matrix=bt709:out_range=tv:threads=0,format=yuva420p10le[ovl];" +
		                "[main][ovl]overlay=format=yuv420p10:shortest=1[v]",
			FfmpegPipeline.FilterGraph("[0:v]", 1, Osmo, Osmo, false, false));
		StringAssert.StartsWith(FfmpegPipeline.FilterGraph("[0:v]", 1, Osmo, OutputVideo.For(Osmo, new OverlaySettings { OutputResolution = 1080 }), false, false),
			"[0:v]scale=1920:1080:flags=lanczos:threads=0,format=yuv420p10le,setparams=");
	}

	[TestMethod]
	public void Aspect_CutsTheShapeFromTheMiddle_AndTheBitrateFollowsWhatIsLeft()
	{
		VideoInfo output = OutputVideo.For(Osmo, new OverlaySettings { OutputAspect = "9:16" });

		Assert.AreEqual((1216, 2160), (output.Width, output.Height));
		Assert.AreEqual((long)Math.Round(73_000_000 * 1216.0 / 3840), output.BitRate);
		Assert.AreEqual("crop=1216:2160,format=yuv420p10le,", FfmpegPipeline.ResizeFilter(Osmo, output));
	}

	[TestMethod]
	public void Aspect_WithAResolution_CutsFirstAndScalesTheCutPicture()
	{
		VideoInfo output = OutputVideo.For(Osmo, new OverlaySettings { OutputAspect = "9:16", OutputResolution = 1080 });

		Assert.AreEqual((1080, 1918), (output.Width, output.Height));
		Assert.AreEqual("crop=1216:2160,scale=1080:1918:flags=lanczos:threads=0,format=yuv420p10le,", FfmpegPipeline.ResizeFilter(Osmo, output));
	}

	[TestMethod]
	public void Aspect_OfASquareSource_CutsTheSidesOfAVerticalOne()
	{
		VideoInfo square = Osmo with { Width = 4000, Height = 4000 };
		VideoInfo output = OutputVideo.For(square, new OverlaySettings { OutputAspect = "9:16" });

		Assert.AreEqual((2250, 4000), (output.Width, output.Height));
		Assert.AreEqual((4000, 2250), OutputVideo.For(square, new OverlaySettings { OutputAspect = "16:9" }) is { } wide ? (wide.Width, wide.Height) : default);
	}

	[TestMethod]
	public void Aspect_OnBars_KeepsTheWholePictureInABiggerFrame()
	{
		VideoInfo output = OutputVideo.For(Osmo, new OverlaySettings { OutputAspect = "9:16", OutputPadToAspect = true });

		Assert.AreEqual((3840, 6826), (output.Width, output.Height));
		Assert.AreEqual(Osmo.BitRate, output.BitRate, "bars cost nothing - the bitrate is the picture's");
		Assert.AreEqual("format=yuv420p10le,pad=3840:6826:(ow-iw)/2:(oh-ih)/2:black,", FfmpegPipeline.ResizeFilter(Osmo, output, true));
	}

	[TestMethod]
	public void Aspect_OnBars_WithAResolution_ScalesThePictureToFitTheFrame()
	{
		VideoInfo square = Osmo with { Width = 4000, Height = 4000 };
		VideoInfo output = OutputVideo.For(square, new OverlaySettings { OutputAspect = "9:16", OutputPadToAspect = true, OutputResolution = 1080 });

		Assert.AreEqual((1080, 1920), (output.Width, output.Height));
		Assert.AreEqual((long)Math.Round(73_000_000 * 1080.0 * 1080 / (4000.0 * 4000)), output.BitRate);
		Assert.AreEqual("scale=1080:1080:flags=lanczos:threads=0,format=yuv420p10le,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:black,",
			FfmpegPipeline.ResizeFilter(square, output, true));
	}

	[TestMethod]
	public void Aspect_OfTheSourcesOwnShape_ChangesNothing()
	{
		VideoInfo actionCam = Osmo with { Width = 2704, Height = 1520 };

		Assert.AreSame(actionCam, OutputVideo.For(actionCam, new OverlaySettings { OutputAspect = "16:9" }));
		Assert.AreSame(actionCam, OutputVideo.For(actionCam, new OverlaySettings { OutputAspect = "16:9", OutputPadToAspect = true }));
		Assert.AreSame(Osmo, OutputVideo.For(Osmo, new OverlaySettings { OutputPadToAspect = true }), "bars without a shape are nothing");

		VideoInfo scaled = OutputVideo.For(actionCam, new OverlaySettings { OutputAspect = "16:9", OutputResolution = 720 });
		Assert.AreEqual((1280, 720), (scaled.Width, scaled.Height));
		StringAssert.StartsWith(FfmpegPipeline.ResizeFilter(actionCam, scaled), "scale=1280:720", "the rounding of a smaller size isn't a new shape");
	}

	[TestMethod]
	public void Aspect_OutsideTheOfferedOnes_IsIgnored()
	{
		Assert.AreSame(Osmo, OutputVideo.For(Osmo, new OverlaySettings { OutputAspect = "7:3" }));
		Assert.AreSame(Osmo, OutputVideo.For(Osmo, new OverlaySettings { OutputAspect = "-vf nope" }));
	}

	[TestMethod]
	[DataRow("9:16", "9:16", false)]
	[DataRow("1:1@bars", "1:1", true)]
	[DataRow("4:5@BARS", "4:5", true)]
	public void TryParseOption_TakesAShapeWithOptionalBars(string text, string aspect, bool bars)
	{
		Assert.IsTrue(OutputVideo.TryParseOption(text, out string parsed, out bool parsedBars));
		Assert.AreEqual((aspect, bars), (parsed, parsedBars));
	}

	[TestMethod]
	[DataRow("")]
	[DataRow("7:3")]
	[DataRow("@bars")]
	[DataRow("9:16@cut")]
	public void TryParseOption_RefusesAnythingElse(string text)
	{
		Assert.IsFalse(OutputVideo.TryParseOption(text, out _, out _));
	}

	[TestMethod]
	public void FilterGraph_CarriesTheBarsToTheResize()
	{
		VideoInfo output = OutputVideo.For(Osmo, new OverlaySettings { OutputAspect = "1:1", OutputPadToAspect = true });
		string graph = FfmpegPipeline.FilterGraph("[0:v]", 1, Osmo, output, false, false, true);

		StringAssert.StartsWith(graph, "[0:v]format=yuv420p10le,pad=3840:3840:(ow-iw)/2:(oh-ih)/2:black,setparams=");
		StringAssert.StartsWith(FfmpegPipeline.FilterGraph("[0:v]", 1, Osmo, OutputVideo.For(Osmo, new OverlaySettings { OutputAspect = "1:1" }), false, false),
			"[0:v]crop=2160:2160,format=yuv420p10le,setparams=");
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
