using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     A widget's drop shadow (OverlayElement.Shadow*), made in BeginElement: the widget is drawn into a layer whose image
///     filter casts the shadow from its outline and keeps it only outside the widget, so a translucent panel isn't darkened
///     twice. It's the only shadow a widget has (the text's own outline shadow aside), so switching it off leaves none.
/// </summary>
public sealed partial class OverlayRenderer
{
	public const float ShadowOpacityDefault = 0.6f;
	// Text and bars; the round widgets and images are bigger, so their shadow is too (see their constructors).
	public const float ShadowRadiusDefault = 6f;
	public const float ShadowRadiusMedium = 10f;
	public const float ShadowRadiusLarge = 20f;
	public const float ShadowRadiusMax = 60f;
	public const float ShadowOffsetXDefault = 0f;
	public const float ShadowOffsetYDefault = 0f;
	public const float ShadowOffsetMax = 60f;

	// Multiplies a pixel's alpha so that anything the widget drew on at all (a panel at 5% included) counts as solid.
	private const float FootprintAlphaGain = 40f;
	private const int ShadowFilterCacheMax = 32;

	/// <summary>A widget's shadow as drawn: the opacity is already in the color's alpha, the radius is three of its sigmas.</summary>
	private readonly record struct ShadowStyle(SKColor Color, float Sigma, float OffsetX, float OffsetY);

	private readonly SKPaint _shadowPaint = new();

	// One filter per distinct look, kept for the renderer's lifetime like the blur mask filters: the same few are asked for
	// every frame. Editing a shadow's values makes a new look at each step, so the cache starts over when it grows.
	private readonly Dictionary<ShadowStyle, SKImageFilter> _shadowFilters = [];

	/// <summary>Off: no widget gets its shadow - the preview's light mode (OverlaySettings.PreviewShadows), never a render.</summary>
	public bool DrawShadows { get; set; } = true;

	/// <summary>A shadow at 0% opacity is invisible, so it costs nothing: no layer, no filter.</summary>
	private bool IsShadowed(OverlayElement element)
	{
		return DrawShadows && element.ShadowEnabled && element.ShadowOpacity > 0f;
	}

	private static ShadowStyle ShadowStyleOf(OverlayElement element)
	{
		SKColor color = ResolveColor(element.ShadowColor, SKColors.Black);
		float opacity = Math.Clamp(element.ShadowOpacity, 0f, 1f);
		return new ShadowStyle(color.WithAlpha((byte)Math.Round(color.Alpha * opacity)),
			Math.Clamp(element.ShadowRadius, 0f, ShadowRadiusMax) / 3f,
			Math.Clamp(element.ShadowOffsetX, -ShadowOffsetMax, ShadowOffsetMax),
			Math.Clamp(element.ShadowOffsetY, -ShadowOffsetMax, ShadowOffsetMax));
	}

	/// <summary>
	///     What a layer has to cover for a widget that draws `content`: it and where its shadow reaches (its radius plus the
	///     offset). A whole-frame layer would run the blur over the whole frame for every shadowed widget.
	/// </summary>
	private static SKRect ShadowArea(ShadowStyle style, SKRect content)
	{
		float reach = style.Sigma * 3f + 2f;
		return new SKRect(content.Left + Math.Min(0f, style.OffsetX) - reach, content.Top + Math.Min(0f, style.OffsetY) - reach,
			content.Right + Math.Max(0f, style.OffsetX) + reach, content.Bottom + Math.Max(0f, style.OffsetY) + reach);
	}

	/// <summary>
	///     Opens the layer the shadow is made from, inside the fade's layer so the shadow fades with the widget. Not part of
	///     MeasureElement (it draws through DrawElement, not BeginElement): hover and selection frame the widget itself.
	/// </summary>
	private void BeginShadow(SKCanvas canvas, ShadowStyle style, SKRect area)
	{
		_shadowPaint.ImageFilter = ShadowFilter(style);
		canvas.SaveLayer(area, _shadowPaint);
		_shadowPaint.ImageFilter = null;
	}

	private SKImageFilter ShadowFilter(ShadowStyle style)
	{
		if (_shadowFilters.TryGetValue(style, out SKImageFilter? filter)) return filter;

		if (_shadowFilters.Count >= ShadowFilterCacheMax) DisposeShadowFilters();
		filter = CreateShadowFilter(style);
		_shadowFilters[style] = filter;
		return filter;
	}

	/// <summary>
	///     The footprint is the widget with every drawn pixel made solid. The shadow is cast from it - so a translucent panel
	///     casts as strong a shadow as an opaque one - and cut out under it, then the widget goes on top.
	/// </summary>
	private static SKImageFilter CreateShadowFilter(ShadowStyle style)
	{
		using var solid = SKColorFilter.CreateColorMatrix([
			1, 0, 0, 0, 0,
			0, 1, 0, 0, 0,
			0, 0, 1, 0, 0,
			0, 0, 0, FootprintAlphaGain, 0
		]);
		using var footprint = SKImageFilter.CreateColorFilter(solid);
		using var cast = SKImageFilter.CreateDropShadowOnly(style.OffsetX, style.OffsetY, style.Sigma, style.Sigma, style.Color, footprint);
		using var outside = SKImageFilter.CreateBlendMode(SKBlendMode.DstOut, cast, footprint);
		return SKImageFilter.CreateMerge(outside, null);
	}

	private void DisposeShadowFilters()
	{
		foreach (SKImageFilter filter in _shadowFilters.Values) filter.Dispose();
		_shadowFilters.Clear();
	}
}
