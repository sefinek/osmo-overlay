using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using OsmoOverlay.Core;

namespace OsmoOverlay.Gui;

/// <summary>
///     The preview's timeline: scrubbing (Value in recording seconds, like a Slider) plus everything marked on
///     the recording - the parts cut out of the render (red, hatched, the stripes slowly moving), the In/Out
///     selection about to be cut (bracketed) and GPS signal loss (a thin strip under the track). One control
///     so all of them share one seconds-to-pixels mapping with the thumb - bands on a Canvas behind a Slider
///     never lined up exactly with its inset track.
/// </summary>
public sealed class PreviewTimeline : RangeBase, ICustomHitTest
{
	private const double Inset = 8;
	private const double TrackHeight = 6;
	private const double CutHeight = 16;
	private const double SelectionHeight = 24;
	private const double ThumbRadius = 7;
	private const double StripeSpacing = 7;
	private const double StripePixelsPerSecond = 8;

	private static readonly IBrush TrackBrush = Palette.StrokeStrong;
	private static readonly IBrush GpsLossBrush = Palette.Warning;
	private static readonly IBrush CutFill = Palette.Tint(Palette.Danger, 0.22);
	private static readonly IPen CutBorder = new Pen(Palette.Tint(Palette.Danger, 0.85));
	private static readonly IPen CutStripe = new Pen(Palette.Tint(Palette.Danger, 0.65), 2);
	private static readonly IBrush SelectionFill = Palette.Tint(Palette.Accent, 0.2);
	private static readonly IPen SelectionBracket = new Pen(Palette.Accent, 2);
	private static readonly IBrush ThumbBrush = Palette.Accent;

	private IReadOnlyList<TimeRange> _cuts = [];
	private IReadOnlyList<TimeRange> _gpsLoss = [];
	private TimeRange? _selection;
	private bool _scrubbing;
	private bool _attached;
	private bool _animating;
	private TimeSpan? _lastAnimationFrame;
	private double _stripeOffset;

	static PreviewTimeline()
	{
		AffectsRender<PreviewTimeline>(ValueProperty, MinimumProperty, MaximumProperty, IsEnabledProperty);
		FocusableProperty.OverrideDefaultValue<PreviewTimeline>(false);
	}

	public PreviewTimeline()
	{
		Cursor = new Cursor(StandardCursorType.Hand);
	}

	/// <summary>A drag on the timeline began / ended - Value keeps changing (ValueChanged) in between.</summary>
	public event Action? ScrubStarted;
	public event Action? ScrubEnded;

	public IReadOnlyList<TimeRange> Cuts
	{
		get => _cuts;
		set
		{
			_cuts = value;
			InvalidateVisual();
			StartStripeAnimation();
		}
	}

	public IReadOnlyList<TimeRange> GpsLoss
	{
		get => _gpsLoss;
		set
		{
			_gpsLoss = value;
			InvalidateVisual();
		}
	}

	public TimeRange? Selection
	{
		get => _selection;
		set
		{
			_selection = value;
			InvalidateVisual();
		}
	}

	/// <summary>The whole control takes the pointer, not just what's drawn - a click between the marks still scrubs.</summary>
	public bool HitTest(Point point)
	{
		return new Rect(Bounds.Size).Contains(point);
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		return new Size(0, SelectionHeight + 4);
	}

	public override void Render(DrawingContext context)
	{
		var height = Bounds.Height;
		var middle = height / 2;
		var trackWidth = Math.Max(0, Bounds.Width - 2 * Inset);
		using DrawingContext.PushedState opacity = context.PushOpacity(IsEffectivelyEnabled ? 1 : 0.45);

		context.DrawRectangle(TrackBrush, null, new Rect(Inset, middle - TrackHeight / 2, trackWidth, TrackHeight), 3, 3);

		foreach (TimeRange loss in _gpsLoss)
		{
			var (x1, x2) = Span(loss, 3);
			context.DrawRectangle(GpsLossBrush, null, new Rect(x1, middle + TrackHeight / 2 + 3, x2 - x1, 3), 1.5, 1.5);
		}

		foreach (TimeRange cut in _cuts) DrawCut(context, cut, middle);

		if (_selection is { } selection) DrawSelection(context, selection, middle);

		context.DrawEllipse(ThumbBrush, null, new Point(X(Value), middle), ThumbRadius, ThumbRadius);
	}

	private void DrawCut(DrawingContext context, TimeRange cut, double middle)
	{
		var (x1, x2) = Span(cut, 3);
		var band = new Rect(x1, middle - CutHeight / 2, x2 - x1, CutHeight);
		context.DrawRectangle(CutFill, null, band, 2, 2);

		using (context.PushClip(new RoundedRect(band, 2)))
		{
			// Diagonal stripes (the usual "removed" hatching), shifted by the animation offset.
			for (var x = band.Left - band.Height - StripeSpacing + _stripeOffset; x < band.Right; x += StripeSpacing)
				context.DrawLine(CutStripe, new Point(x, band.Bottom), new Point(x + band.Height, band.Top));
		}

		context.DrawRectangle(null, CutBorder, band.Deflate(0.5), 2, 2);
	}

	private void DrawSelection(DrawingContext context, TimeRange selection, double middle)
	{
		var (x1, x2) = Span(selection, 2);
		var top = middle - SelectionHeight / 2;
		var bottom = middle + SelectionHeight / 2;
		context.DrawRectangle(SelectionFill, null, new Rect(x1, top, x2 - x1, SelectionHeight), 2, 2);

		const double tick = 5;
		foreach (var (x, direction) in new[] { (x1 + 1, 1.0), (x2 - 1, -1.0) })
		{
			context.DrawLine(SelectionBracket, new Point(x, top), new Point(x, bottom));
			context.DrawLine(SelectionBracket, new Point(x, top + 1), new Point(x + direction * tick, top + 1));
			context.DrawLine(SelectionBracket, new Point(x, bottom - 1), new Point(x + direction * tick, bottom - 1));
		}
	}

	private double X(double seconds)
	{
		var range = Maximum - Minimum;
		var fraction = range > 0 ? Math.Clamp((seconds - Minimum) / range, 0, 1) : 0;
		return Inset + fraction * Math.Max(0, Bounds.Width - 2 * Inset);
	}

	/// <summary>A range in pixels, at least minWidth wide so a one-frame cut still shows.</summary>
	private (double X1, double X2) Span(TimeRange range, double minWidth)
	{
		var x1 = X(range.StartSeconds);
		return (x1, Math.Max(X(range.EndSeconds), x1 + minWidth));
	}

	private double ValueAt(double x)
	{
		var trackWidth = Bounds.Width - 2 * Inset;
		var fraction = trackWidth > 0 ? Math.Clamp((x - Inset) / trackWidth, 0, 1) : 0;
		return Minimum + fraction * (Maximum - Minimum);
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

		e.Pointer.Capture(this);
		_scrubbing = true;
		ScrubStarted?.Invoke();
		Value = ValueAt(e.GetPosition(this).X);
		e.Handled = true;
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		if (_scrubbing) Value = ValueAt(e.GetPosition(this).X);
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		if (!_scrubbing) return;

		// Cleared before releasing the capture - OnPointerCaptureLost must not end the scrub a second time.
		_scrubbing = false;
		e.Pointer.Capture(null);
		ScrubEnded?.Invoke();
	}

	protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
	{
		base.OnPointerCaptureLost(e);
		if (!_scrubbing) return;

		_scrubbing = false;
		ScrubEnded?.Invoke();
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		_attached = true;
		StartStripeAnimation();
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		_attached = false;
	}

	/// <summary>Runs on the render loop's frames only while there is a cut to draw.</summary>
	private void StartStripeAnimation()
	{
		if (_animating || !_attached || _cuts.Count == 0 || TopLevel.GetTopLevel(this) is not { } topLevel) return;

		_animating = true;
		_lastAnimationFrame = null;
		topLevel.RequestAnimationFrame(OnAnimationFrame);
	}

	private void OnAnimationFrame(TimeSpan time)
	{
		if (!_attached || _cuts.Count == 0 || TopLevel.GetTopLevel(this) is not { } topLevel)
		{
			_animating = false;
			return;
		}

		if (_lastAnimationFrame is { } last)
			_stripeOffset = (_stripeOffset + (time - last).TotalSeconds * StripePixelsPerSecond) % StripeSpacing;
		_lastAnimationFrame = time;
		InvalidateVisual();
		topLevel.RequestAnimationFrame(OnAnimationFrame);
	}
}
