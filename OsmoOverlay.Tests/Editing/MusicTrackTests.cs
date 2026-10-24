using OsmoOverlay.Core;

namespace OsmoOverlay.Tests.Editing;

[TestClass]
public sealed class MusicTrackTests
{
	private const double Output = 60;

	private static MusicClip Clip(double start = 0, double length = 20, double source = 30, double inPoint = 0, double db = 0, double fadeIn = 0,
		double fadeOut = 0, bool loop = false, double duck = 0, string path = "a.mp3")
	{
		return new MusicClip(path, start, inPoint, length, source, db, fadeIn, fadeOut, loop, duck);
	}

	[TestMethod]
	public void Normalize_CutsTheClipToTheFileAndTheOutput()
	{
		List<MusicClip> clips = MusicTrack.Normalize([Clip(50, 20, 30), Clip(0, 40, 30, 10)], Output);

		Assert.AreEqual(2, clips.Count);
		Assert.AreEqual(20, clips[0].LengthSeconds, 1e-9);
		Assert.AreEqual(10, clips[1].LengthSeconds, 1e-9);
	}

	[TestMethod]
	public void Normalize_LoopingClipIsNotCutToTheFile()
	{
		MusicClip clip = MusicTrack.Normalize([Clip(0, 45, 20, loop: true)], Output).Single();

		Assert.AreEqual(45, clip.LengthSeconds, 1e-9);
		Assert.IsTrue(clip.Repeats);
	}

	[TestMethod]
	public void Normalize_LoopWithoutKnownFileLength_IsDropped()
	{
		Assert.IsFalse(MusicTrack.Normalize([Clip(0, 10, 0, loop: true)], Output).Single().Loop);
	}

	[TestMethod]
	public void Normalize_DropsClipsOutsideTheOutputOrInvalid()
	{
		Assert.AreEqual(0, MusicTrack.Normalize([Clip(60), Clip(-5, 0.01), Clip(path: " "), Clip(double.NaN)], Output).Count);
	}

	[TestMethod]
	public void Normalize_ClampsLevelFadesAndDucking_AndSortsByStart()
	{
		List<MusicClip> clips = MusicTrack.Normalize([Clip(30, 10), Clip(5, 10, db: 99, fadeIn: 8, fadeOut: 8, duck: 5)], Output);

		Assert.AreEqual(5, clips[0].StartSeconds);
		Assert.AreEqual(MusicTrack.MaxDb, clips[0].Db);
		Assert.AreEqual(8, clips[0].FadeInSeconds);
		Assert.AreEqual(2, clips[0].FadeOutSeconds, 1e-9);
		Assert.AreEqual(0, clips[0].DuckDb);
	}

	[TestMethod]
	public void GainAt_FollowsTheFadesAndIsZeroOutsideTheClip()
	{
		MusicClip clip = Clip(0, 10, fadeIn: 2, fadeOut: 2);

		Assert.AreEqual(0, MusicTrack.GainAt(clip, 0), 1e-9);
		Assert.AreEqual(0.5, MusicTrack.GainAt(clip, 1), 1e-9);
		Assert.AreEqual(1, MusicTrack.GainAt(clip, 5), 1e-9);
		Assert.AreEqual(0.25, MusicTrack.GainAt(clip, 9.5), 1e-9);
		Assert.AreEqual(0, MusicTrack.GainAt(clip, 10));
		Assert.AreEqual(0, MusicTrack.GainAt(clip, -1));
	}

	[TestMethod]
	public void FilePosition_WrapsRoundWhenTheClipRepeats()
	{
		MusicClip clip = Clip(0, 50, 20, 5, loop: true);

		Assert.AreEqual(5, MusicTrack.FilePosition(clip, 0), 1e-9);
		Assert.AreEqual(10, MusicTrack.FilePosition(clip, 5), 1e-9);
		Assert.AreEqual(7, MusicTrack.FilePosition(clip, 17), 1e-9);
		Assert.AreEqual(8, MusicTrack.FilePosition(Clip(0, 10, 30, 5), 3), 1e-9);
	}

	[TestMethod]
	public void DuckDbAt_RampsInAndOutAndSumsTheClips()
	{
		List<MusicClip> clips = [Clip(10, 20, duck: -12), Clip(15, 10, duck: -6)];

		Assert.AreEqual(0, MusicTrack.DuckDbAt(clips, 5));
		Assert.AreEqual(-6, MusicTrack.DuckDbAt(clips, 10 + MusicTrack.DuckRampSeconds / 2), 1e-9);
		Assert.AreEqual(-12, MusicTrack.DuckDbAt(clips, 12));
		Assert.AreEqual(-18, MusicTrack.DuckDbAt(clips, 20), 1e-9);
		Assert.AreEqual(0, MusicTrack.DuckDbAt(clips, 31));
	}

	[TestMethod]
	public void WithCrossfades_OverlapBecomesTheFadeOfBothClips()
	{
		List<MusicClip> heard = MusicTrack.WithCrossfades([Clip(0, 20, 30, fadeOut: 1), Clip(15, 20, 30, fadeIn: 2)]);

		Assert.AreEqual(5, heard[0].FadeOutSeconds, 1e-9);
		Assert.AreEqual(5, heard[1].FadeInSeconds, 1e-9);
		Assert.AreEqual(0, heard[0].FadeInSeconds);
	}

	[TestMethod]
	public void WithCrossfades_ClipInsideAnotherOrApartIsLeftAlone()
	{
		List<MusicClip> inside = MusicTrack.WithCrossfades([Clip(0, 30, 40), Clip(5, 10, 30)]);
		List<MusicClip> apart = MusicTrack.WithCrossfades([Clip(0, 10, 30), Clip(20, 10, 30)]);

		Assert.IsTrue(inside.All(c => c.FadeInSeconds == 0 && c.FadeOutSeconds == 0));
		Assert.IsTrue(apart.All(c => c.FadeInSeconds == 0 && c.FadeOutSeconds == 0));
	}

	[TestMethod]
	public void FfmpegGraph_OriginalAndTwoClips_MixesAllThree()
	{
		string graph = MusicTrack.FfmpegGraph([Clip(2, 10, db: -6, fadeIn: 1, fadeOut: 2, duck: -10), Clip(20, 5, 30, 3)], 2, "0:a");

		StringAssert.Contains(graph, "[0:a]aformat=");
		StringAssert.Contains(graph, "volume=volume='pow(10,(");
		StringAssert.Contains(graph, "[2:a]aresample=48000");
		StringAssert.Contains(graph, "volume=-6dB");
		StringAssert.Contains(graph, "afade=t=in:st=0:d=1");
		StringAssert.Contains(graph, "afade=t=out:st=8:d=2");
		StringAssert.Contains(graph, "adelay=2000:all=1");
		StringAssert.Contains(graph, "[3:a]aresample=48000");
		StringAssert.Contains(graph, "atrim=start=3,");
		StringAssert.Contains(graph, "[orig][m0][m1]amix=inputs=3:duration=longest:normalize=0");
		Assert.IsTrue(graph.EndsWith("[mus]", StringComparison.Ordinal));
	}

	[TestMethod]
	public void FfmpegGraph_SingleClipWithoutRecordingSound_HasNoMix()
	{
		string graph = MusicTrack.FfmpegGraph([Clip(0, 10)], 1, null);

		Assert.IsFalse(graph.Contains("amix", StringComparison.Ordinal));
		Assert.IsFalse(graph.Contains("[orig]", StringComparison.Ordinal));
		StringAssert.Contains(graph, "[m0]anull");
	}

	[TestMethod]
	public void FfmpegGraph_RepeatingClip_LoopsTheStretchFromItsInPoint()
	{
		string graph = MusicTrack.FfmpegGraph([Clip(0, 50, 20, 5, loop: true)], 1, null);

		StringAssert.Contains(graph, "aloop=loop=-1:size=720000");
	}

	[TestMethod]
	public void FfmpegGraph_LabelOfTheCutGraphIsKeptAsIs()
	{
		StringAssert.Contains(MusicTrack.FfmpegGraph([Clip()], 1, "[cuta]"), "[cuta]aformat=");
	}
}
