using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Overlay;

/// <summary>The GPS alert widget: on screen only while the GPS has something to say.</summary>
[TestClass]
public sealed class GpsAlertTests
{
	private const double Hz = 30;

	/// <summary>10 s at Hz: no fix from 4 s to 5 s, the readings at 6 s corrected.</summary>
	private static List<DerivedFrame> Frames()
	{
		return
		[
			.. Enumerable.Range(0, (int)(10 * Hz)).Select(i =>
			{
				double t = i / Hz;
				var raw = new TelemetryFrame(i, t, 50, 20, 200, null, 0, 1, 0, HasGpsFix: t is < 4 or >= 5,
					GpsCorrected: t is >= 6 and < 6.2 ? GpsCorrection.Position : GpsCorrection.None);
				return new DerivedFrame(raw, 20, 0, 0, t, 0, 0, new SunPosition(120, 30), 0, 0, 1, 0, 0);
			})
		];
	}

	private static int DrawnPixels(double seconds)
	{
		List<DerivedFrame> frames = Frames();
		var alert = new GpsAlertElement { X = 1920, Y = 400 };
		using var renderer = new OverlayRenderer(3840, 2160, 200, [alert], frames, 20, false);
		byte[] pixels = new byte[renderer.FrameBufferSize()];
		renderer.RenderInto(TelemetryProcessor.FindNearest(frames, seconds), pixels, premultiplied: true);
		int drawn = 0;
		for (int i = 3; i < pixels.Length; i += 4)
		{
			if (pixels[i] > 0) drawn++;
		}

		return drawn;
	}

	[TestMethod]
	public void Shown_WhileLost_AndWhenItsBack_AndAfterACorrection()
	{
		Assert.IsTrue(DrawnPixels(4.5) > 10_000, "the signal lost");
		Assert.IsTrue(DrawnPixels(5.8) > 10_000, "back, for a moment");
		Assert.IsTrue(DrawnPixels(6.5) > 10_000, "a reading corrected");
	}

	[TestMethod]
	public void Nothing_WhileTheGpsIsFine()
	{
		Assert.AreEqual(0, DrawnPixels(2));
		Assert.AreEqual(0, DrawnPixels(9.9));
	}

	[TestMethod]
	public void Repair_MarksWhatItCorrected()
	{
		List<TelemetryFrame> frames =
		[
			.. Enumerable.Range(0, 100).Select(i => new TelemetryFrame(i, i / 10.0, 50 + i * 1.0 / 111_320, 20, 200, null, 0, 1, 0, 10))
		];
		frames[50] = frames[50] with { GpsSpeedMs = 40 };

		List<DerivedFrame> derived = TelemetryProcessor.Process(frames, null);

		Assert.AreEqual(GpsCorrection.Speed, derived[50].Raw.GpsCorrected);
		Assert.IsTrue(derived.Where((_, i) => i != 50).All(f => f.Raw.GpsCorrected == GpsCorrection.None));
	}

	[TestMethod]
	public void NeedsAFix_SomewhereInTheRecording()
	{
		Assert.IsTrue(OverlayDataRequirements.IsSupported(OverlayElementType.GpsAlert, new OverlayAvailability(true, false, false, false, false)));
		Assert.IsFalse(OverlayDataRequirements.IsSupported(OverlayElementType.GpsAlert, new OverlayAvailability(false, true, true, true, true)));
	}
}
