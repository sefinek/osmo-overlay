using Avalonia.Controls;
using Avalonia.Input;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     Undo/redo (Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z) and copy/cut/paste/duplicate of a widget (Ctrl+C/X/V/D, Delete) - in the
///     preview and anywhere but a text box, which keeps its own. The history covers what the user edits: the active
///     preset's layout, the cuts and the 360 view (EditSnapshot). The three places those change report to it
///     (ReplaceActiveElements, ApplyCuts, ApplyReframe); Preset-level operations (switching, new, delete, reset) and loading
///     a recording or project start a new history.
/// </summary>
public partial class MainWindow
{
	private const float PasteOffset = 50;

	private readonly EditHistory _history = new();
	private bool _applyingHistory;
	private OverlayElement? _copiedElement;
	private int _pasteCount;

	private EditSnapshot CaptureState()
	{
		return new EditSnapshot(_activePresetId, ActiveElements, ActivePreset?.Layers, [.. _cuts], _reframe);
	}

	private bool HistoryReady => !_applyingHistory && _overlayPresetsLoaded && _summary is not null;

	/// <summary>Reports the state after a change. `gestureKey` is set for the many small changes of one drag or typing run, which stay one step.</summary>
	private void CommitHistory(string? gestureKey = null)
	{
		if (HistoryReady) _history.Commit(CaptureState(), gestureKey);
	}

	/// <summary>The current state is where the history starts - a loaded recording or project, a switched or reset preset.</summary>
	private void ResetHistory()
	{
		if (HistoryReady) _history.Reset(CaptureState());
	}

	private void SealHistory()
	{
		_history.Seal();
	}

	private void Undo()
	{
		StepHistory(_history.Undo);
	}

	private void Redo()
	{
		StepHistory(_history.Redo);
	}

	private void StepHistory(Func<EditSnapshot?> step)
	{
		if (!HistoryReady) return;

		// A preset switched since the last change starts a new history first, so nothing from the other one comes back.
		CommitHistory();
		if (step() is not { } snapshot) return;

		ApplySnapshot(snapshot);
	}

	private void ApplySnapshot(EditSnapshot snapshot)
	{
		int index = _overlayPresets.FindIndex(p => p.Id == _activePresetId);
		if (index < 0 || snapshot.PresetId != _activePresetId) return;

		_applyingHistory = true;
		try
		{
			_overlayPresets[index] = _overlayPresets[index] with
			{
				Elements = [.. snapshot.Elements],
				Layers = snapshot.Layers is null ? null : [.. snapshot.Layers]
			};
			_cuts = [.. snapshot.Cuts];
			ApplyCuts();
			ApplyReframe(snapshot.Reframe);
			if (Is360) ScheduleThumbnailReload();

			ShowLayout();
			SaveOverlayPresets();
			RefreshLayers();
			RefreshWidgetList();

			if (_selectedElementId is { } selected && ActiveElements.All(e => e.Id != selected)) _selectedElementId = null;
			if (_editingElementId is { } editing)
			{
				if (ActiveElements.FirstOrDefault(e => e.Id == editing) is { } element) PopulateElementSettings(element);
				else CloseElementSettings();
			}

			RefreshSelectionHighlight();
		}
		finally
		{
			_applyingHistory = false;
		}
	}

	/// <summary>Keys the preview and the rest of the window share, except while a text box has the focus (it keeps its own undo and clipboard).</summary>
	private void OnEditShortcutKeyDown(object? sender, KeyEventArgs e)
	{
		if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
		if (e.Key == Key.S)
		{
			e.Handled = true;
			SaveProjectFromShortcut(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
			return;
		}

		if (_summary is null || FocusManager?.GetFocusedElement() is TextBox) return;

		bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
		Action? action = e.Key switch
		{
			Key.Z when shift => Redo,
			Key.Z => Undo,
			Key.Y when !shift => Redo,
			Key.C when !shift => CopySelectedElement,
			Key.X when !shift => CutSelectedElement,
			Key.V when !shift => PasteElement,
			Key.D when !shift => DuplicateSelectedElement,
			_ => null
		};
		if (action is null) return;

		action();
		e.Handled = true;
	}

	private OverlayElement? SelectedElement => _selectedElementId is { } id ? ActiveElements.FirstOrDefault(e => e.Id == id) : null;

	private void CopySelectedElement()
	{
		if (SelectedElement is not { } element) return;

		_copiedElement = element;
		_pasteCount = 0;
	}

	private void CutSelectedElement()
	{
		if (IsActivePresetDefault || SelectedElement is not { } element) return;

		CopySelectedElement();
		RemoveElementInstance(element.Id);
	}

	private void PasteElement()
	{
		if (_copiedElement is null) return;

		_pasteCount++;
		AddCopyOf(_copiedElement, PasteOffset * _pasteCount);
	}

	private void DuplicateSelectedElement()
	{
		if (SelectedElement is { } element) AddCopyOf(element, PasteOffset);
	}

	/// <summary>A new instance of `source` shifted a little (so it doesn't hide under the original), on a layer of its own and selected.</summary>
	private void AddCopyOf(OverlayElement source, float offset)
	{
		if (_summary is null || IsActivePresetDefault) return;

		OverlayElement copy = source with
		{
			Id = Guid.NewGuid().ToString("N"),
			LayerId = null,
			Visible = true,
			X = Math.Min(source.X + offset, OverlayElementBounds.ReferenceWidth),
			Y = Math.Min(source.Y + offset, OverlayElementBounds.ReferenceHeight)
		};

		ReplaceActiveElements([.. ActiveElements, copy]);
		ShowLayout();
		SaveOverlayPresets();

		_selectedElementId = copy.Id;
		RefreshWidgetList();
		if (_editingElementId is not null) OpenElementSettings(copy);
	}

	/// <summary>Delete with nothing cut or selected on the timeline removes the selected widget.</summary>
	private bool TryDeleteSelectedElement()
	{
		if (_selectedCut is not null || Selection is not null || IsActivePresetDefault || SelectedElement is not { } element) return false;

		RemoveElementInstance(element.Id);
		return true;
	}
}
