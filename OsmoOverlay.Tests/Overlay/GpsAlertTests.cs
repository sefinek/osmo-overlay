using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using static OsmoOverlay.Core.Overlay.OverlayRenderer;

namespace OsmoOverlay.Tests.Overlay;

/// <summary>The GPS alert widget: on screen only while the GPS has something to say.</summary>
[TestClass]
public sealed class GpsAlertTests
{
	private const double Hz = 30;

	/// <summary>`seconds` at Hz with a fix everywhere, then shaped by `shape`.</summary>
	private static List<DerivedFrame> Frames(double seconds, Func<TelemetryFrame, TelemetryFrame> shape)
	{
		return
		[
			.. Enumerable.Range(0, (int)(seconds * Hz)).Select(i =>
			{
				double t = i / Hz;
				TelemetryFrame raw = shape(new TelemetryFrame(i, t, 50, 20, 200, null, 0, 1, 0));
				return new DerivedFrame(raw, 20, 0, 0, t, 0, 0, new SunPosition(120, 30), 0, 0, 1, 0, 0);
			})
		];
	}

	/// <summary>10 s: no fix from 4 s to 5.5 s, the readings at 6 s corrected.</summary>
	private static List<DerivedFrame> LossAndCorrection()
	{
		return Frames(10, f => f with
		{
			HasGpsFix = f.SampleTimeSeconds is < 4 or >= 5.5,
			GpsCorrected = f.SampleTimeSeconds is >= 6 and < 6.2 ? GpsCorrection.Position : GpsCorrection.None
		});
	}

	private static int DrawnPixels(double seconds, GpsAlertElement? element = null)
	{
		List<DerivedFrame> frames = LossAndCorrection();
		GpsAlertElement alert = element ?? new GpsAlertElement { X = 1920, Y = 400 };
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
	public void ItsOwnTimingAndAnimation_AreIgnored()
	{
		var timed = new GpsAlertElement
		{
			X = 1920, Y = 400, AppearAtSeconds = 9, DisappearAtSeconds = 9.5, AnimationType = OverlayAnimationType.SlideUp
		};

		Assert.AreEqual(DrawnPixels(4.5), DrawnPixels(4.5, timed), "drawn the same during the loss, before its own appear time");
		Assert.AreEqual(0, DrawnPixels(2, timed));
	}

	[TestMethod]
	public void Nothing_WhileTheGpsIsFine()
	{
		Assert.AreEqual(0, DrawnPixels(2));
		Assert.AreEqual(0, DrawnPixels(9.9));
	}

	[TestMethod]
	public void ShortDropout_SaysNothing()
	{
		List<DerivedFrame> frames = Frames(10, f => f with { HasGpsFix = f.SampleTimeSeconds is < 4 or >= 4 + GpsLossMinSeconds / 2 });

		Assert.AreEqual(0, FindGpsAlerts(frames).Events.Count);
	}

	[TestMethod]
	public void Back_CountsTheLossInRecordingTime_ThroughACut()
	{
		// 4 s of the recording cut out in the middle of a loss: 1 s of it in the output, 5 s in the recording.
		List<DerivedFrame> frames = Frames(10, f => f with
		{
			HasGpsFix = f.SampleTimeSeconds is < 4 or >= 5,
			SourceTimeSeconds = f.SampleTimeSeconds < 4.5 ? f.SampleTimeSeconds : f.SampleTimeSeconds + 4
		});

		List<GpsAlertEvent> events = FindGpsAlerts(frames).Events;

		Assert.AreEqual(2, events.Count);
		Assert.AreEqual(GpsNotice.Lost, events[0].Notice);
		Assert.AreEqual(4, events[0].Since, 1e-9);
		Assert.AreEqual(GpsNotice.Back, events[1].Notice);
		Assert.AreEqual(5, events[1].Start, 1e-9);
		Assert.AreEqual(5, events[1].Lasted, 1e-9);
	}

	[TestMethod]
	public void EachFile_SearchesForItsFirstFix_AndALossBeforeAStopIsNotBack()
	{
		// File 1: no fix until 2 s, lost from 4.5 s to its end at 6 s. File 2, after a stop: no fix until 8 s.
		List<DerivedFrame> frames = Frames(10, f => f with
		{
			HasGpsFix = f.SampleTimeSeconds is >= 2 and < 4.5 or >= 8,
			StartsAfterGap = f.FrameNumber == (int)(6 * Hz)
		});

		GpsNotice[] notices = [.. FindGpsAlerts(frames).Events.Select(e => e.Notice)];

		CollectionAssert.AreEqual(new[] { GpsNotice.Searching, GpsNotice.Found, GpsNotice.Lost, GpsNotice.Searching, GpsNotice.Found }, notices);
	}

	[TestMethod]
	public void CorrectionsCloseTogether_AreOneNotice()
	{
		List<DerivedFrame> frames = Frames(12, f => f with
		{
			GpsCorrected = f.SampleTimeSeconds switch
			{
				>= 3 and < 3.1 => GpsCorrection.Position,
				>= 4 and < 4.1 => GpsCorrection.Speed,
				>= 9 and < 9.1 => GpsCorrection.Position,
				_ => GpsCorrection.None
			}
		});

		List<GpsAlertEvent> events = FindGpsAlerts(frames).Events;

		Assert.AreEqual(2, events.Count);
		Assert.AreEqual(GpsCorrection.Position | GpsCorrection.Speed, events[0].What);
		Assert.AreEqual(3, events[0].Start, 1e-9);
		Assert.IsTrue(events[0].End > 7, "stays up from the last correction of the run");
		Assert.AreEqual(GpsCorrection.Position, events[1].What);
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
	public void OwnText_TakesThePlaceOfTheBuiltIn_LineByLine()
	{
		var texts = new GpsAlertTexts().With(GpsAlertMessage.Lost, new GpsAlertText("Sygnał zgubiony", null));

		(string[] titles, string[] details) = GpsAlertLines(texts);

		Assert.AreEqual("SYGNAŁ ZGUBIONY", titles[(int)GpsAlertMessage.Lost]);
		Assert.AreEqual(DefaultGpsAlertText(GpsAlertMessage.Lost).Detail!.ToUpperInvariant(), details[(int)GpsAlertMessage.Lost]);
		Assert.AreEqual(DefaultGpsAlertText(GpsAlertMessage.Back).Title!.ToUpperInvariant(), titles[(int)GpsAlertMessage.Back]);
	}

	[TestMethod]
	public void NoLineOfItsOwn_StoredAsNone()
	{
		GpsAlertTexts texts = new GpsAlertTexts().With(GpsAlertMessage.Found, new GpsAlertText("A", "B")).With(GpsAlertMessage.Found, new GpsAlertText());

		Assert.AreEqual(new GpsAlertTexts(), texts);
	}

	[TestMethod]
	public void Panel_FitsALongerOwnText()
	{
		var texts = new GpsAlertTexts().With(GpsAlertMessage.Searching, new GpsAlertText(new string('W', 40), null));

		Assert.IsTrue(OverlayElementBounds.GpsAlertBox(null, texts).Width > OverlayElementBounds.GpsAlertBox(null, null).Width);
	}

	[TestMethod]
	public void NeedsAFix_SomewhereInTheRecording()
	{
		Assert.IsTrue(OverlayDataRequirements.IsSupported(OverlayElementType.GpsAlert, new OverlayAvailability(true, false, false, false, false)));
		Assert.IsFalse(OverlayDataRequirements.IsSupported(OverlayElementType.GpsAlert, new OverlayAvailability(false, true, true, true, true)));
	}
}
