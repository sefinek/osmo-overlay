using Avalonia.Controls;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>OverlayElement's shadow fields as edited - Opacity 0..1, Radius/Offset in the 4K reference space.</summary>
public sealed record ElementShadow(bool Enabled, string? Color, float Opacity, float Radius, float OffsetX, float OffsetY);

/// <summary>Drop shadow switch and its color/opacity/radius/offset - identical for every widget type, one control per settings panel.</summary>
public partial class ElementShadowEditor : UserControl
{
	private bool _populating;
	private string? _appliedColor;

	public ElementShadowEditor()
	{
		InitializeComponent();
		RadiusBox.Maximum = (decimal)OverlayRenderer.ShadowRadiusMax;
		OffsetXBox.Minimum = -(decimal)OverlayRenderer.ShadowOffsetMax;
		OffsetXBox.Maximum = (decimal)OverlayRenderer.ShadowOffsetMax;
		OffsetYBox.Minimum = -(decimal)OverlayRenderer.ShadowOffsetMax;
		OffsetYBox.Maximum = (decimal)OverlayRenderer.ShadowOffsetMax;

		EnabledCheck.IsCheckedChanged += (_, _) => OnChanged();
		OpacityBox.ValueChanged += (_, _) => OnChanged();
		RadiusBox.ValueChanged += (_, _) => OnChanged();
		OffsetXBox.ValueChanged += (_, _) => OnChanged();
		OffsetYBox.ValueChanged += (_, _) => OnChanged();
		// LostFocus, not TextChanged: a half-typed hex would otherwise re-render the preview per keystroke.
		ColorBox.LostFocus += (_, _) =>
		{
			if (EnteredColor() != _appliedColor) OnChanged();
		};
		ApplyToAllButton.Click += (_, _) => ApplyToAllRequested?.Invoke();
	}

	/// <summary>Raised on user edits only, not while Populate fills the controls.</summary>
	public event Action<ElementShadow>? ShadowChanged;

	public event Action? ApplyToAllRequested;

	/// <summary>
	///     Shadows turned off everywhere (OverlaySettings.DisableShadows): the widget's own values are kept but can't be edited,
	///     and a line says why.
	/// </summary>
	public void SetLocked(bool locked)
	{
		LockedHint.IsVisible = locked;
		EnabledCheck.IsEnabled = !locked;
		OptionsPanel.IsEnabled = !locked;
	}

	public void Populate(OverlayElement element)
	{
		_populating = true;
		EnabledCheck.IsChecked = element.ShadowEnabled;
		ColorBox.Text = element.ShadowColor;
		_appliedColor = EnteredColor();
		OpacityBox.Value = (decimal)Math.Round(element.ShadowOpacity * 100);
		RadiusBox.Value = (decimal)element.ShadowRadius;
		OffsetXBox.Value = (decimal)element.ShadowOffsetX;
		OffsetYBox.Value = (decimal)element.ShadowOffsetY;
		UpdateState();
		_populating = false;
	}

	private void UpdateState()
	{
		OptionsPanel.IsVisible = EnabledCheck.IsChecked == true;
		ColorSwatch.Update(ColorPreview, ColorBox.Text, "#000000");
	}

	private string? EnteredColor()
	{
		return string.IsNullOrWhiteSpace(ColorBox.Text) ? null : ColorBox.Text.Trim();
	}

	private void OnChanged()
	{
		UpdateState();
		if (_populating) return;

		_appliedColor = EnteredColor();

		ShadowChanged?.Invoke(new ElementShadow(
			EnabledCheck.IsChecked == true,
			_appliedColor,
			OpacityBox.Value is { } opacity ? (float)opacity / 100f : OverlayRenderer.ShadowOpacityDefault,
			RadiusBox.Value is { } radius ? (float)radius : OverlayRenderer.ShadowRadiusDefault,
			OffsetXBox.Value is { } x ? (float)x : OverlayRenderer.ShadowOffsetXDefault,
			OffsetYBox.Value is { } y ? (float)y : OverlayRenderer.ShadowOffsetYDefault));
	}
}
