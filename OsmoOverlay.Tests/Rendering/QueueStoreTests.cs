using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;

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
