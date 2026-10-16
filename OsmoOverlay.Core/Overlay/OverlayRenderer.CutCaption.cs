using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

public sealed partial class OverlayRenderer
{
	private const float CutCaptionTextSize = 54;
	private const float CutCaptionLineHeight = 70;
	private const float CutCaptionPaddingX = 56;
	private const float CutCaptionPaddingY = 36;
	// In the reference space (like every widget's size): wider than this the text goes on to the next line.
	private const float CutCaptionMaxTextWidth = 1500;

	// Wrapped once per text and frame size, not on every frame it is up.
	private (string Text, string[] Lines, float Width)? _cutCaptionLayout;

	/// <summary>The captions at the cuts, on the output's timeline (OutputTimeline.Captions) - set with the timeline, null/empty without cuts.</summary>
	public IReadOnlyList<CutCaptionAt> CutCaptions { get; set; } = [];

	/// <summary>The caption up at `t` with how far it has faded in (0..1) - the latest one started when two overlap; null when none.</summary>
	internal (CutCaptionAt Caption, float Alpha)? ActiveCutCaption(double t)
	{
		CutCaptionAt? active = null;
		foreach (CutCaptionAt caption in CutCaptions)
		{
			if (t >= caption.StartSeconds && t < caption.EndSeconds && (active is null || caption.StartSeconds >= active.StartSeconds)) active = caption;
		}

		if (active is null) return null;

		// A caption shorter than its two fades gets them squeezed, not cut off.
		double fade = Math.Min(active.FadeSeconds, (active.EndSeconds - active.StartSeconds) / 2);
		float alpha = fade <= 0 ? 1f : (float)Math.Clamp(Math.Min((t - active.StartSeconds) / fade, (active.EndSeconds - t) / fade), 0, 1);
		return alpha > 0f ? (active, alpha) : null;
	}

	private void DrawCutCaption(SKCanvas canvas, double t)
	{
		if (ActiveCutCaption(t) is not { } active) return;

		(string[] lines, float textWidth) = CutCaptionLines(active.Caption.Text);
		float width = textWidth + CutCaptionPaddingX * 2, height = lines.Length * CutCaptionLineHeight + CutCaptionPaddingY * 2;

		canvas.Save();
		canvas.Translate(_width / 2f, _height / 2f);
		canvas.Scale(_scale);
		var box = new SKRect(-width / 2, -height / 2, width / 2, height / 2);
		const float radius = 36;
		SKPaint fill = PanelFillPaint(null, 0.7f);
		fill.Color = fill.Color.WithAlpha((byte)(fill.Color.Alpha * active.Alpha));
		canvas.DrawRoundRect(box, radius, radius, fill);

		_gpsAlertPaint.Style = SKPaintStyle.Stroke;
		_gpsAlertPaint.StrokeWidth = 2;
		_gpsAlertPaint.Color = White.WithAlpha((byte)(70 * active.Alpha));
		canvas.DrawRoundRect(SKRect.Inflate(box, -1, -1), radius - 1, radius - 1, _gpsAlertPaint);

		SKFont font = GetFont(null, CutCaptionTextSize);
		float y = box.Top + CutCaptionPaddingY + CutCaptionLineHeight / 2 + CutCaptionTextSize * 0.35f;
		foreach (string line in lines)
		{
			DrawOutlined(canvas, line, 0, y, font, White, SKTextAlign.Center, opacity: active.Alpha);
			y += CutCaptionLineHeight;
		}

		canvas.Restore();
	}

	/// <summary>The text broken into lines no wider than CutCaptionMaxTextWidth (at its own newlines, then between words), and the widest one.</summary>
	private (string[] Lines, float Width) CutCaptionLines(string text)
	{
		if (_cutCaptionLayout is { } cached && cached.Text == text) return (cached.Lines, cached.Width);

		SKFont font = GetFont(null, CutCaptionTextSize);
		List<string> lines = [];
		foreach (string paragraph in text.Replace("\r\n", "\n").Split('\n'))
		{
			string current = "";
			foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				string candidate = current.Length == 0 ? word : $"{current} {word}";
				if (current.Length > 0 && font.MeasureText(candidate) > CutCaptionMaxTextWidth)
				{
					lines.Add(current);
					current = word;
				}
				else
					current = candidate;
			}

			lines.Add(current);
		}

		string[] result = [.. lines];
		float widest = result.Length == 0 ? 0 : result.Max(l => font.MeasureText(l));
		_cutCaptionLayout = (text, result, widest);
		return (result, widest);
	}
}
