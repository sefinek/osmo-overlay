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
		new(Strings.Timing_AnimNone, OverlayAnimationType.None),
		new(Strings.Timing_FadeIn, OverlayAnimationType.Fade),
		new(Strings.Timing_SlideInFromBelow, OverlayAnimationType.SlideUp),
		new(Strings.Timing_SlideInFromAbove, OverlayAnimationType.SlideDown),
		new(Strings.Timing_SlideInFromRight, OverlayAnimationType.SlideLeft),
		new(Strings.Timing_SlideInFromLeft, OverlayAnimationType.SlideRight)
	];

	private static readonly List<AnimationOption> OutOptions =
	[
		new(Strings.Timing_AnimNone, OverlayAnimationType.None),
		new(Strings.Timing_FadeOut, OverlayAnimationType.Fade),
		new(Strings.Timing_SlideOutUp, OverlayAnimationType.SlideUp),
		new(Strings.Timing_SlideOutDown, OverlayAnimationType.SlideDown),
		new(Strings.Timing_SlideOutLeft, OverlayAnimationType.SlideLeft),
		new(Strings.Timing_SlideOutRight, OverlayAnimationType.SlideRight)
	];

	private bool _populating;
	// Appear at set by something else (LockAppear): shown, not edited, and the widget's own value passed on untouched.
	private bool _appearLocked;
	private double? _ownAppear;
	// Disappear at set by something else (LockDisappear), the same way.
	private bool _disappearLocked;
	private double? _ownDisappear;
	// How much later than its own times the widget shows (the opening after the route overview card): the boxes show the times it
	// really shows at, and an edit is stored without the shift. What the boxes showed when filled is `_shown*` - a box still
	// at that value hands the widget's own back, so a change of something else never turns "Start" into a time.
	private double _shift;
	private decimal? _shownAppear, _shownDisappear;

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

	/// <param name="shift">How much later than its own times the widget shows (OverlayRenderer.IntroShiftSeconds).</param>
	public void Populate(OverlayElement element, double shift = 0)
	{
		_populating = true;
		_shift = shift;
		_ownAppear = element.AppearAtSeconds;
		if (!_appearLocked) ShowAppear();
		_ownDisappear = element.DisappearAtSeconds;
		if (!_disappearLocked) ShowDisappear();
		InAnimationCombo.SelectedItem = Option(InOptions, element.AnimationType);
		InDurationBox.Value = (decimal)element.AnimationDurationSeconds;
		OutAnimationCombo.SelectedItem = Option(OutOptions, ElementAnimation.OutType(element.OutAnimationType));
		OutDurationBox.Value = (decimal)ElementAnimation.OutDuration(element.OutAnimationDurationSeconds);
		// Set explicitly (not left to SelectionChanged) - re-selecting the already selected option doesn't
		// raise it, which would leave the visibility from whichever element was shown before.
		UpdateDurationPanels();
		_populating = false;
	}

	/// <summary>
	///     Appear at decided by something else (the trip progress bar's speed gate): `seconds` is shown, the box locked (null
	///     for never); unlocked gives the box back with the widget's own value. Call after Populate.
	/// </summary>
	public void LockAppear(bool locked, double? seconds)
	{
		_populating = true;
		_appearLocked = locked;
		AppearAtBox.IsEnabled = !locked;
		if (locked) AppearAtBox.Value = (decimal?)seconds;
		else ShowAppear();
		_populating = false;
	}

	/// <summary>
	///     Disappear at decided by something else (the trip progress bar leaving after the finish): `seconds` is shown, the box
	///     locked; unlocked gives the box back with the widget's own value. Call after Populate.
	/// </summary>
	public void LockDisappear(bool locked, double? seconds)
	{
		_populating = true;
		_disappearLocked = locked;
		DisappearAtBox.IsEnabled = !locked;
		if (locked) DisappearAtBox.Value = (decimal?)seconds;
		else ShowDisappear();
		_populating = false;
	}

	/// <summary>
	///     When anything can first be seen: the end of the route overview card, which covers the video (and every widget on it)
	///     until then - a widget showing from the start is really shown from here. Set before Populate; 0 without a card.
	/// </summary>
	public double VisibleFromSeconds { get; set; }

	// A widget with no appear time shows from the start - after the shift, that is the shift - and none shows before the card ends.
	private void ShowAppear()
	{
		double? appear = _shift > 0 ? (_ownAppear ?? 0) + _shift : _ownAppear;
		if (VisibleFromSeconds > 0) appear = Math.Max(appear ?? 0, VisibleFromSeconds);
		AppearAtBox.Value = _shownAppear = (decimal?)appear;
	}

	private void ShowDisappear()
	{
		DisappearAtBox.Value = _shownDisappear = (decimal?)(_ownDisappear + _shift);
	}

	private double? Stored(decimal? box, decimal? shown, double? own)
	{
		if (box == shown) return own;

		return box is { } value ? Math.Max(0, (double)value - _shift) : null;
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

		double inLength = InDurationBox.Value is { } inValue ? (double)inValue : OverlayRenderer.AnimationDurationSecondsDefault;
		// No way out is stored as none at all, like a widget that never had one.
		OverlayAnimationType? outType = OutType == OverlayAnimationType.None ? null : OutType;
		double? outLength = outType is not null && OutDurationBox.Value is { } outValue ? (double)outValue : null;
		double? appear = _appearLocked ? _ownAppear : Stored(AppearAtBox.Value, _shownAppear, _ownAppear);
		double? disappear = _disappearLocked ? _ownDisappear : Stored(DisappearAtBox.Value, _shownDisappear, _ownDisappear);
		TimingChanged?.Invoke(new ElementTiming(appear, disappear, InType, inLength, outType,
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
