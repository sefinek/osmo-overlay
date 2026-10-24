using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Rendering;

[TestClass]
public sealed class RenderQueueTests
{
	private string _dir = "";

	[TestInitialize]
	public void Setup()
	{
		_dir = Path.Combine(Path.GetTempPath(), "ovl-queue-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	[TestCleanup]
	public void Cleanup()
	{
		Directory.Delete(_dir, true);
	}

	private string Project(string name, string recording = "ride.mp4", string? output = null, string? settingsPresetId = null)
	{
		string path = Path.Combine(_dir, name + OverlayProject.Extension);
		new OverlayProject([Path.Combine(_dir, recording)], output is null ? null : Path.Combine(_dir, output), [], null, 0, null, null)
			{ SettingsPresetId = settingsPresetId }.Save(path);
		return path;
	}

	private sealed class Fake
	{
		public List<RenderOptions> Rendered { get; } = [];
		public Func<RenderOptions, CancellationToken, RenderResult>? Behaviour { get; set; }
		public HashSet<string> ExistingOutputs { get; } = [];
		public List<SettingsPreset> Presets { get; } = [];

		public RenderQueue Queue()
		{
			return new RenderQueue(new RenderQueue.Services(
				(options, progress, ct) =>
				{
					Rendered.Add(options);
					progress?.Report(new RenderStatus(RenderPhase.Rendering, "Frame 1", 50, 100, TimeSpan.Zero));
					return Task.FromResult(Behaviour?.Invoke(options, ct) ?? new RenderResult(true, null, TimeSpan.FromSeconds(3)));
				},
				_ => new ProjectRender.Timeline(30, 3000),
				() => new OverlaySettings(),
				() => Presets,
				path => ExistingOutputs.Contains(path) || (path.EndsWith(OverlayProject.Extension) ? File.Exists(path) : Path.GetFileName(path) == "ride.mp4")));
		}
	}

	[TestMethod]
	public async Task RunsTheWaitingProjectsInOrderAndMarksThemDone()
	{
		var fake = new Fake();
		RenderQueue queue = fake.Queue();
		QueueItem a = queue.Add(new QueueRequest(Project("a", output: "a.mp4")));
		QueueItem b = queue.Add(new QueueRequest(Project("b", output: "b.mp4")));

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		CollectionAssert.AreEqual(new[] { Path.Combine(_dir, "a.mp4"), Path.Combine(_dir, "b.mp4") }, fake.Rendered.Select(o => o.OutputPath).ToArray());
		Assert.AreEqual(QueueState.Done, a.State);
		Assert.AreEqual(QueueState.Done, b.State);
		Assert.AreEqual(1.0, a.Progress);
		Assert.AreEqual(TimeSpan.FromSeconds(3), a.Elapsed);
		Assert.IsFalse(queue.IsRunning);
	}

	[TestMethod]
	public async Task AFailedRenderIsNotedAndTheQueueGoesOn()
	{
		var fake = new Fake { Behaviour = (o, _) => o.OutputPath.EndsWith("a.mp4") ? new RenderResult(false, "disk full", TimeSpan.Zero) : new RenderResult(true, null, TimeSpan.Zero) };
		RenderQueue queue = fake.Queue();
		QueueItem a = queue.Add(new QueueRequest(Project("a", output: "a.mp4")));
		QueueItem b = queue.Add(new QueueRequest(Project("b", output: "b.mp4")));

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.AreEqual(QueueState.Failed, a.State);
		Assert.AreEqual("disk full", a.Message);
		Assert.AreEqual(QueueState.Done, b.State);
	}

	[TestMethod]
	public async Task ARenderThatThrowsFailsOnlyItsOwnItem()
	{
		var fake = new Fake { Behaviour = (o, _) => o.OutputPath.EndsWith("a.mp4") ? throw new InvalidOperationException("boom") : new RenderResult(true, null, TimeSpan.Zero) };
		RenderQueue queue = fake.Queue();
		QueueItem a = queue.Add(new QueueRequest(Project("a", output: "a.mp4")));
		QueueItem b = queue.Add(new QueueRequest(Project("b", output: "b.mp4")));

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.AreEqual(QueueState.Failed, a.State);
		Assert.AreEqual("boom", a.Message);
		Assert.AreEqual(QueueState.Done, b.State);
	}

	[TestMethod]
	public async Task CancellingStopsTheQueueAndLeavesTheRestWaiting()
	{
		using var cts = new CancellationTokenSource();
		var fake = new Fake
		{
			Behaviour = (_, _) =>
			{
				cts.Cancel();
				return new RenderResult(false, "Cancelled by user", TimeSpan.Zero);
			}
		};
		RenderQueue queue = fake.Queue();
		QueueItem a = queue.Add(new QueueRequest(Project("a", output: "a.mp4")));
		QueueItem b = queue.Add(new QueueRequest(Project("b", output: "b.mp4")));

		await queue.RunAsync(ExistingOutput.Skip, cts.Token);

		Assert.AreEqual(QueueState.Cancelled, a.State);
		Assert.AreEqual(QueueState.Waiting, b.State);
		Assert.AreEqual(1, fake.Rendered.Count);

		Assert.IsTrue(queue.Retry(a));
		Assert.AreEqual(QueueState.Waiting, a.State);
	}

	[TestMethod]
	public async Task AnExistingOutputIsSkippedByDefault()
	{
		var fake = new Fake();
		fake.ExistingOutputs.Add(Path.Combine(_dir, "a.mp4"));
		RenderQueue queue = fake.Queue();
		QueueItem a = queue.Add(new QueueRequest(Project("a", output: "a.mp4")));

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.AreEqual(QueueState.Skipped, a.State);
		Assert.AreEqual(0, fake.Rendered.Count);
	}

	[TestMethod]
	public async Task AnExistingOutputCanBeNumberedOrWrittenOver()
	{
		var fake = new Fake();
		string target = Path.Combine(_dir, "a.mp4");
		fake.ExistingOutputs.Add(target);
		RenderQueue queue = fake.Queue();
		queue.Add(new QueueRequest(Project("a", output: "a.mp4")));
		await queue.RunAsync(ExistingOutput.Number, CancellationToken.None);
		RenderQueue again = fake.Queue();
		again.Add(new QueueRequest(Project("a", output: "a.mp4")));
		await again.RunAsync(ExistingOutput.Overwrite, CancellationToken.None);

		Assert.AreEqual(Path.Combine(_dir, "a (2).mp4"), fake.Rendered[0].OutputPath);
		Assert.IsFalse(fake.Rendered[0].Overwrite);
		Assert.AreEqual(target, fake.Rendered[1].OutputPath);
		Assert.IsTrue(fake.Rendered[1].Overwrite);
	}

	[TestMethod]
	public async Task AMissingRecordingFailsWithoutRendering()
	{
		var fake = new Fake();
		RenderQueue queue = fake.Queue();
		QueueItem item = queue.Add(new QueueRequest(Project("a", recording: "missing.mp4")));

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.AreEqual(QueueState.Failed, item.State);
		StringAssert.Contains(item.Message, "missing.mp4");
		Assert.AreEqual(0, fake.Rendered.Count);
	}

	[TestMethod]
	public async Task AProjectThatIsGoneFails()
	{
		var fake = new Fake();
		RenderQueue queue = fake.Queue();
		QueueItem item = queue.Add(new QueueRequest(Path.Combine(_dir, "gone" + OverlayProject.Extension)));

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.AreEqual(QueueState.Failed, item.State);
		Assert.AreEqual(0, fake.Rendered.Count);
	}

	[TestMethod]
	public async Task TheExportProfileIsAppliedToThatRendersSettingsOnly()
	{
		var fake = new Fake();
		RenderQueue queue = fake.Queue();
		queue.Add(new QueueRequest(Project("a", output: "a.mp4"), ProfileId: "phone720"));
		queue.Add(new QueueRequest(Project("b", output: "b.mp4")));

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.AreEqual(720, fake.Rendered[0].Settings!.OutputResolution);
		Assert.AreEqual("h264", fake.Rendered[0].Settings!.OutputCodec);
		Assert.IsNull(fake.Rendered[1].Settings!.OutputResolution);
	}

	[TestMethod]
	public async Task TheSettingsAreReadOnceWhenTheQueueStartsNotForEveryItem()
	{
		int reads = 0;
		var fake = new Fake();
		RenderQueue queue = new(new RenderQueue.Services(
			(options, _, _) =>
			{
				fake.Rendered.Add(options);
				return Task.FromResult(new RenderResult(true, null, TimeSpan.Zero));
			},
			_ => new ProjectRender.Timeline(30, 3000),
			() => new OverlaySettings { SpeedCorrectionPercent = ++reads },
			() => [],
			path => path.EndsWith(OverlayProject.Extension) ? File.Exists(path) : Path.GetFileName(path) == "ride.mp4"));
		queue.Add(new QueueRequest(Project("a", output: "a.mp4")));
		queue.Add(new QueueRequest(Project("b", output: "b.mp4")));

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.AreEqual(1, reads);
		Assert.AreEqual(fake.Rendered[0].Settings!.SpeedCorrectionPercent, fake.Rendered[1].Settings!.SpeedCorrectionPercent);
	}

	[TestMethod]
	public async Task AnUnknownProfileFailsTheItem()
	{
		var fake = new Fake();
		RenderQueue queue = fake.Queue();
		QueueItem item = queue.Add(new QueueRequest(Project("a"), ProfileId: "nope"));

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.AreEqual(QueueState.Failed, item.State);
		StringAssert.Contains(item.Message, "nope");
	}

	[TestMethod]
	public async Task TheProjectsSettingsPresetIsUsed()
	{
		var fake = new Fake();
		fake.Presets.Add(new SettingsPreset("bike", "Bike", new SettingsPresetValues(SpeedCorrectionPercent: 5)));
		RenderQueue queue = fake.Queue();
		queue.Add(new QueueRequest(Project("a", output: "a.mp4", settingsPresetId: "bike")));

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.AreEqual(5, fake.Rendered[0].Settings!.SpeedCorrectionPercent);
	}

	[TestMethod]
	public async Task AnAlphaRequestWritesTheTransparentOverlayToItsOwnFile()
	{
		var fake = new Fake();
		RenderQueue queue = fake.Queue();
		queue.Add(new QueueRequest(Project("a", output: "a_overlay.mp4"), Alpha: AlphaCodec.ProRes4444));

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.AreEqual(Path.Combine(_dir, "a_alpha.mov"), fake.Rendered[0].OutputPath);
		Assert.AreEqual(AlphaCodec.ProRes4444, fake.Rendered[0].Alpha);
	}

	[TestMethod]
	public async Task TheRequestsOwnOutputWinsOverTheProjects()
	{
		var fake = new Fake();
		RenderQueue queue = fake.Queue();
		string elsewhere = Path.Combine(_dir, "elsewhere.mp4");
		queue.Add(new QueueRequest(Project("a", output: "a.mp4"), Output: elsewhere));

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.AreEqual(elsewhere, fake.Rendered[0].OutputPath);
	}

	[TestMethod]
	public async Task AnItemAddedWhileItRunsIsRenderedToo()
	{
		var fake = new Fake();
		RenderQueue queue = fake.Queue();
		string late = Project("late", output: "late.mp4");
		fake.Behaviour = (_, _) =>
		{
			if (fake.Rendered.Count == 1) queue.Add(new QueueRequest(late));
			return new RenderResult(true, null, TimeSpan.Zero);
		};
		queue.Add(new QueueRequest(Project("a", output: "a.mp4")));

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.AreEqual(2, fake.Rendered.Count);
	}

	[TestMethod]
	public void WaitingItemsMoveAmongThemselvesAndFinishedOnesStay()
	{
		RenderQueue queue = new Fake().Queue();
		QueueItem a = queue.Add(new QueueRequest("a")), b = queue.Add(new QueueRequest("b")), c = queue.Add(new QueueRequest("c"));

		Assert.IsTrue(queue.Move(c, -1));
		CollectionAssert.AreEqual(new[] { a, c, b }, queue.Items.ToArray());
		Assert.IsFalse(queue.Move(a, -1), "already first");
		Assert.IsFalse(queue.Move(b, 1), "already last");
	}

	[TestMethod]
	public async Task AnItemBeingRenderedCannotBeRemovedButAFinishedOneCan()
	{
		var fake = new Fake();
		RenderQueue queue = fake.Queue();
		QueueItem item = queue.Add(new QueueRequest(Project("a", output: "a.mp4")));
		bool removedWhileRunning = true;
		fake.Behaviour = (_, _) =>
		{
			removedWhileRunning = queue.Remove(item);
			return new RenderResult(true, null, TimeSpan.Zero);
		};

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.IsFalse(removedWhileRunning);
		Assert.IsTrue(queue.Remove(item));
		Assert.AreEqual(0, queue.Items.Count);
	}

	[TestMethod]
	public async Task ClearFinishedKeepsWhatIsStillWaiting()
	{
		var fake = new Fake();
		RenderQueue queue = fake.Queue();
		queue.Add(new QueueRequest(Project("a", output: "a.mp4")));
		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);
		QueueItem waiting = queue.Add(new QueueRequest("later"));

		queue.ClearFinished();

		CollectionAssert.AreEqual(new[] { waiting }, queue.Items.ToArray());
	}

	[TestMethod]
	public async Task ItCannotRunTwiceAtOnce()
	{
		var fake = new Fake();
		RenderQueue queue = fake.Queue();
		queue.Add(new QueueRequest(Project("a", output: "a.mp4")));
		Task? second = null;
		fake.Behaviour = (_, _) =>
		{
			second = queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);
			return new RenderResult(true, null, TimeSpan.Zero);
		};

		await queue.RunAsync(ExistingOutput.Skip, CancellationToken.None);

		Assert.IsTrue(second!.IsFaulted);
	}

	[TestMethod]
	public void ItemChangedReportsProgressAndListChangedReportsTheList()
	{
		RenderQueue queue = new Fake().Queue();
		int listChanges = 0;
		queue.ListChanged += () => listChanges++;

		QueueItem item = queue.Add(new QueueRequest("a"));
		queue.Move(item, 1);
		queue.Remove(item);

		Assert.AreEqual(2, listChanges, "an add and a remove; the move had nowhere to go");
	}

	[TestMethod]
	public void OutputFiles_Resolve()
	{
		HashSet<string> there = [Path.Combine("d", "a.mp4"), Path.Combine("d", "a (2).mp4")];
		string path = Path.Combine("d", "a.mp4");

		Assert.AreEqual(Path.Combine("d", "free.mp4"), OutputFiles.Resolve(Path.Combine("d", "free.mp4"), ExistingOutput.Skip, there.Contains));
		Assert.IsNull(OutputFiles.Resolve(path, ExistingOutput.Skip, there.Contains));
		Assert.AreEqual(path, OutputFiles.Resolve(path, ExistingOutput.Overwrite, there.Contains));
		Assert.AreEqual(Path.Combine("d", "a (3).mp4"), OutputFiles.Resolve(path, ExistingOutput.Number, there.Contains));
	}
}
