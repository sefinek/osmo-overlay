using OsmoOverlay.Core;

namespace OsmoOverlay.Tests.Recordings;

[TestClass]
public sealed class RecommendedRecordingTests
{
	private static readonly RecommendedRecording Dji = new(3840, 2160, 50, 59.94, 70_000_000, 256_000);
	private static readonly RecommendedRecording GoPro = new(3840, 2160, 50, 59.94, 60_000_000, 128_000, true, false);

	[TestMethod]
	public void Resolution_FourKIsGood_FullHdIsAWarning_LessIsBad()
	{
		Assert.AreEqual(CheckLevel.Good, Dji.Resolution(3840, 2160));
		Assert.AreEqual(CheckLevel.Good, Dji.Resolution(5312, 2988));
		Assert.AreEqual(CheckLevel.Warning, Dji.Resolution(2704, 1520));
		Assert.AreEqual(CheckLevel.Warning, Dji.Resolution(1920, 1080));
		Assert.AreEqual(CheckLevel.Bad, Dji.Resolution(1280, 720));
	}

	[TestMethod]
	public void FrameRate_FiftyAndSixtyAreGood_AnythingSmoothIsAWarning_SlowIsBad()
	{
		Assert.AreEqual(CheckLevel.Good, Dji.FrameRate(50));
		Assert.AreEqual(CheckLevel.Good, Dji.FrameRate(59.94));
		Assert.AreEqual(CheckLevel.Good, Dji.FrameRate(60));
		Assert.AreEqual(CheckLevel.Warning, Dji.FrameRate(25));
		Assert.AreEqual(CheckLevel.Warning, Dji.FrameRate(119.88));
		Assert.AreEqual(CheckLevel.Bad, Dji.FrameRate(15));
	}

	[TestMethod]
	public void Codec_TenBitHevcIsGood_EightBitHevcAndH264AreWarnings()
	{
		Assert.AreEqual(CheckLevel.Good, Dji.Codec("hevc", "yuv420p10le"));
		Assert.AreEqual(CheckLevel.Warning, Dji.Codec("hevc", "yuvj420p"));
		Assert.AreEqual(CheckLevel.Warning, Dji.Codec("h264", "yuv420p"));
		Assert.AreEqual(CheckLevel.Bad, Dji.Codec("mpeg4", "yuv420p"));
	}

	[TestMethod]
	public void Codec_WhereTenBitIsntRecommendedEightBitHevcIsGood()
	{
		Assert.AreEqual(CheckLevel.Good, GoPro.Codec("hevc", "yuvj420p"));
		Assert.AreEqual(CheckLevel.Good, GoPro.Codec("hevc", "yuv420p10le"));
		Assert.AreEqual(CheckLevel.Warning, GoPro.Codec("h264", "yuv420p"));
	}

	[TestMethod]
	public void Color_LimitedRec709IsGood_FullRangeOnlyWhereTheCameraWritesIt_OtherTagsAreAWarning()
	{
		Assert.AreEqual(CheckLevel.Good, Dji.Color("bt709", "bt709", "bt709", "tv"));
		Assert.AreEqual(CheckLevel.Warning, Dji.Color("bt709", "bt709", "bt709", "pc"));
		Assert.AreEqual(CheckLevel.Good, GoPro.Color("bt709", "bt709", "bt709", "pc"));
		Assert.AreEqual(CheckLevel.Warning, GoPro.Color(null, null, null, null));
		Assert.AreEqual(CheckLevel.Warning, Dji.Color("bt2020", "arib-std-b67", "bt2020nc", "tv"));
	}

	[TestMethod]
	public void Bitrate_AtLeastTheRecommendedIsGood_SixtyPercentIsAWarning_LessIsBad()
	{
		Assert.AreEqual(CheckLevel.Good, Dji.VideoBitrate(75_000_000));
		Assert.AreEqual(CheckLevel.Warning, Dji.VideoBitrate(45_000_000));
		Assert.AreEqual(CheckLevel.Bad, Dji.VideoBitrate(30_000_000));
		Assert.AreEqual(CheckLevel.Good, GoPro.AudioBitrate(189_416));
		Assert.AreEqual(CheckLevel.Warning, Dji.AudioBitrate(160_000));
		Assert.AreEqual(CheckLevel.Bad, Dji.AudioBitrate(96_000));
	}
}
