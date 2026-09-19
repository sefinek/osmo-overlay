using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>The "Made with OsmoOverlay" watermark and the map's required attribution slide/fallback.</summary>
public sealed partial class OverlayRenderer
{
	// Watermark holds at full opacity from frame 0, then fades out. When the Map widget is visible,
	// its required OSM credit follows as its own second slide (see MapAttributionSlideAlpha) rather
	// than being crammed into this block, so the two read as a short sequence, not a cluttered stack.
	private const double WatermarkFadeOutStartSeconds = 5.0;
	private const double WatermarkDurationSeconds = 6.0;
	private const double MapAttributionSlideDurationSeconds = 4.0;
	private const float WatermarkBottomMargin = 110f;
	private const float WatermarkLineGap = 46f;

	private static readonly string WatermarkVersion = FormatVersion(typeof(OverlayRenderer).Assembly.GetName().Version);

	/// <summary>
	///     Bottom-center attribution watermark, full opacity from frame 0 then fading out (see
	///     WatermarkAlpha). When the Map widget is also visible, DrawMapAttributionSlide follows as a
	///     separate second slide - see DrawMapAttributionOnly for when this watermark is off but the
	///     map's mandatory credit isn't.
	/// </summary>
	private void DrawWatermark(SKCanvas canvas, double sampleTimeSeconds)
	{
		var alpha = WatermarkAlpha(sampleTimeSeconds);
		if (alpha <= 0f) return;

		canvas.Save();
		canvas.Translate(_width / 2f, _height - WatermarkBottomMargin * _scale);
		canvas.Scale(_scale, _scale);

		DrawOutlined(canvas, "Made with OsmoOverlay", 0, -WatermarkLineGap, _watermarkTitleFont, White,
			SKTextAlign.Center, alpha);
		DrawOutlined(canvas, $"github.com/sefinek/osmo-overlay  •  v{WatermarkVersion}", 0, 0, _watermarkSubtitleFont,
			Accent, SKTextAlign.Center, alpha);

		canvas.Restore();
	}

	/// <summary>The map's required OSM credit, shown as its own short slide right after the watermark's (see MapAttributionSlideAlpha).</summary>
	private void DrawMapAttributionSlide(SKCanvas canvas, double sampleTimeSeconds, string mapAttribution)
	{
		var alpha = MapAttributionSlideAlpha(sampleTimeSeconds);
		if (alpha <= 0f) return;

		canvas.Save();
		canvas.Translate(_width / 2f, _height - WatermarkBottomMargin * _scale);
		canvas.Scale(_scale, _scale);

		DrawOutlined(canvas, mapAttribution, 0, 0, _smallFont, White, SKTextAlign.Center, alpha);

		canvas.Restore();
	}

	/// <summary>
	///     OSM's tile usage policy requires visible attribution whenever its tiles are shown, so unlike
	///     the optional "Made with OsmoOverlay" watermark, this can't fade out or be turned off - drawn
	///     at full opacity for the whole video when the watermark itself is disabled.
	/// </summary>
	private void DrawMapAttributionOnly(SKCanvas canvas, string mapAttribution)
	{
		canvas.Save();
		canvas.Translate(_width / 2f, _height - WatermarkBottomMargin * _scale);
		canvas.Scale(_scale, _scale);

		DrawOutlined(canvas, mapAttribution, 0, 0, _smallFont, White, SKTextAlign.Center);

		canvas.Restore();
	}

	/// <summary>No fade-in - it's on screen from the very first frame, so an instant appearance reads as the video simply starting, not as something popping in.</summary>
	private static float WatermarkAlpha(double sampleTimeSeconds)
	{
		return FadeAlpha(sampleTimeSeconds, 0, 0, WatermarkFadeOutStartSeconds, WatermarkDurationSeconds);
	}

	/// <summary>
	///     Unlike the watermark, this slide appears mid-video (right after the watermark's own slide
	///     ends) rather than at frame 0, so it gets a short fade-in too - popping in abruptly here would
	///     read as a glitch rather than an intentional second slide.
	/// </summary>
	private static float MapAttributionSlideAlpha(double sampleTimeSeconds)
	{
		var start = WatermarkDurationSeconds;
		var fadeInEnd = start + 1.0;
		var end = start + MapAttributionSlideDurationSeconds;
		var fadeOutStart = end - 1.0;
		return FadeAlpha(sampleTimeSeconds, start, fadeInEnd, fadeOutStart, end);
	}

	/// <summary>
	///     Linear fade in from `fadeInStart` to `fadeInEnd`, full opacity until `fadeOutStart`, then a
	///     linear fade down to 0 by `fadeOutEnd`. Pass `fadeInStart == fadeInEnd` for an instant
	///     appearance with no fade-in at all (used by the watermark, which starts at frame 0).
	/// </summary>
	private static float FadeAlpha(double sampleTimeSeconds, double fadeInStart, double fadeInEnd, double fadeOutStart,
		double fadeOutEnd)
	{
		if (sampleTimeSeconds < fadeInStart || sampleTimeSeconds >= fadeOutEnd) return 0f;
		if (sampleTimeSeconds < fadeInEnd) return (float)((sampleTimeSeconds - fadeInStart) / (fadeInEnd - fadeInStart));
		if (sampleTimeSeconds < fadeOutStart) return 1f;

		return (float)(1.0 - (sampleTimeSeconds - fadeOutStart) / (fadeOutEnd - fadeOutStart));
	}

	private static string FormatVersion(Version? version)
	{
		return version is null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
	}
}
