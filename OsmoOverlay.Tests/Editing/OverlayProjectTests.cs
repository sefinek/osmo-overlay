using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Tests;

[TestClass]
public sealed class OverlayProjectTests
{
	[TestMethod]
	public void SaveAndLoad_RoundTripsEverything()
	{
		var preset = OverlayPreset.CreateDefault("p1", "Mine");
		var project = new OverlayProject([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")], Path.GetFullPath("out.mp4"), [new FrameRange(10, 20, new CutTransition(CutTransitionKind.FadeWhite, 0.8))], new ReframeView(10, 5, 0, 90, false), 42, "p1", preset)
		{
			ExternalTrack = new ExternalTrackSource(Path.GetFullPath("ride.fit"), Synchronization: new TrackSynchronization(new TrackSyncAnchor(18.5, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc))))
		};
		string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{OverlayProject.Extension}");

		try
		{
			project.Save(path);
			var loaded = OverlayProject.Load(path);

			CollectionAssert.AreEqual(project.InputPaths, loaded.InputPaths);
			CollectionAssert.AreEqual(project.Cuts, loaded.Cuts);
			Assert.AreEqual(project.Reframe, loaded.Reframe);
			Assert.AreEqual(42, loaded.PreviewFrame);
			Assert.AreEqual(project.ExternalTrack, loaded.ExternalTrack);
			Assert.IsTrue(OverlayPresetStore.SameLayout(preset, OverlayPresetStore.Sanitize(loaded.Preset)));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[TestMethod]
	[DataRow("\\\\host\\share\\ride.fit")]
	[DataRow("//host/share/ride.gpx")]
	[DataRow("relative.fit")]
	public void Load_RejectsExternalTracksOffLocalDrives(string trackPath)
	{
		string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{OverlayProject.Extension}");
		new OverlayProject([Path.GetFullPath("a.mp4")], null, [], null, 0, null, null) { ExternalTrack = new ExternalTrackSource(trackPath) }.Save(path);
		try
		{
			Assert.ThrowsExactly<InvalidDataException>(() => OverlayProject.Load(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[TestMethod]
	public void SaveAndLoad_KeepsTheMusic()
	{
		MusicClip clip = new(Path.GetFullPath("song.mp3"), 4.5, 1, 20, 180, -6, 1, 2, true, -12);
		string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{OverlayProject.Extension}");
		new OverlayProject([Path.GetFullPath("a.mp4")], null, [], null, 0, null, null) { Music = [clip] }.Save(path);

		try
		{
			CollectionAssert.AreEqual(new[] { clip }, OverlayProject.Load(path).Music);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[TestMethod]
	[DataRow("\\\\host\\share\\song.mp3")]
	[DataRow("relative.mp3")]
	public void Load_RejectsMusicOffLocalDrives(string musicPath)
	{
		string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{OverlayProject.Extension}");
		new OverlayProject([Path.GetFullPath("a.mp4")], null, [], null, 0, null, null) { Music = [new MusicClip(musicPath, 0, 0, 10, 10)] }.Save(path);
		try
		{
			Assert.ThrowsExactly<InvalidDataException>(() => OverlayProject.Load(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[TestMethod]
	public void Load_RejectsNewerFormat()
	{
		string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{OverlayProject.Extension}");
		File.WriteAllText(path, """{"InputPaths":[],"Cuts":[],"Format":999}""");

		try
		{
			Assert.ThrowsExactly<InvalidDataException>(() => OverlayProject.Load(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[TestMethod]
	[DataRow("\\\\host\\share\\a.mp4", null)]
	[DataRow("//host/share/a.mp4", null)]
	[DataRow("relative.mp4", "\\\\host\\share\\out.mp4")]
	[DataRow("relative.mp4", null)]
	public void Load_RejectsPathsOffTheLocalDrives(string input, string? output)
	{
		string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{OverlayProject.Extension}");
		new OverlayProject([input], output, [], null, 0, null, null).Save(path);

		try
		{
			Assert.ThrowsExactly<InvalidDataException>(() => OverlayProject.Load(path));
		}
		finally
		{
			File.Delete(path);
		}
	}
}
