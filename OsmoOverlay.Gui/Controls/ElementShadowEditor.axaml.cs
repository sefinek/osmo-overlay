using Avalonia.Controls;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>OverlayElement's shadow fields as edited - Opacity 0..1, Blur/Offset in the 4K reference space.</summary>
public sealed record ElementShadow(bool Enabled, string? Color, float Opacity, float Blur, float OffsetX, float OffsetY);

/// <summary>Drop shadow switch and its color/opacity/blur/offset - identical for every widget type, one control per settings panel.</summary>
public partial class ElementShadowEditor : UserControl
{
	private bool _populating;

	public ElementShadowEditor()
	{
		InitializeComponent();
		BlurBox.Maximum = (decimal)OverlayRenderer.ShadowBlurMax;
		OffsetXBox.Minimum = -(decimal)OverlayRenderer.ShadowOffsetMax;
		OffsetXBox.Maximum = (decimal)OverlayRenderer.ShadowOffsetMax;
		OffsetYBox.Minimum = -(decimal)OverlayRenderer.ShadowOffsetMax;
		OffsetYBox.Maximum = (decimal)OverlayRenderer.ShadowOffsetMax;

		EnabledCheck.IsCheckedChanged += (_, _) => OnChanged();
		OpacityBox.ValueChanged += (_, _) => OnChanged();
		BlurBox.ValueChanged += (_, _) => OnChanged();
		OffsetXBox.ValueChanged += (_, _) => OnChanged();
		OffsetYBox.ValueChanged += (_, _) => OnChanged();
		// LostFocus, not TextChanged: a half-typed hex would otherwise re-render the preview per keystroke.
		ColorBox.LostFocus += (_, _) => OnChanged();
	}

	/// <summary>Raised on user edits only, not while Populate fills the controls.</summary>
	public event Action<ElementShadow>? ShadowChanged;

	public void Populate(OverlayElement element)
	{
		_populating = true;
		EnabledCheck.IsChecked = element.ShadowEnabled;
		ColorBox.Text = element.ShadowColor;
		OpacityBox.Value = (decimal)Math.Round(element.ShadowOpacity * 100);
		BlurBox.Value = (decimal)element.ShadowBlur;
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

	private void OnChanged()
	{
		UpdateState();
		if (_populating) return;

		ShadowChanged?.Invoke(new ElementShadow(
			EnabledCheck.IsChecked == true,
			string.IsNullOrWhiteSpace(ColorBox.Text) ? null : ColorBox.Text.Trim(),
			OpacityBox.Value is { } opacity ? (float)opacity / 100f : OverlayRenderer.ShadowOpacityDefault,
			BlurBox.Value is { } blur ? (float)blur : OverlayRenderer.ShadowBlurDefault,
			OffsetXBox.Value is { } x ? (float)x : OverlayRenderer.ShadowOffsetXDefault,
			OffsetYBox.Value is { } y ? (float)y : OverlayRenderer.ShadowOffsetYDefault));
	}
}
