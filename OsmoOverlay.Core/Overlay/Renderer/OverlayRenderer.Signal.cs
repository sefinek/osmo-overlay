using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Signal bars - five rising bars, filled to a level - and the signal widget (SignalElement): a line per radio link with an
///     arrow for its direction, and the bars after the widest line. The GPS widget draws the same bars after its count. A link's percent
///     is the accent color while the link is good and yellow or red below (ColorByLevel).
/// </summary>
public sealed partial class OverlayRenderer
{
	private static readonly SKColor SignalGoodColor = SKColor.Parse("#2BE82B");
	private static readonly SKColor SignalFairColor = SKColor.Parse("#F7C51E");
	private static readonly SKColor SignalPoorColor = SKColor.Parse("#FF4D4D");

	private readonly SKPaint _signalPaint = new() { IsAntialias = true };

	/// <summary>0-5 bars for a quality in percent.</summary>
	internal static int BarsOf(double percent)
	{
		return (int)Math.Clamp(Math.Round(percent / 20, MidpointRounding.AwayFromZero), 0, OverlayElementBounds.SignalBarCount);
	}

	/// <summary>A link's color by the widget's own limits: `good` from GoodPercent, the fair color from FairPercent, the poor one below.</summary>
	private static SKColor SignalLevelColor(SignalElement element, double percent, SKColor good)
	{
		return percent >= element.GoodPercent ? good : percent >= element.FairPercent ? ResolveColor(element.FairColor, SignalFairColor) : ResolveColor(element.PoorColor, SignalPoorColor);
	}

	private static SKColor SignalBarColor(int level)
	{
		return level >= 4 ? SignalGoodColor : level == 3 ? SignalFairColor : SignalPoorColor;
	}

	/// <summary>The bars with their bottom on `bottom` and the first one's left on `left`; a null level draws them all empty.</summary>
	private void DrawSignalBars(SKCanvas canvas, LabeledStatElement element, float left, float bottom, int? level, SKColor? fill = null)
	{
		SKColor filled = fill ?? SignalBarColor(level ?? 0), empty = TextColorOf(element).WithAlpha(70), outline = OutlineColorOf(element);
		for (int i = 0; i < OverlayElementBounds.SignalBarCount; i++)
		{
			float height = OverlayElementBounds.SignalBarMinHeight + i * OverlayElementBounds.SignalBarHeightStep;
			var bar = new SKRect(left + i * OverlayElementBounds.SignalBarStep, bottom - height, left + i * OverlayElementBounds.SignalBarStep + OverlayElementBounds.SignalBarWidth, bottom);

			_signalPaint.Style = SKPaintStyle.Stroke;
			_signalPaint.StrokeWidth = 3;
			_signalPaint.Color = outline;
			canvas.DrawRoundRect(bar, 2, 2, _signalPaint);
			_signalPaint.Style = SKPaintStyle.Fill;
			_signalPaint.Color = i < level ? filled : empty;
			canvas.DrawRoundRect(bar, 2, 2, _signalPaint);
		}
	}

	private void DrawSignal(SKCanvas canvas, DerivedFrame frame, SignalElement element)
	{
		DroneSignal? signal = frame.Raw.DroneSignal;
		SKColor textColor = TextColorOf(element), outlineColor = OutlineColorOf(element);
		DrawOutlined(canvas, (element.Label ?? CoreStrings.Overlay_Signal).ToUpperInvariant(), 0, 0, TextFont(element, OverlayElementBounds.LabelFontSize), textColor,
			outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);

		SKFont font = TextFont(element, OverlayElementBounds.SignalLineSize);
		List<(bool Up, double? Percent)> links = [];
		if (element.ShowUplink) links.Add((true, signal?.UplinkPercent));
		if (element.ShowDownlink) links.Add((false, signal?.DownlinkPercent));

		float widest = 0;
		for (int i = 0; i < links.Count; i++)
		{
			float baseline = OverlayElementBounds.SignalBaseline(links.Count, i);
			DrawLinkArrow(canvas, links[i].Up, baseline, textColor, outlineColor);
			string text = links[i].Percent is { } percent && double.IsFinite(percent) ? F(percent, "0") + "%" : "--%";
			SKColor percentColor = element.ColorByLevel && links[i].Percent is { } level && double.IsFinite(level) ? SignalLevelColor(element, level, AccentColorOf(element)) : textColor;
			DrawOutlined(canvas, text, OverlayElementBounds.SignalTextX, baseline, font, percentColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
			widest = Math.Max(widest, font.MeasureText(text));
		}

		if (!element.ShowBars || links.Count == 0) return;

		double[] known = [.. links.Where(l => l.Percent is not null).Select(l => l.Percent!.Value)];
		float bottom = OverlayElementBounds.StatValueBaselineY, barsLeft = OverlayElementBounds.SignalTextX + widest + OverlayElementBounds.SignalBarsGap;
		if (known.Length == 0)
		{
			DrawSignalBars(canvas, element, barsLeft, bottom, null);
			return;
		}

		double average = known.Average();
		DrawSignalBars(canvas, element, barsLeft, bottom, BarsOf(average), SignalLevelColor(element, average, SignalGoodColor));
	}

	/// <summary>An arrow up (uplink) or down (downlink), as tall as the line's capitals, left of the percent.</summary>
	private void DrawLinkArrow(SKCanvas canvas, bool up, float baseline, SKColor color, SKColor outline)
	{
		const float width = 22, height = 30, head = 14;
		float top = baseline - height, x = 3;
		using var builder = new SKPathBuilder();
		if (up)
		{
			builder.MoveTo(x + width / 2, top);
			builder.LineTo(x + width, top + head);
			builder.LineTo(x + width * 0.64f, top + head);
			builder.LineTo(x + width * 0.64f, baseline);
			builder.LineTo(x + width * 0.36f, baseline);
			builder.LineTo(x + width * 0.36f, top + head);
			builder.LineTo(x, top + head);
		}
		else
		{
			builder.MoveTo(x + width / 2, baseline);
			builder.LineTo(x + width, baseline - head);
			builder.LineTo(x + width * 0.64f, baseline - head);
			builder.LineTo(x + width * 0.64f, top);
			builder.LineTo(x + width * 0.36f, top);
			builder.LineTo(x + width * 0.36f, baseline - head);
			builder.LineTo(x, baseline - head);
		}

		builder.Close();
		using SKPath path = builder.Detach();
		_signalPaint.Style = SKPaintStyle.Stroke;
		_signalPaint.StrokeWidth = 3;
		_signalPaint.StrokeJoin = SKStrokeJoin.Round;
		_signalPaint.Color = outline;
		canvas.DrawPath(path, _signalPaint);
		_signalPaint.Style = SKPaintStyle.Fill;
		_signalPaint.Color = color;
		canvas.DrawPath(path, _signalPaint);
	}
}
