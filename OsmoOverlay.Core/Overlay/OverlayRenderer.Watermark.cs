using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>The "Made with OsmoOverlay" watermark.</summary>
public sealed partial class OverlayRenderer
{
	// Watermark holds at full opacity from frame 0, then fades out. These fixed numbers are the fallback for when there's no route-intro card at all; with one
	// enabled, WatermarkFadeOutStartSecondsEffective/WatermarkFadeOutEndSeconds below replace them so
	// the watermark's own fade locks to the card's actual crossfade instead of running on its own
	// unrelated clock.
	private const double WatermarkFadeOutStartSeconds = 5.0;
	private const double WatermarkDurationSeconds = 6.0;
	private const float WatermarkBottomMargin = 110f;
	private const float WatermarkLineGap = 46f;

	private double WatermarkFadeOutStartSecondsEffective =>
		RouteIntro.Enabled ? RouteIntroTransitionStartSeconds : WatermarkFadeOutStartSeconds;

	private double WatermarkFadeOutEndSeconds =>
		RouteIntro.Enabled ? RouteIntro.DurationSeconds : WatermarkDurationSeconds;

	private static readonly string WatermarkSubtitle =
		$"github.com/sefinek/osmo-overlay  •  v{FormatVersion(typeof(OverlayRenderer).Assembly.GetName().Version)}";

	/// <summary>
	///     Bottom-center attribution watermark, full opacity from frame 0 then fading out (see
	///     WatermarkAlpha). The maps' own credit is a widget of its own (MapAttributionElement). anchorX/align
	///     default to centered under the whole frame (the normal per-frame widget pass); Render passes a
	///     right-aligned override for the route-intro card, whose map only covers the card's left portion.
	/// </summary>
	private void DrawWatermark(SKCanvas canvas, double sampleTimeSeconds, float? anchorX = null,
		SKTextAlign align = SKTextAlign.Center)
	{
		float alpha = WatermarkAlpha(sampleTimeSeconds);
		if (alpha <= 0f) return;

		canvas.Save();
		canvas.Translate(anchorX ?? _width / 2f, _height - WatermarkBottomMargin * _scale);
		canvas.Scale(_scale, _scale);

		DrawOutlined(canvas, "Made with OsmoOverlay", 0, -WatermarkLineGap, _watermarkTitleFont, White, align, alpha);
		DrawOutlined(canvas, WatermarkSubtitle, 0, 0, _watermarkSubtitleFont, Accent, align, alpha);

		canvas.Restore();
	}

	/// <summary>No fade-in - it's on screen from the very first frame, so an instant appearance reads as the video simply starting, not as something popping in.</summary>
	private float WatermarkAlpha(double sampleTimeSeconds)
	{
		return FadeAlpha(sampleTimeSeconds, 0, 0, WatermarkFadeOutStartSecondsEffective, WatermarkFadeOutEndSeconds);
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
