using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     Looking at another overlay preset in the preview without switching to it (F8, or the button beside the picker): the preview
///     draws the other layout, nothing else changes - not the active preset, the project, a render. Any edit or a switch of the
///     active preset ends it.
/// </summary>
public partial class MainWindow
{
	private bool _comparing;
	private string? _comparePresetId;
	private bool _syncingCompare;

	private void WireCompare()
	{
		CompareComboBox.ItemTemplate = PresetItemTemplate();
		KeyDown += (_, e) =>
		{
			if (e.Key != Key.F8 || e.KeyModifiers != KeyModifiers.None || FocusManager?.GetFocusedElement() is TextBox) return;

			ToggleCompare();
			e.Handled = true;
		};
	}

	private OverlayPreset? ComparePreset => _comparePresetId is { } id && id != _activePresetId ? _overlayPresets.FirstOrDefault(p => p.Id == id) : null;

	/// <summary>The layout the preview draws: the one being compared while that's on, else the active preset's - muted and unsoloed layers left out either way.</summary>
	private IReadOnlyList<OverlayElement> ShownLayout()
	{
		return _comparing && ComparePreset is { } other
			? OverlayLayers.Drawn(other.Elements, other.Layers)
			: OverlayLayers.Drawn(ActiveElements, ActiveLayers);
	}

	/// <summary>The presets the active one can be compared with - all the others - after the list or the active preset changed.</summary>
	private void RefreshCompareChoices()
	{
		_syncingCompare = true;
		List<PresetOption> choices = [.. _overlayPresets.Where(p => p.Id != _activePresetId).Select(p => new PresetOption(p.Id, p.Name))];
		CompareComboBox.ItemsSource = choices;
		int index = choices.FindIndex(c => c.Id == _comparePresetId);
		CompareComboBox.SelectedIndex = index;
		_syncingCompare = false;

		if (index < 0)
		{
			_comparePresetId = null;
			_comparing = false;
		}

		UpdateCompareButton();
	}

	private void OnCompareSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_syncingCompare) return;

		_comparePresetId = (CompareComboBox.SelectedItem as PresetOption)?.Id;
		if (_comparing) ShowLayout();
		UpdateCompareButton();
	}

	private void OnCompareClick(object? sender, RoutedEventArgs e)
	{
		ToggleCompare();
	}

	private void ToggleCompare()
	{
		if (_comparing)
		{
			_comparing = false;
		}
		else
		{
			if (ComparePreset is not { } other || _summary is null) return;

			_comparing = true;
			AppendLog(string.Format(Strings.Compare_Log, other.Name));
		}

		ShowLayout();
		UpdateCompareButton();
	}

	/// <summary>Back to the active preset when the canvas is used: its widgets are the ones that can be picked and dragged, not the ones shown.</summary>
	private void EndCompare()
	{
		if (!_comparing) return;

		StopCompare();
		ShowLayout();
	}

	/// <summary>Ends the comparison (an edit or a switch is about to change what the preview should show). The caller shows the layout.</summary>
	private void StopCompare()
	{
		if (!_comparing) return;

		_comparing = false;
		UpdateCompareButton();
	}

	private void UpdateCompareButton()
	{
		CompareButton.IsEnabled = ComparePreset is not null && _summary is not null;
		CompareButton.Content = _comparing ? Strings.Compare_Back : Strings.Compare_Show;
	}
}
