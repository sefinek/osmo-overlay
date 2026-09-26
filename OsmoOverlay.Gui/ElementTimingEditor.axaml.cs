using Avalonia.Controls;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

public sealed record ElementTiming(
	double? AppearAtSeconds,
	double? DisappearAtSeconds,
	OverlayAnimationType AnimationType,
	double AnimationDurationSeconds);

/// <summary>
///     Appear at/Disappear at/Animation/Duration - identical for every widget type (see
///     OverlayRenderer.Animation.cs), so one control per widget settings panel instead of the same
///     block of named controls duplicated per widget.
/// </summary>
public partial class ElementTimingEditor : UserControl
{
	private static readonly List<AnimationOption> AnimationOptions =
	[
		new("None (instant)", OverlayAnimationType.None),
		new("Fade", OverlayAnimationType.Fade),
		new("Slide up", OverlayAnimationType.SlideUp),
		new("Slide down", OverlayAnimationType.SlideDown),
		new("Slide left", OverlayAnimationType.SlideLeft),
		new("Slide right", OverlayAnimationType.SlideRight)
	];

	private bool _populating;

	public ElementTimingEditor()
	{
		InitializeComponent();
		AnimationCombo.ItemsSource = AnimationOptions;

		AppearAtBox.ValueChanged += (_, _) => OnChanged();
		DisappearAtBox.ValueChanged += (_, _) => OnChanged();
		AnimationCombo.SelectionChanged += (_, _) => OnChanged();
		AnimationDurationBox.ValueChanged += (_, _) => OnChanged();
	}

	/// <summary>Raised on user edits only, not while Populate fills the controls.</summary>
	public event Action<ElementTiming>? TimingChanged;

	private OverlayAnimationType SelectedAnimation =>
		(AnimationCombo.SelectedItem as AnimationOption)?.Value ?? OverlayAnimationType.None;

	public void Populate(OverlayElement element)
	{
		_populating = true;
		AppearAtBox.Value = (decimal?)element.AppearAtSeconds;
		DisappearAtBox.Value = (decimal?)element.DisappearAtSeconds;
		AnimationCombo.SelectedItem = AnimationOptions.FirstOrDefault(o => o.Value == element.AnimationType) ?? AnimationOptions[0];
		AnimationDurationBox.Value = (decimal)element.AnimationDurationSeconds;
		// Set explicitly (not left to SelectionChanged) - re-selecting the already selected option doesn't
		// raise it, which would leave the visibility from whichever element was shown before.
		AnimationDurationPanel.IsVisible = element.AnimationType != OverlayAnimationType.None;
		_populating = false;
	}

	private void OnChanged()
	{
		OverlayAnimationType animation = SelectedAnimation;
		AnimationDurationPanel.IsVisible = animation != OverlayAnimationType.None;
		if (_populating) return;

		TimingChanged?.Invoke(new ElementTiming(
			(double?)AppearAtBox.Value,
			(double?)DisappearAtBox.Value,
			animation,
			AnimationDurationBox.Value is { } duration ? (double)duration : OverlayRenderer.AnimationDurationSecondsDefault));
	}

	private sealed record AnimationOption(string Display, OverlayAnimationType Value)
	{
		public override string ToString()
		{
			return Display;
		}
	}
}
