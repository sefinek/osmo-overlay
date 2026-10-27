using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Rendering;

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

		var details = QueueDetails.Of(new QueueRequest(copy, Name: "My ride"), new OverlaySettings());

		Assert.AreEqual("My ride", details.Name);
		Assert.AreNotEqual(Path.GetFileNameWithoutExtension(copy), details.Name);
	}

	[TestMethod]
	public void WithoutANameItIsTheFirstRecordings()
	{
		var details = QueueDetails.Of(new QueueRequest(Queued(Project())), new OverlaySettings());

		Assert.AreEqual("GX010001", details.Name);
	}

	[TestMethod]
	public void ItListsTheRecordingsTheOutputAndWhatTheProjectHolds()
	{
		var details = QueueDetails.Of(new QueueRequest(Queued(Project())), new OverlaySettings());

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
		Assert.AreEqual(Path.Combine(_dir, "out_alpha.webm"), QueueDetails.Of(new QueueRequest(copy, Alpha: AlphaCodec.Vp9), new OverlaySettings()).OutputPath);
	}

	[TestMethod]
	public void AProjectWithoutAnOutputGetsTheDefaultOne()
	{
		OverlayProject project = Project() with { OutputPath = null };

		var details = QueueDetails.Of(new QueueRequest(Queued(project)), new OverlaySettings());

		Assert.AreEqual(Path.Combine(_dir, "GX010001_overlay.mp4"), details.OutputPath);
	}

	[TestMethod]
	public void AGoneProjectIsReportedNotThrown()
	{
		var details = QueueDetails.Of(new QueueRequest(Path.Combine(_dir, "gone.ovproj"), Name: "Gone"), new OverlaySettings());

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
