using OsmoOverlay.Core;

namespace OsmoOverlay.Tests.Editing;

[TestClass]
public sealed class CommandLineSpecsTests
{
	[TestMethod]
	public void VolumeSpec_ParsesTimesAndLevelsAndOff()
	{
		Assert.IsTrue(VolumeSpec.TryParse("0:off, 2:0 ,1:00:-6", out List<VolumePoint> points, out _));

		CollectionAssert.AreEqual(new[] { new VolumePoint(0, VolumeLine.SilentDb), new VolumePoint(2, 0), new VolumePoint(60, -6) }, points);
	}

	[TestMethod]
	public void VolumeSpec_SortsPoints()
	{
		Assert.IsTrue(VolumeSpec.TryParse("10:-3,0:-3", out List<VolumePoint> points, out _));

		Assert.AreEqual(0, points[0].Seconds);
	}

	[TestMethod]
	[DataRow("")]
	[DataRow("abc")]
	[DataRow("5")]
	[DataRow("5:loud")]
	[DataRow("x:-3")]
	public void VolumeSpec_RefusesWhatIsNotTimeColonLevel(string spec)
	{
		Assert.IsFalse(VolumeSpec.TryParse(spec, out _, out string error));
		Assert.IsFalse(string.IsNullOrEmpty(error));
	}

	[TestMethod]
	public void VolumeSpec_FormatParsesBackToTheSameLine()
	{
		List<VolumePoint> line = [new(0, VolumeLine.SilentDb), new(1.5, 0), new(30, -9.5)];

		Assert.IsTrue(VolumeSpec.TryParse(VolumeSpec.Format(line), out List<VolumePoint> back, out _));

		CollectionAssert.AreEqual(line, back);
	}

	[TestMethod]
	public void MusicSpec_FillsInWhatIsLeftOut()
	{
		Assert.IsTrue(MusicSpec.TryParse(@"C:\music\song.mp3", 120, out MusicClip? clip, out _));

		Assert.AreEqual(@"C:\music\song.mp3", clip!.Path);
		Assert.AreEqual(0, clip.StartSeconds);
		Assert.AreEqual(120, clip.LengthSeconds, 1e-9);
		Assert.AreEqual(120, clip.SourceSeconds);
		Assert.IsFalse(clip.Loop);
	}

	[TestMethod]
	public void MusicSpec_ReadsEveryOption()
	{
		Assert.IsTrue(MusicSpec.TryParse("a.mp3;start=1:00;in=5;len=30;db=-6;fadein=2;fadeout=3;duck=-12;loop", 100, out MusicClip? clip, out _));

		Assert.AreEqual(new MusicClip("a.mp3", 60, 5, 30, 100, -6, 2, 3, true, -12), clip);
	}

	[TestMethod]
	public void MusicSpec_LengthRunsToTheEndOfTheFileFromIn()
	{
		Assert.IsTrue(MusicSpec.TryParse("a.mp3;in=20", 100, out MusicClip? clip, out _));

		Assert.AreEqual(80, clip!.LengthSeconds, 1e-9);
	}

	[TestMethod]
	public void MusicSpec_DuckingCanOnlyLowerTheRecording()
	{
		Assert.IsTrue(MusicSpec.TryParse("a.mp3;duck=6", 100, out MusicClip? clip, out _));

		Assert.AreEqual(0, clip!.DuckDb);
	}

	[TestMethod]
	[DataRow("a.mp3;loop")]
	[DataRow("a.mp3;in=100")]
	[DataRow("a.mp3;len=0")]
	[DataRow("a.mp3;volume=3")]
	[DataRow("a.mp3;start")]
	[DataRow("")]
	public void MusicSpec_RefusesWhatCantBePlayed(string spec)
	{
		Assert.IsFalse(MusicSpec.TryParse(spec, 100, out MusicClip? clip, out string error));
		Assert.IsNull(clip);
		Assert.IsFalse(string.IsNullOrEmpty(error));
	}

	[TestMethod]
	public void MusicSpec_FormatParsesBackToTheSameClip()
	{
		var clip = new MusicClip("my song.mp3", 12.5, 3, 40, 90, -4.5, 1, 2, true, -10);

		Assert.IsTrue(MusicSpec.TryParse(MusicSpec.Format(clip), 90, out MusicClip? back, out _));

		Assert.AreEqual(clip, back);
	}
}
