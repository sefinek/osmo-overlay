using System.Text.Json;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Rendering;

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

		Assert.ThrowsExactly<JsonException>(() => store.Import(notProject));
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
