using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Rendering;

[TestClass]
public sealed class QueueStoreTests
{
	private string _dir = "";

	[TestInitialize]
	public void Setup()
	{
		_dir = Path.Combine(Path.GetTempPath(), "ovl-store-" + Guid.NewGuid().ToString("N"));
	}

	[TestCleanup]
	public void Cleanup()
	{
		if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
	}

	private static OverlayProject Project(string recording = "ride.mp4")
	{
		return new OverlayProject([Path.Combine(Path.GetTempPath(), recording)], null, [new FrameRange(10, 20)], null, 0, null, null);
	}

	[TestMethod]
	public void Snapshot_IsACopyOfTheProjectInTheQueuesFolder()
	{
		var store = new QueueStore(_dir);

		string path = store.Snapshot(Project());

		Assert.AreEqual(Path.GetFullPath(_dir), Path.GetDirectoryName(Path.GetFullPath(path)));
		Assert.AreEqual(1, OverlayProject.Load(path).Cuts.Count);
	}

	[TestMethod]
	public void EverySnapshotIsItsOwnFile()
	{
		var store = new QueueStore(_dir);

		Assert.AreNotEqual(store.Snapshot(Project()), store.Snapshot(Project()));
	}

	[TestMethod]
	public void SaveAndLoad_KeepTheRequestsInOrderWithTheirOptions()
	{
		var store = new QueueStore(_dir);
		List<QueueRequest> requests =
		[
			new(store.Snapshot(Project())),
			new(store.Snapshot(Project()), Path.Combine(_dir, "out.mp4"), "web1080", AlphaCodec.Vp9)
		];

		store.Save(requests);

		CollectionAssert.AreEqual(requests, store.Load());
	}

	[TestMethod]
	public void Load_DropsRequestsWhoseProjectIsGone()
	{
		var store = new QueueStore(_dir);
		QueueRequest kept = new(store.Snapshot(Project()));
		string gone = store.Snapshot(Project());
		store.Save([kept, new QueueRequest(gone)]);
		File.Delete(gone);

		CollectionAssert.AreEqual(new[] { kept }, store.Load());
	}

	[TestMethod]
	public void Save_DeletesCopiesNothingPointsAtAnyMore()
	{
		var store = new QueueStore(_dir);
		string kept = store.Snapshot(Project()), dropped = store.Snapshot(Project());

		store.Save([new QueueRequest(kept)]);

		Assert.IsTrue(File.Exists(kept));
		Assert.IsFalse(File.Exists(dropped));
	}

	[TestMethod]
	public void Load_OfNothingOrGarbageIsAnEmptyQueue()
	{
		var store = new QueueStore(_dir);
		Assert.AreEqual(0, store.Load().Count);

		Directory.CreateDirectory(_dir);
		File.WriteAllText(Path.Combine(_dir, "queue.json"), "{ not json");

		Assert.AreEqual(0, store.Load().Count);
	}
}

[TestClass]
public sealed class QueueSourcesTests
{
	private string _dir = "";

	[TestInitialize]
	public void Setup()
	{
		_dir = Path.Combine(Path.GetTempPath(), "ovl-sources-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	[TestCleanup]
	public void Cleanup()
	{
		Directory.Delete(_dir, true);
	}

	[TestMethod]
	public void ForRecordings_IsOneProjectOfAllOfThemWithTheDefaultOutput()
	{
		string a = Path.Combine(_dir, "GX010001.MP4"), b = Path.Combine(_dir, "GX020001.MP4");

		OverlayProject project = ProjectRender.ForRecordings([a, b], new OverlaySettings());

		CollectionAssert.AreEqual(new[] { a, b }, project.InputPaths);
		Assert.AreEqual(Path.Combine(_dir, "GX010001_overlay.mp4"), project.OutputPath);
		Assert.AreEqual(0, project.Cuts.Count);
		Assert.IsNull(project.Preset);
		Assert.IsNull(project.Reframe);
	}

	[TestMethod]
	public void ForRecordings_UsesTheOutputFolderAndTheActiveSettingsPreset()
	{
		string folder = Path.Combine(_dir, "out");
		Directory.CreateDirectory(folder);

		OverlayProject project = ProjectRender.ForRecordings([Path.Combine(_dir, "ride.mp4")],
			new OverlaySettings { DefaultOutputFolder = folder, ActiveSettingsPresetId = "bike" });

		Assert.AreEqual(Path.Combine(folder, "ride_overlay.mp4"), project.OutputPath);
		Assert.AreEqual("bike", project.SettingsPresetId);
	}

	[TestMethod]
	public void ARecordingsProjectCanBeQueuedLikeAnyOther()
	{
		var store = new QueueStore(Path.Combine(_dir, "queue"));

		string path = store.Snapshot(ProjectRender.ForRecordings([Path.Combine(_dir, "ride.mp4")], new OverlaySettings()));

		Assert.AreEqual("ride_overlay.mp4", Path.GetFileName(OverlayProject.Load(path).OutputPath));
	}

	[TestMethod]
	public void Import_QueuesACopyThatLaterEditsOfTheProjectDontChange()
	{
		var store = new QueueStore(Path.Combine(_dir, "queue"));
		string saved = Path.Combine(_dir, "mine.ovproj");
		new OverlayProject([Path.Combine(_dir, "a.mp4")], null, [new FrameRange(10, 20)], null, 0, null, null).Save(saved);

		string queued = store.Import(saved);
		File.WriteAllText(saved, "edited later");

		Assert.AreNotEqual(saved, queued);
		Assert.AreEqual(1, OverlayProject.Load(queued).Cuts.Count);
	}

	[TestMethod]
	public void Import_RefusesWhatIsNotAProject()
	{
		var store = new QueueStore(Path.Combine(_dir, "queue"));
		string notProject = Path.Combine(_dir, "x.ovproj");
		File.WriteAllText(notProject, "{ not json");

		Assert.ThrowsExactly<System.Text.Json.JsonException>(() => store.Import(notProject));
		Assert.IsFalse(Directory.Exists(Path.Combine(_dir, "queue")) && Directory.EnumerateFiles(Path.Combine(_dir, "queue")).Any(), "nothing was copied");
	}

	[TestMethod]
	public void Import_RefusesAProjectFromANewerVersion()
	{
		var store = new QueueStore(Path.Combine(_dir, "queue"));
		string newer = Path.Combine(_dir, "newer.ovproj");
		new OverlayProject([Path.Combine(_dir, "a.mp4")], null, [], null, 0, null, null) { Format = OverlayProject.CurrentFormat + 1 }.Save(newer);

		Assert.ThrowsExactly<InvalidDataException>(() => store.Import(newer));
	}
}

[TestClass]
public sealed class QueueDetailsTests
{
	private string _dir = "";

	[TestInitialize]
	public void Setup()
	{
		_dir = Path.Combine(Path.GetTempPath(), "ovl-details-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	[TestCleanup]
	public void Cleanup()
	{
		Directory.Delete(_dir, true);
	}

	private string Queued(OverlayProject project)
	{
		return new QueueStore(Path.Combine(_dir, "queue")).Snapshot(project);
	}

	private OverlayProject Project(string output = "out.mp4")
	{
		return new OverlayProject([Path.Combine(_dir, "GX010001.MP4"), Path.Combine(_dir, "GX020001.MP4")], Path.Combine(_dir, output),
			[new FrameRange(10, 20), new FrameRange(40, 50)], null, 0, null, null)
		{
			Music = [new MusicClip(Path.Combine(_dir, "song.mp3"), 0, 0, 5, 30)],
			Volume = [new VolumePoint(1, -6)]
		};
	}

	[TestMethod]
	public void TheNameIsTheOneItWasQueuedUnderNotTheCopysFileName()
	{
		string copy = Queued(Project());

		QueueDetails details = QueueDetails.Of(new QueueRequest(copy, Name: "My ride"), new OverlaySettings());

		Assert.AreEqual("My ride", details.Name);
		Assert.AreNotEqual(Path.GetFileNameWithoutExtension(copy), details.Name);
	}

	[TestMethod]
	public void WithoutANameItIsTheFirstRecordings()
	{
		QueueDetails details = QueueDetails.Of(new QueueRequest(Queued(Project())), new OverlaySettings());

		Assert.AreEqual("GX010001", details.Name);
	}

	[TestMethod]
	public void ItListsTheRecordingsTheOutputAndWhatTheProjectHolds()
	{
		QueueDetails details = QueueDetails.Of(new QueueRequest(Queued(Project())), new OverlaySettings());

		CollectionAssert.AreEqual(new[] { Path.Combine(_dir, "GX010001.MP4"), Path.Combine(_dir, "GX020001.MP4") }, details.Recordings.ToArray());
		Assert.AreEqual(Path.Combine(_dir, "out.mp4"), details.OutputPath);
		Assert.AreEqual(2, details.Cuts);
		Assert.AreEqual(1, details.MusicClips);
		Assert.IsTrue(details.EditedVolume);
		Assert.IsFalse(details.ExternalTrack);
		Assert.IsNull(details.Problem);
	}

	[TestMethod]
	public void TheOutputIsWhatWillBeWritten()
	{
		string copy = Queued(Project());

		Assert.AreEqual(Path.Combine(_dir, "elsewhere.mp4"), QueueDetails.Of(new QueueRequest(copy, Output: Path.Combine(_dir, "elsewhere.mp4")), new OverlaySettings()).OutputPath);
		Assert.AreEqual(Path.Combine(_dir, "out_alpha.webm"), QueueDetails.Of(new QueueRequest(copy, Alpha: OsmoOverlay.Core.Ffmpeg.AlphaCodec.Vp9), new OverlaySettings()).OutputPath);
	}

	[TestMethod]
	public void AProjectWithoutAnOutputGetsTheDefaultOne()
	{
		OverlayProject project = Project() with { OutputPath = null };

		QueueDetails details = QueueDetails.Of(new QueueRequest(Queued(project)), new OverlaySettings());

		Assert.AreEqual(Path.Combine(_dir, "GX010001_overlay.mp4"), details.OutputPath);
	}

	[TestMethod]
	public void AGoneProjectIsReportedNotThrown()
	{
		QueueDetails details = QueueDetails.Of(new QueueRequest(Path.Combine(_dir, "gone.ovproj"), Name: "Gone"), new OverlaySettings());

		Assert.AreEqual("Gone", details.Name);
		Assert.IsNotNull(details.Problem);
		Assert.AreEqual(0, details.Recordings.Count);
	}

	[TestMethod]
	public void TheNameSurvivesTheStore()
	{
		var store = new QueueStore(Path.Combine(_dir, "queue"));
		QueueRequest request = new(store.Snapshot(Project()), Name: "Keep me");

		store.Save([request]);

		Assert.AreEqual("Keep me", store.Load().Single().Name);
	}
}
