using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Telemavix.Gui;

/// <summary>
///     Two thin horizontal bars (left over right) showing how loud the sound is now, on a dB scale: green, yellow above
///     -18 dBFS, red above -6. A bar falls back smoothly and a thin peak mark holds for a moment, like a mixer's meter.
/// </summary>
public sealed class LevelMeter : Control
{
	private const double FloorDb = -60;
	private const double YellowDb = -18;
	private const double RedDb = -6;
	private const double FallPerSecond = 0.9;
	private const double PeakHoldSeconds = 1;

	private static readonly IBrush TrackBrush = Palette.SubtleFill;
	private static readonly IBrush GreenBrush = Palette.Success;
	private static readonly IBrush YellowBrush = Palette.Warning;
	private static readonly IBrush RedBrush = Palette.Danger;
	private static readonly IBrush HoldBrush = Palette.TextPrimary;

	private static readonly double[] ScaleDb = [0, -6, -12, -18, -30, -45, -60];
	private static readonly IBrush ScaleBrush = Palette.Tint(Palette.TextPrimary, 0.55);
	private static readonly IPen TickPen = new Pen(Palette.Tint(Palette.TextPrimary, 0.3));

	/// <summary>Bars standing up, filling from the bottom, with a dB scale between them - for a tall panel; else two flat bars.</summary>
	public bool Vertical { get; set; }

	private readonly double[] _shown = new double[2];
	private readonly double[] _hold = new double[2];
	private readonly double[] _holdAge = new double[2];

	/// <summary>Moves the bars toward the peaks (0-1) after `seconds` since the last call; redraws only when something moved.</summary>
	public void Update(float left, float right, double seconds)
	{
		bool changed = Step(0, left, seconds);
		changed |= Step(1, right, seconds);
		if (changed) InvalidateVisual();
	}

	private bool Step(int i, float peak, double seconds)
	{
		double before = _shown[i], holdBefore = _hold[i];
		double target = ToFraction(peak);
		_shown[i] = target >= _shown[i] ? target : Math.Max(target, _shown[i] - FallPerSecond * seconds);

		if (_shown[i] >= _hold[i])
		{
			_hold[i] = _shown[i];
			_holdAge[i] = 0;
		}
		else if ((_holdAge[i] += seconds) > PeakHoldSeconds)
		{
			_hold[i] = Math.Max(_shown[i], _hold[i] - FallPerSecond * seconds);
		}

		return before != _shown[i] || holdBefore != _hold[i];
	}

	private static double ToFraction(float peak)
	{
		// Written so that NaN lands here too: it would stick in the bar for good (every comparison with it is false).
		if (!(peak > 0)) return 0;

		double db = 20 * Math.Log10(peak);
		return Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
	}

	public override void Render(DrawingContext context)
	{
		if (Vertical)
		{
			RenderVertical(context);
			return;
		}

		double barHeight = Math.Max(1, (Bounds.Height - 2) / 2);
		DrawBar(context, 0, 0, barHeight, 0);
		DrawBar(context, 0, barHeight + 2, barHeight, 1);
	}

	private void DrawBar(DrawingContext context, double x, double y, double height, int i)
	{
		double width = Bounds.Width;
		context.DrawRectangle(TrackBrush, null, new Rect(x, y, width, height), 1.5, 1.5);

		double yellowAt = (YellowDb - FloorDb) / -FloorDb, redAt = (RedDb - FloorDb) / -FloorDb;
		double value = _shown[i];
		DrawSegment(context, GreenBrush, 0, Math.Min(value, yellowAt), x, y, width, height);
		DrawSegment(context, YellowBrush, yellowAt, Math.Min(value, redAt), x, y, width, height);
		DrawSegment(context, RedBrush, redAt, value, x, y, width, height);

		if (_hold[i] > 0.02)
			context.DrawRectangle(HoldBrush, null, new Rect(x + Math.Min(_hold[i], 1) * width - 1.5, y, 1.5, height));
	}

	private static void DrawSegment(DrawingContext context, IBrush brush, double from, double to, double x, double y, double width, double height)
	{
		if (to <= from) return;

		context.DrawRectangle(brush, null, new Rect(x + from * width, y, (to - from) * width, height));
	}

	private const int Segments = 40;
	private const double HeaderHeight = 20, FooterHeight = 22, ScaleGap = 34;

	private static readonly IBrush GreenDim = Palette.Tint(Palette.Success, 0.16);
	private static readonly IBrush YellowDim = Palette.Tint(Palette.Warning, 0.16);
	private static readonly IBrush RedDim = Palette.Tint(Palette.Danger, 0.16);
	private static readonly IBrush ReadoutBrush = Palette.TextPrimary;
	private static readonly string[] ChannelNames = ["L", "R"];
	private static FormattedText[]? _scaleLabels, _channelLabels;
	private readonly (int Tenths, FormattedText Formatted)?[] _readouts = new (int, FormattedText)?[2];

	private static FormattedText[] ScaleLabels => _scaleLabels ??= ScaleDb.Select(db => Label(db == 0 ? "0" : $"{db:0}", 10, ScaleBrush)).ToArray();
	private static FormattedText[] ChannelLabels => _channelLabels ??= ChannelNames.Select(n => Label(n, 12, ScaleBrush)).ToArray();

	private static FormattedText Label(string text, double size, IBrush brush)
	{
		return new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
	}

	private void RenderVertical(DrawingContext context)
	{
		double barWidth = Math.Max(4, (Bounds.Width - ScaleGap) / 2);
		double rightX = barWidth + ScaleGap;
		double top = HeaderHeight, area = Math.Max(Segments, Bounds.Height - HeaderHeight - FooterHeight);
		DrawSegmentedBar(context, 0, barWidth, top, area, 0);
		DrawSegmentedBar(context, rightX, barWidth, top, area, 1);

		FormattedText[] channels = ChannelLabels;
		context.DrawText(channels[0], new Point((barWidth - channels[0].Width) / 2, (HeaderHeight - channels[0].Height) / 2));
		context.DrawText(channels[1], new Point(rightX + (barWidth - channels[1].Width) / 2, (HeaderHeight - channels[1].Height) / 2));
		DrawReadout(context, 0, 0, barWidth, top + area);
		DrawReadout(context, 1, rightX, barWidth, top + area);

		FormattedText[] labels = ScaleLabels;
		for (int i = 0; i < ScaleDb.Length; i++)
		{
			double y = top + area - (ScaleDb[i] - FloorDb) / -FloorDb * area;
			double textY = Math.Clamp(y - labels[i].Height / 2, top - labels[i].Height / 2, top + area - labels[i].Height / 2);
			context.DrawText(labels[i], new Point(barWidth + (ScaleGap - labels[i].Width) / 2, textY));
			context.DrawLine(TickPen, new Point(barWidth + 2, y), new Point(barWidth + 6, y));
			context.DrawLine(TickPen, new Point(rightX - 6, y), new Point(rightX - 2, y));
		}
	}

	private void DrawSegmentedBar(DrawingContext context, double x, double width, double top, double height, int i)
	{
		double segment = height / Segments;
		double yellowAt = (YellowDb - FloorDb) / -FloorDb, redAt = (RedDb - FloorDb) / -FloorDb;
		int holdSegment = _hold[i] > 0.02 ? Math.Min(Segments - 1, (int)(_hold[i] * Segments)) : -1;
		for (int n = 0; n < Segments; n++)
		{
			double centre = (n + 0.5) / Segments;
			bool red = centre >= redAt, yellow = centre >= yellowAt;
			bool lit = _shown[i] >= centre - 0.5 / Segments + 0.0001;
			IBrush brush = n == holdSegment && !lit ? HoldBrush : lit ? (red ? RedBrush : yellow ? YellowBrush : GreenBrush) : red ? RedDim : yellow ? YellowDim : GreenDim;
			double y = top + height - (n + 1) * segment;
			context.DrawRectangle(brush, null, new Rect(x, y + 0.75, width, Math.Max(1, segment - 1.5)), 1, 1);
		}
	}

	private void DrawReadout(DrawingContext context, int i, double x, double width, double y)
	{
		// -inf only once the peak has fallen to the floor of the scale, not at an arbitrary level above it.
		int tenths = _hold[i] > 0 ? (int)Math.Round((FloorDb + _hold[i] * -FloorDb) * 10) : int.MinValue;
		if (_readouts[i] is not { } cached || cached.Tenths != tenths)
			_readouts[i] = cached = (tenths, Label(tenths == int.MinValue ? "-inf" : (tenths / 10.0).ToString("0.0"), 11, ReadoutBrush));

		context.DrawText(cached.Formatted, new Point(x + (width - cached.Formatted.Width) / 2, y + (FooterHeight - cached.Formatted.Height) / 2 + 2));
	}
}
