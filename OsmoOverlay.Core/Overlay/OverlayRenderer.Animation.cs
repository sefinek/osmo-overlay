using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>Per-widget placement, appear/disappear timing and animation (see OverlayElement.AppearAtSeconds etc.) - BeginElement is what DrawWidgets sets every widget's draw up with.</summary>
public sealed partial class OverlayRenderer
{
	public const double AnimationDurationSecondsDefault = 0.6;

	// How far (at the 4K reference resolution - see OverlayElementBounds) a sliding widget travels from
	// its resting position at progress 0. Scaled by _scale like every other layout constant, so it reads
	// as the same proportional distance at any actual render resolution.
	private const float SlideDistance = 90f;

	/// <summary>
	///     Sets the canvas up for one widget and returns the save count to restore it to: the origin on the widget's
	///     anchor (element.X/Y, so every widget draws around (0, 0)), scaled by the resolution's scale times the
	///     widget's own element.Scale (pivoted on the anchor, so resizing never shifts it), slid and faded by
	///     `progress` (ElementProgress). The fade's SaveLayer only exists while a widget is fading in or out - a
	///     widget with no timing set, or between its two ramps, draws straight onto the frame.
	/// </summary>
	private int BeginElement(SKCanvas canvas, OverlayElement element, float progress)
	{
		var saveCount = canvas.Save();
		var (offsetX, offsetY) = SlideOffset(element.AnimationType, progress);
		canvas.Translate(element.X + offsetX, element.Y + offsetY);
		var scale = _scale * element.Scale;
		canvas.Scale(scale, scale);
		if (progress < 1f) canvas.SaveLayer(AlphaPaint(progress));
		return saveCount;
	}

	/// <summary>
	///     0..1 visibility/opacity at `sampleTimeSeconds`. AnimationType.None is a hard cut (1 for the
	///     whole [AppearAtSeconds, DisappearAtSeconds) window, 0 outside it) - Fade/Slide types ramp over
	///     AnimationDurationSeconds at each edge instead, reusing the same FadeAlpha the watermark's own
	///     fade already relies on. fadeOutStart is clamped to never sit earlier than fadeInEnd, so an
	///     appear/disappear window shorter than two animation durations still shows *something* instead
	///     of the two ramps crossing and inverting.
	/// </summary>
	private static float ElementProgress(OverlayElement element, double sampleTimeSeconds)
	{
		if (element.AppearAtSeconds is null && element.DisappearAtSeconds is null && element.AnimationType == OverlayAnimationType.None)
			return 1f;

		var appearAt = element.AppearAtSeconds ?? 0;
		var disappearAt = element.DisappearAtSeconds ?? double.PositiveInfinity;

		if (element.AnimationType == OverlayAnimationType.None)
			return sampleTimeSeconds >= appearAt && sampleTimeSeconds < disappearAt ? 1f : 0f;

		var duration = Math.Max(element.AnimationDurationSeconds, 0.05);
		var fadeInEnd = appearAt + duration;
		var fadeOutStart = Math.Max(disappearAt - duration, fadeInEnd);
		return FadeAlpha(sampleTimeSeconds, appearAt, fadeInEnd, fadeOutStart, disappearAt);
	}

	/// <summary>Canvas-space (X, Y) translation at the given progress - see OverlayAnimationType for what each direction enters from.</summary>
	private (float X, float Y) SlideOffset(OverlayAnimationType type, float progress)
	{
		var d = (1f - progress) * SlideDistance * _scale;
		return type switch
		{
			OverlayAnimationType.SlideUp => (0f, d),
			OverlayAnimationType.SlideDown => (0f, -d),
			OverlayAnimationType.SlideLeft => (d, 0f),
			OverlayAnimationType.SlideRight => (-d, 0f),
			_ => (0f, 0f)
		};
	}
}
