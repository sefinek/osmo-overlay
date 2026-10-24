using Avalonia.Controls;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>IPanelElement's fields as edited - Opacity 0..1.</summary>
public sealed record ElementPanel(string? Color, float Opacity);

/// <summary>The fill of a widget's panel - color and opacity - for the widgets drawn on one (IPanelElement).</summary>
public partial class ElementPanelEditor : UserControl
{
	private bool _populating;
	private string? _appliedColor;

	public ElementPanelEditor()
	{
		InitializeComponent();

		OpacityBox.ValueChanged += (_, _) => OnChanged();
		// LostFocus, not TextChanged: a half-typed hex would otherwise re-render the preview per keystroke.
		ColorBox.LostFocus += (_, _) =>
		{
			if (EnteredColor() != _appliedColor) OnChanged();
		};
		ApplyToAllButton.Click += (_, _) => ApplyToAllRequested?.Invoke();
	}

	/// <summary>Raised on user edits only, not while Populate fills the controls.</summary>
	public event Action<ElementPanel>? PanelChanged;

	public event Action? ApplyToAllRequested;

	public void Populate(IPanelElement element)
	{
		_populating = true;
		ColorBox.Text = element.PanelColor;
		_appliedColor = EnteredColor();
		OpacityBox.Value = (decimal)Math.Round(element.PanelOpacity * 100);
		ColorSwatch.Update(ColorPreview, ColorBox.Text, "#000000");
		_populating = false;
	}

	private string? EnteredColor()
	{
		return string.IsNullOrWhiteSpace(ColorBox.Text) ? null : ColorBox.Text.Trim();
	}

	private void OnChanged()
	{
		ColorSwatch.Update(ColorPreview, ColorBox.Text, "#000000");
		if (_populating) return;

		_appliedColor = EnteredColor();

		PanelChanged?.Invoke(new ElementPanel(
			_appliedColor,
			OpacityBox.Value is { } opacity ? (float)opacity / 100f : OverlayRenderer.PanelOpacityDefault));
	}
}
