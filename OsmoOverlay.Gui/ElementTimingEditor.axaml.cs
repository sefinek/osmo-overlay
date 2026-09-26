using Avalonia.Controls;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>OverlayElement's timing fields as edited - OutAnimationType null: no way out (ElementAnimation).</summary>
public sealed record ElementTiming(
	double? AppearAtSeconds,
	double? DisappearAtSeconds,
	OverlayAnimationType AnimationType,
	double AnimationDurationSeconds,
	OverlayAnimationType? OutAnimationType,
	double? OutAnimationDurationSeconds);

/// <summary>
///     Appear at/Disappear at and the ways in and out - identical for every widget type (see ElementAnimation), so one
///     control per widget settings panel instead of the same block of named controls duplicated per widget.
/// </summary>
public partial class ElementTimingEditor : UserControl
{
	// Named for what the viewer sees - OverlayAnimationType's slide is the way the widget moves (SlideUp comes in from
	// below, and goes out upward).
	private static readonly List<AnimationOption> InOptions =
	[
		new("None (instant)", OverlayAnimationType.None),
		new("Fade in", OverlayAnimationType.Fade),
		new("Slide in from below", OverlayAnimationType.SlideUp),
		new("Slide in from above", OverlayAnimationType.SlideDown),
		new("Slide in from the right", OverlayAnimationType.SlideLeft),
		new("Slide in from the left", OverlayAnimationType.SlideRight)
	];

	private static readonly List<AnimationOption> OutOptions =
	[
		new("None (instant)", OverlayAnimationType.None),
		new("Fade out", OverlayAnimationType.Fade),
		new("Slide out upward", OverlayAnimationType.SlideUp),
		new("Slide out downward", OverlayAnimationType.SlideDown),
		new("Slide out to the left", OverlayAnimationType.SlideLeft),
		new("Slide out to the right", OverlayAnimationType.SlideRight)
	];

	private bool _populating;

	public ElementTimingEditor()
	{
		InitializeComponent();
		// mm:ss.fff like the preview's time readout, not seconds with one decimal - a frame is 17-33 ms.
		AppearAtBox.TextConverter = TimeTextConverter.Instance;
		DisappearAtBox.TextConverter = TimeTextConverter.Instance;
		foreach (NumericUpDown box in new[] { InDurationBox, OutDurationBox })
		{
			box.Minimum = (decimal)OverlayRenderer.AnimationDurationSecondsMin;
			box.Maximum = (decimal)OverlayRenderer.AnimationDurationSecondsMax;
		}

		InAnimationCombo.ItemsSource = InOptions;
		OutAnimationCombo.ItemsSource = OutOptions;

		AppearAtBox.ValueChanged += (_, _) => OnChanged();
		DisappearAtBox.ValueChanged += (_, _) => OnChanged();
		InAnimationCombo.SelectionChanged += (_, _) => OnChanged();
		InDurationBox.ValueChanged += (_, _) => OnChanged();
		OutAnimationCombo.SelectionChanged += (_, _) => OnChanged();
		OutDurationBox.ValueChanged += (_, _) => OnChanged();
	}

	/// <summary>Raised on user edits only, not while Populate fills the controls.</summary>
	public event Action<ElementTiming>? TimingChanged;

	private OverlayAnimationType InType => (InAnimationCombo.SelectedItem as AnimationOption)?.Value ?? OverlayAnimationType.None;
	private OverlayAnimationType OutType => (OutAnimationCombo.SelectedItem as AnimationOption)?.Value ?? OverlayAnimationType.None;

	public void Populate(OverlayElement element)
	{
		_populating = true;
		AppearAtBox.Value = (decimal?)element.AppearAtSeconds;
		DisappearAtBox.Value = (decimal?)element.DisappearAtSeconds;
		InAnimationCombo.SelectedItem = Option(InOptions, element.AnimationType);
		InDurationBox.Value = (decimal)element.AnimationDurationSeconds;
		OutAnimationCombo.SelectedItem = Option(OutOptions, ElementAnimation.OutType(element.OutAnimationType));
		OutDurationBox.Value = (decimal)ElementAnimation.OutDuration(element.OutAnimationDurationSeconds);
		// Set explicitly (not left to SelectionChanged) - re-selecting the already selected option doesn't
		// raise it, which would leave the visibility from whichever element was shown before.
		UpdateDurationPanels();
		_populating = false;
	}

	private static AnimationOption Option(List<AnimationOption> options, OverlayAnimationType type)
	{
		return options.FirstOrDefault(o => o.Value == type) ?? options[0];
	}

	private void UpdateDurationPanels()
	{
		InDurationPanel.IsVisible = InType != OverlayAnimationType.None;
		OutDurationPanel.IsVisible = OutType != OverlayAnimationType.None;
	}

	private void OnChanged()
	{
		UpdateDurationPanels();
		if (_populating) return;

		var inLength = InDurationBox.Value is { } inValue ? (double)inValue : OverlayRenderer.AnimationDurationSecondsDefault;
		// No way out is stored as none at all, like a widget that never had one.
		OverlayAnimationType? outType = OutType == OverlayAnimationType.None ? null : OutType;
		double? outLength = outType is not null && OutDurationBox.Value is { } outValue ? (double)outValue : null;
		TimingChanged?.Invoke(new ElementTiming((double?)AppearAtBox.Value, (double?)DisappearAtBox.Value, InType, inLength, outType,
			outLength));
	}

	private sealed record AnimationOption(string Display, OverlayAnimationType Value)
	{
		public override string ToString()
		{
			return Display;
		}
	}
}
