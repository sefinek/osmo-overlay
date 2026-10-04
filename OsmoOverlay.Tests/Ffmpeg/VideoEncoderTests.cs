using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Ffmpeg;

[TestClass]
public sealed class VideoEncoderTests
{
	private static readonly VideoInfo Osmo = new("hevc", "Main 10", 3840, 2160, "60000/1001", "yuv420p10le", "bt709", "bt709", "bt709", "tv",
		73_000_000, 156, 60, null, true);

	private static readonly RenderEncodeSettings Encode = new("p7", true, 1, false);

	private static List<string> EncoderArgs(string encoder, VideoInfo video, RenderEncodeSettings? encode = null)
	{
		List<string> args = [];
		FfmpegPipeline.AddVideoEncoderArgs(args, video, encoder, encode ?? Encode, 60000, 1001);
		return args;
	}

	[TestMethod]
	public void GpuEncoders_AreEveryVendors()
	{
		foreach (string encoder in new[] { "hevc_nvenc", "h264_nvenc", "hevc_amf", "h264_amf", "hevc_qsv", "h264_qsv" })
			Assert.IsTrue(FfmpegPipeline.IsGpuEncoder(encoder), encoder);
		Assert.IsFalse(FfmpegPipeline.IsGpuEncoder("libx265"));
		Assert.IsFalse(FfmpegPipeline.IsGpuEncoder("libx264"));
	}

	[TestMethod]
	public void Encodes_ReadsTheCodecOffAnyEncodersName()
	{
		VideoInfo h264 = OutputVideo.For(Osmo, new OverlaySettings { OutputCodec = "h264" });

		Assert.IsTrue(FfmpegPipeline.Encodes("h264_amf", h264));
		Assert.IsTrue(FfmpegPipeline.Encodes("h264_qsv", h264));
		Assert.IsFalse(FfmpegPipeline.Encodes("hevc_amf", h264));
		Assert.IsTrue(FfmpegPipeline.Encodes("hevc_amf", Osmo));
		Assert.IsFalse(FfmpegPipeline.Encodes("h264_qsv", Osmo));
	}

	[TestMethod]
	public void PresetName_MapsSettingsScaleOntoEachEncoders()
	{
		Assert.AreEqual("p4", FfmpegPipeline.PresetName("hevc_nvenc", "p4"));
		Assert.AreEqual("speed", FfmpegPipeline.PresetName("hevc_amf", "p1"));
		Assert.AreEqual("balanced", FfmpegPipeline.PresetName("hevc_amf", "p4"));
		Assert.AreEqual("quality", FfmpegPipeline.PresetName("hevc_amf", "p6"));
		Assert.AreEqual("quality", FfmpegPipeline.PresetName("hevc_amf", "p7"));
		Assert.AreEqual("veryfast", FfmpegPipeline.PresetName("hevc_qsv", "p1"));
		Assert.AreEqual("medium", FfmpegPipeline.PresetName("hevc_qsv", "p4"));
		Assert.AreEqual("veryslow", FfmpegPipeline.PresetName("hevc_qsv", "p7"));
		Assert.AreEqual("p7", FfmpegPipeline.PresetName("hevc_nvenc", "bogus"), "an unknown value is the default");
		Assert.IsNull(FfmpegPipeline.PresetName("libx265", "p4"));
	}

	[TestMethod]
	public void Amf_IsConstantBitrate_AtTheSourcesLevelAndTier()
	{
		// As run by a real render on an AMD Radeon 610M - its output matched the source's profile, level, tier, GOP and frame count.
		CollectionAssert.AreEqual(new[]
			{
				"-c:v", "hevc_amf", "-quality", "quality", "-rc", "cbr", "-b:v", "73000000", "-bufsize", "73000000", "-bf", "0", "-g", "60",
				"-profile:v", "main10", "-pix_fmt", "p010le", "-level", "5.2", "-tier", "high"
			},
			EncoderArgs("hevc_amf", Osmo));
	}

	[TestMethod]
	public void Amf_H264_TakesItsLevelAsWritten()
	{
		VideoInfo h264 = Osmo with { CodecName = "h264", PixFmt = "yuv420p", Level = 51, HighTier = null };
		List<string> args = EncoderArgs("h264_amf", h264);

		Assert.AreEqual("5.1", args[args.IndexOf("-level") + 1]);
		Assert.AreEqual("nv12", args[args.IndexOf("-pix_fmt") + 1]);
		Assert.AreEqual("high", args[args.IndexOf("-profile:v") + 1]);
		Assert.IsFalse(args.Contains("-tier"));
	}

	[TestMethod]
	public void Qsv_IsConstantBitrate_WithItsOwnPreset()
	{
		List<string> args = EncoderArgs("hevc_qsv", Osmo, Encode with { NvencPreset = "p4" });

		Assert.AreEqual("medium", args[args.IndexOf("-preset") + 1]);
		Assert.AreEqual(args[args.IndexOf("-b:v") + 1], args[args.IndexOf("-maxrate") + 1], "Quick Sync's constant bitrate");
		Assert.AreEqual("p010le", args[args.IndexOf("-pix_fmt") + 1]);
		Assert.IsFalse(args.Contains("-level"));
	}

	[TestMethod]
	public void Nvenc_KeepsItsArgs()
	{
		List<string> args = EncoderArgs("hevc_nvenc", Osmo);

		Assert.AreEqual("p7", args[args.IndexOf("-preset") + 1]);
		Assert.AreEqual("yuv420p10le", args[args.IndexOf("-pix_fmt") + 1]);
		Assert.AreEqual("156", args[args.IndexOf("-level") + 1]);
	}

	[TestMethod]
	public void AmfAndQsv_RunOnTheirVendorsCard_ByVendorWithoutTheAdapterList()
	{
		CollectionAssert.AreEqual(new[] { "-init_hw_device", "d3d11va=enc:,vendor_id=0x1002" }, FfmpegPipeline.EncoderDeviceArgs("hevc_amf", []));
		CollectionAssert.AreEqual(new[] { "-init_hw_device", "d3d11va=enc:,vendor_id=0x8086" }, FfmpegPipeline.EncoderDeviceArgs("h264_qsv", []));
		CollectionAssert.AreEqual(new[] { "-hwaccel", "d3d11va", "-hwaccel_device", "enc" }, FfmpegPipeline.HwDecodeArgs("hevc_amf"));

		Assert.AreEqual(0, FfmpegPipeline.EncoderDeviceArgs("hevc_nvenc").Length);
		Assert.AreEqual(0, FfmpegPipeline.EncoderDeviceArgs("libx265").Length);
		CollectionAssert.AreEqual(new[] { "-hwaccel", "auto" }, FfmpegPipeline.HwDecodeArgs("hevc_nvenc"));
		CollectionAssert.AreEqual(new[] { "-hwaccel", "auto" }, FfmpegPipeline.HwDecodeArgs("libx265"));
	}

	[TestMethod]
	public void Amf_OnALaptopWithTwoAmdCards_RunsOnTheDedicatedOne()
	{
		// A Ryzen's integrated Radeon, listed first as it drives the screen, and a Radeon RX next to it.
		GpuAdapter[] adapters = [new(0, 0x1002, 0x100, 512L << 20), new(1, 0x1002, 0x200, 8L << 30), new(2, 0x1414, 0x300, 0)];

		CollectionAssert.AreEqual(new[] { "-init_hw_device", "d3d11va=enc:1" }, FfmpegPipeline.EncoderDeviceArgs("hevc_amf", adapters));
		Assert.AreEqual(0x200, GpuAdapters.Pick(adapters, 0x1002)!.Luid);
		Assert.IsNull(GpuAdapters.Pick(adapters, 0x8086));
	}
}
