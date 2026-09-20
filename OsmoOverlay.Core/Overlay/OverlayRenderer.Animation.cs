using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>Per-widget appear/disappear timing and animation (see OverlayElement.AppearAtSeconds etc.) - the generic wrapper DrawWidgets routes every widget's draw call through.</summary>
public sealed partial class OverlayRenderer
{
	public const double AnimationDurationSecondsDefault = 0.6;

	// How far (at the 4K reference resolution - see OverlayElementBounds) a sliding widget travels from
	// its resting position at progress 0. Scaled by _scale like every other layout constant, so it reads
	// as the same proportional distance at any actual render resolution.
	private const float SlideDistance = 90f;

	/// <summary>
	///     Gates and (for Fade/Slide types) fades `draw` by the element's AppearAtSeconds/
	///     DisappearAtSeconds/AnimationType - skipped entirely (draw called directly, no SaveLayer) for
	///     the common case of a widget with none of this set, so the overwhelming majority of renders pay
	///     zero cost for a feature they don't use.
	/// </summary>
	private void DrawElement(SKCanvas canvas, OverlayElement element, double sampleTimeSeconds, Action<SKCanvas> draw)
	{
		if (element.AppearAtSeconds is null && element.DisappearAtSeconds is null &&
		    element.AnimationType == OverlayAnimationType.None)
		{
			draw(canvas);
			return;
		}

		var progress = ElementProgress(element, sampleTimeSeconds);
		if (progress <= 0f) return;

		var (offsetX, offsetY) = SlideOffset(element.AnimationType, progress);
		if (offsetX == 0f && offsetY == 0f)
		{
			DrawWithAlpha(canvas, progress, draw);
			return;
		}

		canvas.Save();
		canvas.Translate(offsetX, offsetY);
		DrawWithAlpha(canvas, progress, draw);
		canvas.Restore();
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
