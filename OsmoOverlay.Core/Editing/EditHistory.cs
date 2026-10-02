using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Reframe;

namespace OsmoOverlay.Core;

/// <summary>
///     What Undo and Redo move between: the active preset's layout, the cuts and the 360 view. Elements and layers are
///     immutable records in lists the GUI only ever replaces (copy-on-write), so a snapshot just keeps the references.
/// </summary>
public sealed class EditSnapshot(
	string? presetId,
	IReadOnlyList<OverlayElement> elements,
	IReadOnlyList<OverlayLayer>? layers,
	IReadOnlyList<FrameRange> cuts,
	ReframeView reframe)
{
	public string? PresetId { get; } = presetId;
	public IReadOnlyList<OverlayElement> Elements { get; } = elements;
	public IReadOnlyList<OverlayLayer>? Layers { get; } = layers;
	public IReadOnlyList<FrameRange> Cuts { get; } = cuts;
	public ReframeView Reframe { get; } = reframe;

	public bool SameAs(EditSnapshot other)
	{
		return PresetId == other.PresetId && Reframe == other.Reframe &&
		       Cuts.SequenceEqual(other.Cuts) && Elements.SequenceEqual(other.Elements) &&
		       (Layers is null ? other.Layers is null : other.Layers is not null && Layers.SequenceEqual(other.Layers));
	}
}

/// <summary>
///     Undo/redo by snapshots taken after each change (Commit): the one before it goes on the undo stack. A gesture that
///     changes the state many times a second (a drag, typing, the 360 view's drag) passes a key: changes with the same key
///     within CoalesceWindow stay one step, until Seal ends the gesture. A change of preset starts a new history.
/// </summary>
public sealed class EditHistory
{
	private const int MaxSteps = 200;
	private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1);

	private readonly List<EditSnapshot> _undo = [];
	private readonly List<EditSnapshot> _redo = [];
	private string? _openKey;
	private DateTime _lastCommit;

	public bool CanUndo => _undo.Count > 0;
	public bool CanRedo => _redo.Count > 0;

	/// <summary>The state as of the last commit, undo or redo - null before the first Reset.</summary>
	public EditSnapshot? Current { get; private set; }

	/// <summary>The given state is the baseline now (a recording or project was loaded) - nothing before it can be undone.</summary>
	public void Reset(EditSnapshot state)
	{
		_undo.Clear();
		_redo.Clear();
		Current = state;
		_openKey = null;
	}

	/// <summary>Seals the gesture: the next change with the same key starts a step of its own.</summary>
	public void Seal()
	{
		_openKey = null;
	}

	public void Commit(EditSnapshot state, string? coalesceKey = null)
	{
		if (Current is null || Current.PresetId != state.PresetId)
		{
			Reset(state);
			return;
		}

		if (Current.SameAs(state)) return;

		DateTime now = DateTime.UtcNow;
		bool coalesce = coalesceKey is not null && coalesceKey == _openKey && now - _lastCommit < CoalesceWindow && _undo.Count > 0;
		if (!coalesce)
		{
			_undo.Add(Current);
			if (_undo.Count > MaxSteps) _undo.RemoveAt(0);
		}

		_redo.Clear();
		Current = state;
		_openKey = coalesceKey;
		_lastCommit = now;
	}

	/// <summary>The state to go back to, or null when there is nothing to undo.</summary>
	public EditSnapshot? Undo()
	{
		if (Current is null || _undo.Count == 0) return null;

		_redo.Add(Current);
		Current = _undo[^1];
		_undo.RemoveAt(_undo.Count - 1);
		_openKey = null;
		return Current;
	}

	public EditSnapshot? Redo()
	{
		if (Current is null || _redo.Count == 0) return null;

		_undo.Add(Current);
		Current = _redo[^1];
		_redo.RemoveAt(_redo.Count - 1);
		_openKey = null;
		return Current;
	}
}
