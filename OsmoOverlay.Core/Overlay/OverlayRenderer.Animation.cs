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

	public const float ShadowOpacityDefault = 0.5f;
	// Text and bars; the round widgets and images are bigger, so their shadow is too (see their constructors).
	public const float ShadowBlurDefault = 4f;
	public const float ShadowBlurMedium = 6f;
	public const float ShadowBlurLarge = 8f;
	public const float ShadowBlurMax = 60f;
	public const float ShadowOffsetXDefault = 0f;
	public const float ShadowOffsetYDefault = 0f;
	public const float ShadowOffsetMax = 60f;

	private readonly SKPaint _shadowPaint = new();

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
	private int BeginElement(SKCanvas canvas, OverlayElement element, ElementState state, SKRect? shadowContent = null)
	{
		int saveCount = canvas.Save();
		(float offsetX, float offsetY) = SlideOffset(state);
		(float anchorX, float anchorY) = OverlayElementBounds.ToPixels(element.X, element.Y, _width, _height);
		canvas.Translate(anchorX + offsetX, anchorY + offsetY);
		float scale = _scale * element.Scale;
		canvas.Scale(scale, scale);
		if (state.Progress < 1f) canvas.SaveLayer(AlphaPaint(state.Progress));
		if (element.ShadowEnabled && shadowContent is { } content) BeginShadow(canvas, element, content);
		return saveCount;
	}

	/// <summary>
	///     Opens the layer a widget's drop shadow is made from, inside the fade's layer so the shadow fades with the widget.
	///     The layer covers only `content` (what the widget draws, ContentBounds) and where the shadow reaches - a whole-frame
	///     layer would blur the whole frame for every shadowed widget. Not part of MeasureElement (it draws through
	///     DrawElement, not BeginElement): hover and selection frame the widget itself, not its shadow.
	/// </summary>
	private void BeginShadow(SKCanvas canvas, OverlayElement element, SKRect content)
	{
		float offsetX = Math.Clamp(element.ShadowOffsetX, -ShadowOffsetMax, ShadowOffsetMax);
		float offsetY = Math.Clamp(element.ShadowOffsetY, -ShadowOffsetMax, ShadowOffsetMax);
		float sigma = Math.Clamp(element.ShadowBlur, 0f, ShadowBlurMax) / 2f;

		_shadowPaint.ImageFilter = ShadowFilter(ResolveColor(element.ShadowColor, SKColors.Black), Math.Clamp(element.ShadowOpacity, 0f, 1f), sigma, offsetX, offsetY);
		float reach = sigma * 3f + 2f;
		canvas.SaveLayer(new SKRect(content.Left + Math.Min(0f, offsetX) - reach, content.Top + Math.Min(0f, offsetY) - reach,
			content.Right + Math.Max(0f, offsetX) + reach, content.Bottom + Math.Max(0f, offsetY) + reach), _shadowPaint);
		_shadowPaint.ImageFilter = null;
	}

	private readonly record struct ShadowKey(SKColor Color, float Opacity, float Sigma, float OffsetX, float OffsetY);

	// A drop shadow filter per distinct look, kept for the renderer's lifetime like the blur mask filters: the same few are
	// asked for every frame. Editing a shadow's values makes a new look each step, so the cache starts over when it grows.
	private readonly Dictionary<ShadowKey, SKImageFilter> _shadowFilters = [];
	private const int ShadowFilterCacheMax = 32;

	private SKImageFilter ShadowFilter(SKColor color, float opacity, float sigma, float offsetX, float offsetY)
	{
		var key = new ShadowKey(color, opacity, sigma, offsetX, offsetY);
		if (_shadowFilters.TryGetValue(key, out SKImageFilter? filter)) return filter;

		if (_shadowFilters.Count >= ShadowFilterCacheMax) DisposeShadowFilters();
		filter = SKImageFilter.CreateDropShadow(offsetX, offsetY, sigma, sigma, color.WithAlpha((byte)Math.Round(color.Alpha * opacity)));
		_shadowFilters[key] = filter;
		return filter;
	}

	private void DisposeShadowFilters()
	{
		foreach (SKImageFilter filter in _shadowFilters.Values) filter.Dispose();
		_shadowFilters.Clear();
	}

	/// <summary>
	///     Canvas-space (X, Y) translation - see OverlayAnimationType: coming in, a slide starts off to the side it enters from;
	///     going out, it ends up off to the side it moves toward.
	/// </summary>
	private (float X, float Y) SlideOffset(ElementState state)
	{
		float d = (1f - state.Progress) * SlideDistance * _scale * (state.Leaving ? -1 : 1);
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
