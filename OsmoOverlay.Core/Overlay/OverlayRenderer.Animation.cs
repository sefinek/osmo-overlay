using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>Per-widget placement, appear/disappear timing and animation (see OverlayElement.AppearAtSeconds etc.) - BeginElement is what DrawWidgets sets every widget's draw up with.</summary>
public sealed partial class OverlayRenderer
{
	public const double AnimationDurationSecondsDefault = 0.6;
	// The longest a way in or out can be set to (the settings' boxes, the layers' handles) - a slow fade over a title or a
	// logo, not just a quick transition. Within a clip the way in and out still never overlap (ElementAnimation).
	public const double AnimationDurationSecondsMin = 0.1;
	public const double AnimationDurationSecondsMax = 30;

	/// <summary>
	///     The rendered video's length in output seconds - a widget with no DisappearAtSeconds leaves at it, so its way out
	///     ends with the video (ElementAnimation). Infinite until a caller sets it: nothing then plays a way out at the end.
	/// </summary>
	public double OutputDurationSeconds { get; set; } = double.PositiveInfinity;

	// How far (at the 4K reference resolution - see OverlayElementBounds) a sliding widget travels from
	// its resting position at progress 0. Scaled by _scale like every other layout constant, so it reads
	// as the same proportional distance at any actual render resolution.
	private const float SlideDistance = 90f;

	/// <summary>
	///     Sets the canvas up for one widget and returns the save count to restore it to: the origin on the widget's
	///     anchor (element.X/Y, so every widget draws around (0, 0)), scaled by the resolution's scale times the
	///     widget's own element.Scale (pivoted on the anchor, so resizing never shifts it), slid and faded by
	///     `state` (ElementAnimation.At). The fade's SaveLayer only exists while a widget is fading in or out - a
	///     widget with no timing set, or between its two ramps, draws straight onto the frame.
	/// </summary>
	private int BeginElement(SKCanvas canvas, OverlayElement element, ElementState state)
	{
		var saveCount = canvas.Save();
		var (offsetX, offsetY) = SlideOffset(state);
		canvas.Translate(element.X + offsetX, element.Y + offsetY);
		var scale = _scale * element.Scale;
		canvas.Scale(scale, scale);
		if (state.Progress < 1f) canvas.SaveLayer(AlphaPaint(state.Progress));
		return saveCount;
	}

	/// <summary>
	///     Canvas-space (X, Y) translation - see OverlayAnimationType: coming in, a slide starts off to the side it enters from;
	///     going out, it ends up off to the side it moves toward.
	/// </summary>
	private (float X, float Y) SlideOffset(ElementState state)
	{
		var d = (1f - state.Progress) * SlideDistance * _scale * (state.Leaving ? -1 : 1);
		return state.Animation switch
		{
			OverlayAnimationType.SlideUp => (0f, d),
			OverlayAnimationType.SlideDown => (0f, -d),
			OverlayAnimationType.SlideLeft => (d, 0f),
			OverlayAnimationType.SlideRight => (-d, 0f),
			_ => (0f, 0f)
		};
	}
}
