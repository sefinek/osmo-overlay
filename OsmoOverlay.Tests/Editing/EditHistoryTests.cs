using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Reframe;

namespace OsmoOverlay.Tests;

[TestClass]
public sealed class EditHistoryTests
{
	private static EditSnapshot State(string preset = "p", params long[] cutStarts)
	{
		return new EditSnapshot(preset, [], null, [.. cutStarts.Select(s => new FrameRange(s, s + 10))], new ReframeView());
	}

	[TestMethod]
	public void UndoAndRedo_WalkBetweenCommittedStates()
	{
		var history = new EditHistory();
		history.Reset(State());
		history.Commit(State("p", 1));
		history.Commit(State("p", 1, 20));

		Assert.AreEqual(1, history.Undo()!.Cuts.Count);
		Assert.AreEqual(0, history.Undo()!.Cuts.Count);
		Assert.IsNull(history.Undo());
		Assert.AreEqual(1, history.Redo()!.Cuts.Count);
		Assert.AreEqual(2, history.Redo()!.Cuts.Count);
		Assert.IsNull(history.Redo());
	}

	[TestMethod]
	public void Commit_OfTheSameStateAddsNoStep_AndANewChangeDropsRedo()
	{
		var history = new EditHistory();
		history.Reset(State());
		history.Commit(State());
		Assert.IsFalse(history.CanUndo);

		history.Commit(State("p", 1));
		history.Undo();
		Assert.IsTrue(history.CanRedo);
		history.Commit(State("p", 2));
		Assert.IsFalse(history.CanRedo);
	}

	[TestMethod]
	public void Commit_WithTheSameKeyBecomesOneStepUntilSealed()
	{
		var history = new EditHistory();
		history.Reset(State());
		history.Commit(State("p", 1), "drag");
		history.Commit(State("p", 2), "drag");
		history.Commit(State("p", 3), "drag");
		history.Seal();
		history.Commit(State("p", 4), "drag");

		Assert.AreEqual(3, history.Undo()!.Cuts[0].Start);
		Assert.AreEqual(0, history.Undo()!.Cuts.Count);
		Assert.IsNull(history.Undo());
	}

	[TestMethod]
	public void Commit_OfAnotherPresetStartsANewHistory()
	{
		var history = new EditHistory();
		history.Reset(State("a"));
		history.Commit(State("a", 1));
		history.Commit(State("b", 5));

		Assert.IsFalse(history.CanUndo);
		Assert.AreEqual("b", history.Current!.PresetId);
	}
}
