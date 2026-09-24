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

	private static (double, double)[] Seconds(IEnumerable<PreviewPlayer.PlaybackStretch> stretches)
	{
		return [.. stretches.Select(s => (s.Start.TotalSeconds, s.End.TotalSeconds))];
	}

	[TestMethod]
	public void NoCuts_PlaysToTheEnd()
	{
		PreviewPlayer.PlaybackPlan plan = PreviewPlayer.PlanPlayback(null, TimeSpan.FromSeconds(3), Duration, false, null);

		CollectionAssert.AreEqual(new[] { (3.0, 10.0) }, Seconds(plan.First));
		Assert.AreEqual(0, plan.Repeat.Count);
	}

	[TestMethod]
	public void Cuts_AreSkipped()
	{
		PreviewPlayer.PlaybackPlan plan = PreviewPlayer.PlanPlayback(Cut, TimeSpan.FromSeconds(3), Duration, false, null);

		CollectionAssert.AreEqual(new[] { (3.0, 4.0), (6.0, 8.0) }, Seconds(plan.First));
	}

	[TestMethod]
	public void Loop_FinishesFromThePositionThenRepeatsTheRange()
	{
		PreviewPlayer.PlaybackPlan plan = PreviewPlayer.PlanPlayback(Cut, TimeSpan.FromSeconds(3), Duration, true, new TimeRange(2.5, 7));

		CollectionAssert.AreEqual(new[] { (3.0, 4.0), (6.0, 7.0) }, Seconds(plan.First));
		CollectionAssert.AreEqual(new[] { (2.5, 4.0), (6.0, 7.0) }, Seconds(plan.Repeat));
		CollectionAssert.AreEqual(new[] { (3.0, 4.0), (6.0, 7.0), (2.5, 4.0), (6.0, 7.0), (2.5, 4.0) }, Seconds(plan.Stretches().Take(5)));
	}

	[TestMethod]
	public void Loop_FromOutsideTheRange_StartsAtItsStart()
	{
		PreviewPlayer.PlaybackPlan plan = PreviewPlayer.PlanPlayback(null, TimeSpan.FromSeconds(9), Duration, true, new TimeRange(1, 2));

		CollectionAssert.AreEqual(new[] { (1.0, 2.0) }, Seconds(plan.First));
	}

	[TestMethod]
	public void Loop_WithoutRange_RepeatsTheWholeRecording()
	{
		PreviewPlayer.PlaybackPlan plan = PreviewPlayer.PlanPlayback(null, TimeSpan.FromSeconds(4), Duration, true, null);

		CollectionAssert.AreEqual(new[] { (0.0, 10.0) }, Seconds(plan.Repeat));
	}

	[TestMethod]
	public void FrameStep_ShowsAtMostAboutSixtyFramesASecond()
	{
		const double osmoFps = 60000 / 1001.0;
		Assert.AreEqual(1, PreviewPlayer.FrameStep(0.25, osmoFps));
		Assert.AreEqual(1, PreviewPlayer.FrameStep(1, osmoFps));
		Assert.AreEqual(2, PreviewPlayer.FrameStep(2, osmoFps));
		Assert.AreEqual(4, PreviewPlayer.FrameStep(4, osmoFps));
		Assert.AreEqual(1, PreviewPlayer.FrameStep(2, 30), "30 fps at 2x is still 60 shown frames a second");
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
	public void Audio_IsWhatWasPushedMinusQueuedAndLatency()
	{
		var audio = new FakeAudio { QueuedSeconds = 0.25, DeviceLatencySeconds = 0.01 };
		var clock = new PlaybackClock(audio, 48000, 1, () => 0);

		clock.Start(0);
		clock.AddPushed(48000 * 2);

		Assert.IsTrue(audio.Started);
		Assert.AreEqual(1.74, clock.Now, 1e-9);
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
