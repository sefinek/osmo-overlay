using System.Text.Json.Nodes;
using OsmoOverlay.Cameras.Dji;
using OsmoOverlay.Cameras.Insta360;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Cameras;

[TestClass]
public sealed class CameraFormatsTests
{
	private static JsonArray Streams(params string[] streams)
	{
		return JsonNode.Parse($"[{string.Join(',', streams)}]")!.AsArray();
	}

	private static string Video(int index, int width, int height, bool thumbnail = false)
	{
		var attached = thumbnail ? 1 : 0;
		return $"{{\"index\":{index},\"codec_type\":\"video\",\"width\":{width},\"height\":{height},\"disposition\":{{\"attached_pic\":{attached}}}}}";
	}

	[TestMethod]
	public void Dji_IsTheFileWithADjmdStream()
	{
		var dji = new DjiOsmoFormat();

		CameraRecording? recording = dji.Detect("x.mp4", Streams(Video(0, 3840, 2160), """{"index":3,"codec_type":"data","codec_tag_string":"djmd"}"""));
		Assert.IsNotNull(recording);
		Assert.AreEqual(3, recording.TelemetryStream);
		Assert.IsNull(recording.Lenses);

		Assert.IsNull(dji.Detect("x.mp4", Streams(Video(0, 3840, 2160), """{"index":1,"codec_type":"data","codec_tag_string":"tmcd"}""")));
	}

	[TestMethod]
	public void Insta360_LensLayouts()
	{
		Assert.AreEqual(new DualFisheye(FisheyeLayout.TwoStreams, 1920), Insta360Format.DetectLenses(Streams(Video(0, 1920, 1920), Video(1, 1920, 1920))));
		Assert.AreEqual(new DualFisheye(FisheyeLayout.SideBySide, 832), Insta360Format.DetectLenses(Streams(Video(0, 1664, 832))));
		Assert.AreEqual(new DualFisheye(FisheyeLayout.SideBySide, 832),
			Insta360Format.DetectLenses(Streams(Video(0, 1664, 832), Video(1, 320, 160, true))), "a thumbnail isn't a lens");
		Assert.IsNull(Insta360Format.DetectLenses(Streams(Video(0, 3840, 2160))), "a single-lens camera is flat");
	}

	[TestMethod]
	public void GMeterAxes_ComeFromTheFormatsGravity()
	{
		List<TelemetryFrame> frames = [new(0, 0, 50, 20, 200, null, 0.2, 0.3, 0.9)];

		DerivedFrame dji = TelemetryProcessor.Process(frames, new DjiOsmoFormat())[0];
		Assert.AreEqual(-0.3, dji.LateralAccelG, 1e-12, "DJI: AccelY negated - a right tilt swings it negative");
		Assert.AreEqual(0.2, dji.LongitudinalAccelG, 1e-12);

		DerivedFrame insta360 = TelemetryProcessor.Process(frames, new Insta360Format())[0];
		Assert.AreEqual(0.9, insta360.LateralAccelG, 1e-12, "Insta360: gravity's x is AccelZ");
		Assert.AreEqual(-0.3, insta360.LongitudinalAccelG, 1e-12, "forward is gravity's z (AccelY), negated");
	}

	[TestMethod]
	public void Insta360_TiltIsTheBodysAgainstItsFrontLens()
	{
		var lean = 20 * Math.PI / 180;
		// Gravity (AccelZ, -AccelX, AccelY): upright is AccelX = -1, leaning right tips gravity toward x.
		List<TelemetryFrame> upright = [new(0, 0, 0, 0, 0, null, -1, 0, 0, HasGpsFix: false)];
		List<TelemetryFrame> leaning = [new(0, 0, 0, 0, 0, null, -Math.Cos(lean), 0, Math.Sin(lean), HasGpsFix: false)];
		List<TelemetryFrame> noseUp = [new(0, 0, 0, 0, 0, null, -Math.Cos(lean), -Math.Sin(lean), 0, HasGpsFix: false)];

		DerivedFrame level = TelemetryProcessor.Process(upright, new Insta360Format())[0];
		Assert.AreEqual(0, level.RollDegrees, 1e-9);
		Assert.AreEqual(0, level.PitchDegrees, 1e-9);
		Assert.AreEqual(20, TelemetryProcessor.Process(leaning, new Insta360Format())[0].RollDegrees, 1e-9, "right lean is positive");
		Assert.AreEqual(20, TelemetryProcessor.Process(noseUp, new Insta360Format())[0].PitchDegrees, 1e-9, "nose up is positive");
	}

	[TestMethod]
	public void Register_KeepsOneOfEach_Find_ById()
	{
		CameraFormats.Register(new DjiOsmoFormat(), new Insta360Format());
		CameraFormats.Register(new DjiOsmoFormat());

		Assert.AreEqual(1, CameraFormats.All.Count(f => f.Id == "dji-osmo"));
		Assert.IsInstanceOfType<Insta360Format>(CameraFormats.Find("insta360"));
		Assert.IsNull(CameraFormats.Find("gopro"));
		Assert.IsNull(CameraFormats.Find(null));
	}
}
