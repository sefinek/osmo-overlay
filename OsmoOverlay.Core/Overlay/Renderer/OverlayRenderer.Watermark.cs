using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>The "Made with OsmoOverlay" watermark: a widget (WatermarkElement) over the video, in its own corner on the route overview card.</summary>
public sealed partial class OverlayRenderer
{
	// On the card it holds from frame 0 and fades out with the card's own crossfade.
	private const float WatermarkBottomMargin = 110f;

	private static readonly string WatermarkSubtitle =
		$"github.com/sefinek/osmo-overlay  •  v{FormatVersion(typeof(OverlayRenderer).Assembly.GetName().Version)}";

	/// <summary>
	///     On the route overview card: right-aligned to the margin the stats column and map panel use - centered under the
	///     frame it would sit under the map alone, which only covers the card's left part.
	/// </summary>
	private void DrawWatermark(SKCanvas canvas, double sampleTimeSeconds)
	{
		float alpha = FadeAlpha(sampleTimeSeconds, 0, 0, RouteIntroTransitionStartSeconds, RouteIntro.DurationSeconds);
		if (alpha <= 0f) return;

		canvas.Save();
		canvas.Translate(_width - OverlayElementBounds.Margin * _scale, _height - WatermarkBottomMargin * _scale);
		canvas.Scale(_scale, _scale);
		DrawWatermarkLines(canvas, _watermarkTitleFont, _watermarkSubtitleFont, White, Accent, Shadow, 1f, SKTextAlign.Right, alpha);
		canvas.Restore();
	}

	/// <summary>The widget, centered on its anchor in its own font and colors; the second line keeps the accent.</summary>
	private void DrawWatermarkWidget(SKCanvas canvas, WatermarkElement element)
	{
		DrawWatermarkLines(canvas, TextFont(element, OverlayElementBounds.WatermarkTitleFontSize),
			TextFont(element, OverlayElementBounds.WatermarkSubtitleFontSize), TextColorOf(element), Accent, OutlineColorOf(element),
			element.OutlineWidth, SKTextAlign.Center, 1f);
	}

	private void DrawWatermarkLines(SKCanvas canvas, SKFont titleFont, SKFont subtitleFont, SKColor title, SKColor subtitle, SKColor outline,
		float outlineWidth, SKTextAlign align, float alpha)
	{
		DrawOutlined(canvas, OverlayElementBounds.WatermarkTitle, 0, -OverlayElementBounds.WatermarkLineGap, titleFont, title, align, alpha,
			outline, outlineWidth);
		DrawOutlined(canvas, WatermarkSubtitle, 0, 0, subtitleFont, subtitle, align, alpha, outline, outlineWidth);
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
