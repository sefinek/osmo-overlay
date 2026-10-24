using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Tests.Ffmpeg;

[TestClass]
public sealed class AlphaOverlayTests
{
	private static readonly VideoInfo Source = new("hevc", "Main 10", 3840, 2160, "60000/1001", "yuv420p10le", null, null, null, null, 90_000_000);
	private static readonly VideoInfo Output = Source with { Width = 1920, Height = 1080 };
	private static readonly RenderEncodeSettings Encode = new("p7", false, 1, false);

	private static List<string> Args(AlphaCodec format, bool overwrite = true, RenderEncodeSettings? encode = null)
	{
		return AlphaOverlay.Arguments("out.mov", Source, Output, format, overwrite, encode ?? Encode, 1800);
	}

	private static string After(List<string> args, string option)
	{
		return args[args.IndexOf(option) + 1];
	}

	[TestMethod]
	public void TheFramesOnThePipeAreTheOutputsSizeAndTheSourcesRate()
	{
		List<string> args = Args(AlphaCodec.ProRes4444);

		Assert.AreEqual("1920x1080", After(args, "-s"));
		Assert.AreEqual("60000/1001", After(args, "-r"));
		Assert.AreEqual("bgra", After(args, "-pix_fmt"));
		Assert.AreEqual("pipe:0", After(args, "-i"));
	}

	[TestMethod]
	public void TheEncoderStopsAfterTheFramesTheRenderDraws()
	{
		Assert.AreEqual("1800", After(Args(AlphaCodec.ProRes4444), "-frames:v"));
	}

	[TestMethod]
	public void ProRes4444_KeepsAnAlphaChannelAt16Bits()
	{
		List<string> args = Args(AlphaCodec.ProRes4444);

		Assert.AreEqual("prores_ks", After(args, "-c:v"));
		Assert.AreEqual("4444", After(args, "-profile:v"));
		Assert.AreEqual("16", After(args, "-alpha_bits"));
		CollectionAssert.Contains(args, "yuva444p10le");
		StringAssert.Contains(After(args, "-vf"), "yuva444p10le");
	}

	[TestMethod]
	public void Vp9_KeepsAnAlphaChannelAndTurnsOffTheLookaheadThatDropsIt()
	{
		List<string> args = Args(AlphaCodec.Vp9);

		Assert.AreEqual("libvpx-vp9", After(args, "-c:v"));
		Assert.AreEqual("yuva420p", args[args.LastIndexOf("-pix_fmt") + 1], "the encoder's, not the pipe's");
		Assert.AreEqual("0", After(args, "-auto-alt-ref"));
	}

	[TestMethod]
	public void HasNoSoundNoMatterWhat()
	{
		CollectionAssert.Contains(Args(AlphaCodec.ProRes4444), "-an");
		CollectionAssert.Contains(Args(AlphaCodec.Vp9), "-an");
	}

	[TestMethod]
	public void TheColorsAreBt709Whatever()
	{
		List<string> args = Args(AlphaCodec.ProRes4444);

		Assert.AreEqual("bt709", After(args, "-colorspace"));
		StringAssert.Contains(After(args, "-vf"), "out_color_matrix=bt709");
	}

	[TestMethod]
	public void AnExistingFileIsOverwrittenOnlyWhenAsked()
	{
		CollectionAssert.Contains(Args(AlphaCodec.Vp9), "-y");
		CollectionAssert.Contains(Args(AlphaCodec.Vp9, false), "-n");
		CollectionAssert.DoesNotContain(Args(AlphaCodec.Vp9, false), "-y");
	}

	[TestMethod]
	public void FastStartIsAMovThing()
	{
		RenderEncodeSettings fast = Encode with { FfmpegFastStart = true };

		CollectionAssert.Contains(Args(AlphaCodec.ProRes4444, encode: fast), "+faststart");
		CollectionAssert.DoesNotContain(Args(AlphaCodec.Vp9, encode: fast), "+faststart");
	}

	[TestMethod]
	public void TheExportMarkerGoesInTheComment()
	{
		Assert.AreEqual("comment=made by test", After(Args(AlphaCodec.ProRes4444, encode: Encode with { Comment = "made by test" }), "-metadata"));
	}

	[TestMethod]
	public void TheOutputPathIsLast()
	{
		Assert.AreEqual("out.mov", Args(AlphaCodec.ProRes4444)[^1]);
	}

	[TestMethod]
	public void FormatsHaveTheirOwnExtensionsAndCommandLineNames()
	{
		Assert.AreEqual(".mov", AlphaOverlay.Extension(AlphaCodec.ProRes4444));
		Assert.AreEqual(".webm", AlphaOverlay.Extension(AlphaCodec.Vp9));
		Assert.AreEqual(AlphaCodec.ProRes4444, AlphaOverlay.Parse("ProRes"));
		Assert.AreEqual(AlphaCodec.Vp9, AlphaOverlay.Parse("WEBM"));
		Assert.IsNull(AlphaOverlay.Parse("gif"));
	}

	[TestMethod]
	public void TheEstimateGrowsWithSizeAndLengthAndProResIsTheBigger()
	{
		long hd = AlphaOverlay.EstimateBytes(AlphaCodec.ProRes4444, 1920, 1080, 1000);
		long uhd = AlphaOverlay.EstimateBytes(AlphaCodec.ProRes4444, 3840, 2160, 1000);

		Assert.AreEqual(hd * 4, uhd);
		Assert.IsTrue(AlphaOverlay.EstimateBytes(AlphaCodec.ProRes4444, 1920, 1080, 2000) > hd);
		Assert.IsTrue(hd > AlphaOverlay.EstimateBytes(AlphaCodec.Vp9, 1920, 1080, 1000));
	}

	[TestMethod]
	public void AlphaOutputPath_SwapsTheOverlaySuffixAndTakesTheFormatsExtension()
	{
		string path = RenderOptions.AlphaOutputPath(Path.Combine("out", "ride_overlay.mp4"), AlphaCodec.ProRes4444);

		Assert.AreEqual(Path.Combine("out", "ride_alpha.mov"), path);
		Assert.AreEqual(Path.Combine("out", "ride_alpha.webm"), RenderOptions.AlphaOutputPath(Path.Combine("out", "ride.mp4"), AlphaCodec.Vp9));
	}
}
