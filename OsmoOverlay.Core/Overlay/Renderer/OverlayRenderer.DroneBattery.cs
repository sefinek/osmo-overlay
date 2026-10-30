using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     The drone battery widget (DroneBatteryElement): the caption, a battery icon filled to the charge beside the percent, and
///     under the percent the extra lines the user asked for (voltage, temperature, current, cells). The icon and the percent take
///     a color that follows the charge: green when full, through yellow to LowColor (orange) at LowPercent and CriticalColor (red) at
///     CriticalPercent and below, blended in between. The voltage, temperature and cell lines stay the text color while they are fine and
///     take LowColor or CriticalColor when the reading is poor (DroneBatteryHealth).
/// </summary>
public sealed partial class OverlayRenderer
{
	private static readonly SKColor DroneBatteryLowDefault = SKColor.Parse("#FF9800");
	private static readonly SKColor DroneBatteryCriticalDefault = SKColor.Parse("#F44336");
	private static readonly SKColor DroneBatteryFullColor = SKColor.Parse("#4CAF50");
	private static readonly SKColor DroneBatteryMidColor = SKColor.Parse("#FFC107");

	internal static readonly DroneBattery SampleDroneBattery = new(100, 99.9, 99.9, 99.9, [3.99, 3.99, 3.99]);

	private readonly SKPaint _droneBatteryPaint = new() { IsAntialias = true };

	/// <summary>
	///     The charge's color: stops at 100 % (green), halfway between LowPercent and full (yellow), LowPercent and CriticalPercent, blended
	///     between them, the critical color below. Without a reading the widget's accent.
	/// </summary>
	internal static SKColor DroneBatteryColor(DroneBatteryElement element, double? percent)
	{
		if (percent is not { } charge || !element.ColorByCharge) return AccentColorOf(element);

		double critical = element.CriticalPercent, low = Math.Max(element.LowPercent, critical);
		(double At, SKColor Color)[] stops =
		[
			(critical, ResolveColor(element.CriticalColor, DroneBatteryCriticalDefault)),
			(low, ResolveColor(element.LowColor, DroneBatteryLowDefault)),
			((low + 100) / 2, DroneBatteryMidColor),
			(100, DroneBatteryFullColor)
		];

		if (charge <= stops[0].At) return stops[0].Color;
		for (int i = 1; i < stops.Length; i++)
		{
			if (charge > stops[i].At) continue;

			double span = stops[i].At - stops[i - 1].At;
			return span <= 0 ? stops[i].Color : Blend(stops[i - 1].Color, stops[i].Color, (charge - stops[i - 1].At) / span);
		}

		return stops[^1].Color;
	}

	private static SKColor Blend(SKColor from, SKColor to, double share)
	{
		static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
		return new SKColor(Mix(from.Red, to.Red, share), Mix(from.Green, to.Green, share), Mix(from.Blue, to.Blue, share));
	}

	/// <summary>The lines under the percent for what the widget shows - "--" with its unit where the log has no reading.</summary>
	internal static string[] DroneBatteryLines(DroneBatteryElement element, DroneBattery? battery)
	{
		return [.. DroneBatteryRows(element, battery).Select(r => r.Text)];
	}

	/// <summary>Those lines with how their reading stands (DroneBatteryHealth): the voltage and the cells by the lowest cell, the temperature by its range, the current by none.</summary>
	internal static (string Text, BatteryCondition Condition)[] DroneBatteryRows(DroneBatteryElement element, DroneBattery? battery)
	{
		List<(string, BatteryCondition)> rows = [];
		BatteryCondition cells = DroneBatteryHealth.Cells(battery, element.LowCellVolts, element.CriticalCellVolts);
		if (element.ShowVoltage) rows.Add((battery?.VoltageV is { } volts ? $"{F(volts, "0.0")} V" : "-- V", cells));
		if (element.ShowTemperature)
		{
			(string value, string unit) = FormatTemperature(battery?.TemperatureCelsius, element.Units);
			rows.Add(($"{value} {unit}", DroneBatteryHealth.Temperature(battery?.TemperatureCelsius, element.ColdCautionCelsius, element.ColdCriticalCelsius, element.HotCautionCelsius, element.HotCriticalCelsius)));
		}

		if (element.ShowCurrent) rows.Add((battery?.CurrentA is { } amps ? $"{F(amps, "0.0")} A" : "-- A", BatteryCondition.Normal));
		if (element.ShowCells) rows.Add((battery?.CellVoltages is { Count: > 0 } list ? string.Join(" / ", list.Select(c => F(c, "0.00"))) + " V" : "-- V", cells));
		return [.. rows];
	}

	/// <summary>A line's color: the plain text color while all is well, the widget's low or critical color when its reading isn't.</summary>
	private static SKColor DroneBatteryLineColor(DroneBatteryElement element, BatteryCondition condition, SKColor text)
	{
		return !element.ColorByCharge || condition == BatteryCondition.Normal ? text
			: condition == BatteryCondition.Critical ? ResolveColor(element.CriticalColor, DroneBatteryCriticalDefault)
			: ResolveColor(element.LowColor, DroneBatteryLowDefault);
	}

	private void DrawDroneBattery(SKCanvas canvas, DerivedFrame frame, DroneBatteryElement element)
	{
		DroneBattery? battery = frame.Raw.DroneBattery is { } reading && double.IsFinite(reading.Percent) ? reading : null;
		SKColor textColor = TextColorOf(element), outlineColor = OutlineColorOf(element), level = DroneBatteryColor(element, battery?.Percent);

		DrawOutlined(canvas, (element.Label ?? CoreStrings.Overlay_DroneBattery).ToUpperInvariant(), 0, 0, TextFont(element, OverlayElementBounds.LabelFontSize), textColor,
			outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);

		float left = 0;
		if (element.ShowIcon)
		{
			DrawBatteryIcon(canvas, battery?.Percent, level, textColor);
			left = OverlayElementBounds.DroneBatteryIconWidth + OverlayElementBounds.DroneBatteryIconGap;
		}

		SKFont valueFont = TextFont(element, OverlayElementBounds.DroneBatteryValueFontSize);
		string value = battery is null ? "--" : F(battery.Percent, "0");
		DrawOutlined(canvas, value, left, OverlayElementBounds.DroneBatteryValueBaseline, valueFont, level, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, "%", left + valueFont.MeasureText(value) + OverlayElementBounds.StatUnitGapX, OverlayElementBounds.DroneBatteryValueBaseline,
			TextFont(element, OverlayElementBounds.UnitFontSize), textColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);

		SKFont lineFont = TextFont(element, OverlayElementBounds.SmallFontSize);
		(string Text, BatteryCondition Condition)[] rows = DroneBatteryRows(element, battery);
		for (int i = 0; i < rows.Length; i++)
		{
			DrawOutlined(canvas, rows[i].Text, left, OverlayElementBounds.DroneBatteryLineBaseline + i * OverlayElementBounds.DroneBatteryLineStep, lineFont,
				DroneBatteryLineColor(element, rows[i].Condition, textColor), outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		}
	}

	/// <summary>An upright battery - a body with a cap on top - outlined in the text color and filled from the bottom to `percent`.</summary>
	private void DrawBatteryIcon(SKCanvas canvas, double? percent, SKColor fill, SKColor outline)
	{
		const float stroke = 5, radius = 9;
		float width = OverlayElementBounds.DroneBatteryIconWidth;
		float capHeight = OverlayElementBounds.DroneBatteryCapHeight, capWidth = OverlayElementBounds.DroneBatteryCapWidth;
		float top = OverlayElementBounds.DroneBatteryIconTop + capHeight, bottom = OverlayElementBounds.DroneBatteryIconBottom;
		var body = new SKRect(0, top, width, bottom);

		_droneBatteryPaint.Style = SKPaintStyle.Fill;
		_droneBatteryPaint.Color = outline;
		canvas.DrawRoundRect(new SKRect((width - capWidth) / 2, OverlayElementBounds.DroneBatteryIconTop, (width + capWidth) / 2, top + 2), 3, 3, _droneBatteryPaint);

		if (percent is { } charge)
		{
			SKRect inner = SKRect.Inflate(body, -(stroke + 3), -(stroke + 3));
			float height = inner.Height * (float)Math.Clamp(charge / 100.0, 0, 1);
			if (height > 0)
			{
				canvas.Save();
				canvas.ClipRoundRect(new SKRoundRect(inner, radius - stroke / 2));
				_droneBatteryPaint.Color = fill;
				canvas.DrawRect(new SKRect(inner.Left, inner.Bottom - height, inner.Right, inner.Bottom), _droneBatteryPaint);
				canvas.Restore();
			}
		}

		_droneBatteryPaint.Style = SKPaintStyle.Stroke;
		_droneBatteryPaint.StrokeWidth = stroke;
		_droneBatteryPaint.Color = outline;
		canvas.DrawRoundRect(SKRect.Inflate(body, -stroke / 2, -stroke / 2), radius, radius, _droneBatteryPaint);
	}
}
