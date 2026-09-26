using OsmoOverlay.Core;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Tests.Preview;

[TestClass]
public sealed class PlaybackPlanTests
{
	private const double Fps = 50;
	private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);

	// Kept: 2-4 s and 6-8 s of 10 s.
	private static readonly OutputTimeline Cut =
		new(new RenderPlan([new RenderPiece(100, 100), new RenderPiece(300, 100)], true), Fps);

	private static (double, double)[] Seconds(IEnumerable<PlaybackStretch> stretches)
	{
		return [.. stretches.Select(s => (s.Start.TotalSeconds, s.End.TotalSeconds))];
	}

	[TestMethod]
	public void NoCuts_PlaysToTheEnd()
	{
		PlaybackPlan plan = PlaybackPlan.For(null, TimeSpan.FromSeconds(3), Duration, false, null);

		CollectionAssert.AreEqual(new[] { (3.0, 10.0) }, Seconds(plan.First));
		Assert.AreEqual(0, plan.Repeat.Count);
	}

	[TestMethod]
	public void Cuts_AreSkipped()
	{
		PlaybackPlan plan = PlaybackPlan.For(Cut, TimeSpan.FromSeconds(3), Duration, false, null);

		CollectionAssert.AreEqual(new[] { (3.0, 4.0), (6.0, 8.0) }, Seconds(plan.First));
	}

	[TestMethod]
	public void Loop_FinishesFromThePositionThenRepeatsTheRange()
	{
		PlaybackPlan plan = PlaybackPlan.For(Cut, TimeSpan.FromSeconds(3), Duration, true, new TimeRange(2.5, 7));

		CollectionAssert.AreEqual(new[] { (3.0, 4.0), (6.0, 7.0) }, Seconds(plan.First));
		CollectionAssert.AreEqual(new[] { (2.5, 4.0), (6.0, 7.0) }, Seconds(plan.Repeat));
		CollectionAssert.AreEqual(new[] { (3.0, 4.0), (6.0, 7.0), (2.5, 4.0), (6.0, 7.0), (2.5, 4.0) }, Seconds(plan.Stretches().Take(5)));
	}

	[TestMethod]
	public void Loop_FromOutsideTheRange_StartsAtItsStart()
	{
		PlaybackPlan plan = PlaybackPlan.For(null, TimeSpan.FromSeconds(9), Duration, true, new TimeRange(1, 2));

		CollectionAssert.AreEqual(new[] { (1.0, 2.0) }, Seconds(plan.First));
	}

	[TestMethod]
	public void Loop_WithoutRange_RepeatsTheWholeRecording()
	{
		PlaybackPlan plan = PlaybackPlan.For(null, TimeSpan.FromSeconds(4), Duration, true, null);

		CollectionAssert.AreEqual(new[] { (0.0, 10.0) }, Seconds(plan.Repeat));
	}

	[TestMethod]
	public void StretchesFrom_StartsPartWayIntoTheStretchAtThatPlayTime()
	{
		PlaybackPlan plan = PlaybackPlan.For(Cut, TimeSpan.FromSeconds(3), Duration, false, null);

		// Play time 0-1 is 3-4 s, 1-3 is 6-8 s.
		CollectionAssert.AreEqual(new[] { (3.5, 4.0), (6.0, 8.0) }, Seconds(plan.StretchesFrom(0.5)));
		CollectionAssert.AreEqual(new[] { (7.0, 8.0) }, Seconds(plan.StretchesFrom(2)));
		Assert.AreEqual(0, plan.StretchesFrom(3).Count(), "past the end of a plan that doesn't loop");
	}

	[TestMethod]
	public void StretchesFrom_WhileLooping_GoesOnIntoTheRepeats()
	{
		PlaybackPlan plan = PlaybackPlan.For(null, TimeSpan.FromSeconds(1.5), Duration, true, new TimeRange(1, 2));

		// Play time 0-0.5 is the first pass (1.5-2 s), then 1 s per repeat.
		CollectionAssert.AreEqual(new[] { (1.25, 2.0), (1.0, 2.0) }, Seconds(plan.StretchesFrom(0.75).Take(2)));
	}

	[TestMethod]
	public void FrameStep_ShowsAtMostAboutSixtyFramesASecond()
	{
		const double osmoFps = 60000 / 1001.0;
		Assert.AreEqual(1, PlaybackSession.FrameStep(0.25, osmoFps));
		Assert.AreEqual(1, PlaybackSession.FrameStep(1, osmoFps));
		Assert.AreEqual(2, PlaybackSession.FrameStep(2, osmoFps));
		Assert.AreEqual(4, PlaybackSession.FrameStep(4, osmoFps));
		Assert.AreEqual(1, PlaybackSession.FrameStep(2, 30), "30 fps at 2x is still 60 shown frames a second");
	}
}

[TestClass]
public sealed class PlaybackClockTests
{
	private sealed class FakeAudio : IAudioClockSource
	{
		public double QueuedSeconds { get; set; }
		public double DeviceLatencySeconds { get; set; }
		public bool Started { get; private set; }

		public void Start()
		{
			Started = true;
		}
	}

	[TestMethod]
	public void Stopwatch_RunsAtTheRate()
	{
		double wall = 100;
		var clock = new PlaybackClock(null, 48000, 2, () => wall);

		clock.Start(5);
		wall += 1.5;

		Assert.IsFalse(clock.FollowsAudio);
		Assert.AreEqual(8, clock.Now, 1e-9);
	}

	[TestMethod]
	public void Rebase_RestartsTheStopwatchAtAPlayTime()
	{
		double wall = 0;
		var clock = new PlaybackClock(null, 48000, 1, () => wall);
		clock.Start(0);
		wall = 3;

		clock.Rebase(10);
		wall = 3.5;

		Assert.AreEqual(10.5, clock.Now, 1e-9);
	}

	[TestMethod]
	public void Stopwatch_SetRate_ChangesPaceFromWhereItIs()
	{
		double wall = 0;
		var clock = new PlaybackClock(null, 48000, 1, () => wall);
		clock.Start(0);
		wall = 2;

		clock.SetRate(4);
		wall = 2.5;

		Assert.AreEqual(4, clock.Now, 1e-9);
	}

	[TestMethod]
	public void Audio_IsWhatWasPushedMinusQueuedAndLatency_PlusHalfABuffer()
	{
		var audio = new FakeAudio { QueuedSeconds = 0.25, DeviceLatencySeconds = 0.01 };
		var clock = new PlaybackClock(audio, 48000, 1, () => 0);

		clock.Start(0);
		clock.AddPushed(48000 * 2);

		Assert.IsTrue(audio.Started);
		// The device's position lags what's heard by up to one buffer - half of one on average.
		Assert.AreEqual(1.745, clock.Now, 1e-9);
	}

	[TestMethod]
	public void Audio_AtTwiceTheSpeed_CountsDoubleOnThePlayTimeline()
	{
		var audio = new FakeAudio();
		var clock = new PlaybackClock(audio, 48000, 2, () => 0);

		clock.AddPushed(48000);

		Assert.AreEqual(2, clock.Now, 1e-9);
	}

	[TestMethod]
	public void Audio_RunOut_HandsOverToTheStopwatch()
	{
		double wall = 0;
		var audio = new FakeAudio();
		var clock = new PlaybackClock(audio, 48000, 1, () => wall);
		clock.AddPushed(48000 * 3);

		clock.AudioFinished();
		Assert.AreEqual(3, clock.Now, 1e-9);
		wall = 2;

		Assert.IsFalse(clock.FollowsAudio);
		Assert.AreEqual(5, clock.Now, 1e-9);
	}

	[TestMethod]
	public void Audio_TakenABufferAtATime_StillTicksEvenly()
	{
		const double buffer = 0.01;
		const double display = 1 / 60.0;
		double wall = 0;
		var audio = new FakeAudio { DeviceLatencySeconds = buffer };
		var clock = new PlaybackClock(audio, 48000, 1, () => wall);
		clock.AddPushed(48000 * 10);

		double? previous = null;
		for (var i = 0; i < 180; i++)
		{
			wall = i * display;
			// The device took whole buffers up to now: its position steps every 10 ms, the heard sound doesn't.
			var taken = Math.Floor(wall / buffer) * buffer;
			audio.QueuedSeconds = 10 - buffer - taken;
			var now = clock.Now;

			if (i >= 60)
			{
				// Read at 60 Hz, 10 ms steps are only ever caught at three phases, so "half a buffer behind" is off by a
				// couple of ms here - a constant, far below what A/V sync notices. What matters is the even pace.
				Assert.AreEqual(wall, now, 0.003, $"reading {i}");
				Assert.AreEqual(display, now - previous!.Value, 0.0005, $"step {i}");
			}

			previous = now;
		}
	}

	[TestMethod]
	public void RestartAudio_CarriesOnFromThePlayTimeAtTheNewTempo()
	{
		var audio = new FakeAudio();
		var clock = new PlaybackClock(audio, 48000, 1, () => 0);
		clock.AddPushed(48000 * 5);

		clock.RestartAudio(4, 2);
		clock.AddPushed(48000);

		Assert.AreEqual(6, clock.AudioPosition!.Value, 1e-9);
	}

	[TestMethod]
	public void AudioPosition_IsTheSoundsPosition_WithoutHandingOver()
	{
		var audio = new FakeAudio { QueuedSeconds = 0.5 };
		var clock = new PlaybackClock(audio, 48000, 1, () => 0);
		clock.AddPushed(48000 * 3);

		Assert.AreEqual(2.5, clock.AudioPosition!.Value, 1e-9);

		clock.AudioFinished();
		Assert.IsNull(clock.AudioPosition);
		Assert.IsTrue(clock.FollowsAudio, "only Now hands the clock over to the stopwatch");
	}

	[TestMethod]
	public void AudioPosition_IsNullWithoutSound()
	{
		Assert.IsNull(new PlaybackClock(null, 48000, 1, () => 0).AudioPosition);
	}
}

[TestClass]
public sealed class CatchUpTests
{
	private const double Fps = 60;

	[TestMethod]
	public void OnTimeOrSilent_DecodesOn()
	{
		Assert.AreEqual(0, PlaybackSession.CatchUpFrames(null, 1, Fps, 1, 1), "no sound to fall behind");
		Assert.AreEqual(0, PlaybackSession.CatchUpFrames(1, 2, Fps, 1, 1), "ahead of the sound");
		Assert.AreEqual(0, PlaybackSession.CatchUpFrames(2, 2, Fps, 1, 1), "due right now");
	}

	[TestMethod]
	public void SlightlyBehind_SkipsAStep_RatherThanConvertAFrameAlreadyLate()
	{
		Assert.AreEqual(1, PlaybackSession.CatchUpFrames(2.04, 2, Fps, 1, 1));
	}

	[TestMethod]
	public void Behind_SpreadsTheSkipOverReads_InProportionToTheLateness()
	{
		// 0.5 s behind is five times the threshold: five more steps this read, of the ~39 frames it takes in all.
		Assert.AreEqual(5, PlaybackSession.CatchUpFrames(2.5, 2, Fps, 1, 1));
		Assert.AreEqual(20, PlaybackSession.CatchUpFrames(2.5, 2, Fps, 1, 4));
	}

	[TestMethod]
	public void Behind_NeverSkipsPastTheLead()
	{
		// 0.15 s behind with a big step: all it takes is 0.15 s to the sound plus the 0.15 s lead - 18 frames.
		var frames = PlaybackSession.CatchUpFrames(2.15, 2, Fps, 1, 30);
		Assert.IsTrue(frames is 18 or 19, $"{frames}");
	}

	[TestMethod]
	public void SpedUp_LatenessIsRealTime()
	{
		Assert.AreEqual(4, PlaybackSession.CatchUpFrames(2.16, 2, Fps, 4, 4), "0.16 s of play time is 40 ms at 4x: one step");
		Assert.AreEqual(8, PlaybackSession.CatchUpFrames(2.6, 2, Fps, 4, 4), "150 ms real time behind: two steps");
	}

	[TestMethod]
	public void FarBehind_SkipsAtMostTwoSecondsPerRead()
	{
		Assert.AreEqual(120, PlaybackSession.CatchUpFrames(30, 2, Fps, 1, 1));
	}
}

[TestClass]
public sealed class AudioDisplayTests
{
	[TestMethod]
	public void FilterFor_ChainsStagesBelowHalfSpeed()
	{
		Assert.AreEqual("atempo=2", AudioTempo.FilterFor(2));
		Assert.AreEqual("atempo=0.5", AudioTempo.FilterFor(0.5));
		Assert.AreEqual("atempo=0.5,atempo=0.5", AudioTempo.FilterFor(0.25));
		Assert.AreEqual("atempo=0.5,atempo=0.6", AudioTempo.FilterFor(0.3));
	}

	[TestMethod]
	public void DisplayLevel_TheLoudestPeakFillsTheTrack()
	{
		Assert.AreEqual(1, AudioWaveform.DisplayLevel(0.03f, 0.03f), 1e-9);
	}

	[TestMethod]
	public void DisplayLevel_IsDecibelsBelowTheLoudest()
	{
		// A loud recording (peak -6 dBFS): 24 dB below it is halfway down the 48 dB range.
		Assert.AreEqual(0.5, AudioWaveform.DisplayLevel(0.5f * (float)Math.Pow(10, -24.0 / 20), 0.5f), 1e-6);
		Assert.AreEqual(0, AudioWaveform.DisplayLevel(0.5f * (float)Math.Pow(10, -60.0 / 20), 0.5f), 1e-9);
		Assert.AreEqual(0, AudioWaveform.DisplayLevel(0, 0.5f));
	}

	[TestMethod]
	public void DisplayLevel_QuietRecording_RangeStopsAtTheFloor()
	{
		// Peak -30 dBFS: the range ends at -72 dBFS instead of -78, so -51 dBFS is halfway.
		var loudest = (float)Math.Pow(10, -30.0 / 20);
		Assert.AreEqual(0.5, AudioWaveform.DisplayLevel((float)Math.Pow(10, -51.0 / 20), loudest), 1e-6);
	}

	[TestMethod]
	public void DisplayLevel_Silence_DoesNotBlowUpTheNoiseFloor()
	{
		// Everything at -80 dBFS: measured against the -60 dBFS reference, not against itself.
		Assert.AreEqual(0, AudioWaveform.DisplayLevel(0.0001f, 0.0001f), 1e-9);
	}
}
