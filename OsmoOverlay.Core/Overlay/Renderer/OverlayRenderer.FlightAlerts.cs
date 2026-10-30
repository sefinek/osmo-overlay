using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     The flight alerts widget (FlightAlertsElement): what the flight log says happened, each on a panel like the GPS alert's -
///     a colored icon, the kind in capitals and the detail under it. An event is up for the widget's ShowSeconds from its frame
///     and fades in and out; the newest MaxLines are stacked, the oldest on top. The events come from the whole set of frames
///     once (FlightAlertNotices), so a frame draws the same whatever was drawn before it.
/// </summary>
public sealed partial class OverlayRenderer
{
	private const float FlightAlertSlideDistance = 44;
	private const float FlightAlertLineGap = 14;

	// The widest a detail line gets before it's cut with an ellipsis - the app's own messages can be long.
	internal const float FlightAlertMaxTextWidth = 1500;

	private static readonly SKColor FlightModeColor = SKColor.Parse("#4FC3F7");
	private static readonly SKColor FlightMotorsOnColor = SKColor.Parse("#3DDC84");
	private static readonly SKColor FlightMotorsOffColor = SKColor.Parse("#9AA5B1");
	private static readonly SKColor FlightBatteryColor = SKColor.Parse("#F7C51E");
	private static readonly SKColor FlightLowVoltageColor = SKColor.Parse("#FF8A3D");
	private static readonly SKColor FlightCriticalColor = SKColor.Parse("#FF4D4D");
	private static readonly SKColor FlightTipColor = SKColor.Parse("#4FC3F7");

	private static readonly (float X, float Y)[] FlightRotors = [(-1f, -1f), (1f, 1f), (-1f, 1f), (1f, -1f)];

	private readonly SKPaint _flightAlertPaint = new() { IsAntialias = true };
	private List<FlightAlertNotice>? _flightAlerts;

	/// <summary>One event and the moment on the frames' timeline (the output's, after cuts) it begins at.</summary>
	internal readonly record struct FlightAlertNotice(double Start, FlightEvent Event);

	/// <summary>Every event the frames carry, in time order.</summary>
	internal static List<FlightAlertNotice> FindFlightAlerts(IReadOnlyList<DerivedFrame> frames)
	{
		List<FlightAlertNotice> notices = [];
		foreach (DerivedFrame frame in frames)
		{
			if (frame.Raw.FlightEvents is not { Count: > 0 } events) continue;

			foreach (FlightEvent flightEvent in events) notices.Add(new FlightAlertNotice(frame.Raw.SampleTimeSeconds, flightEvent));
		}

		return notices;
	}

	internal static bool IsAnnounced(FlightAlertsElement element, FlightEventKind kind)
	{
		return kind switch
		{
			FlightEventKind.FlightMode => element.ShowFlightModes,
			FlightEventKind.MotorsOn or FlightEventKind.MotorsOff or FlightEventKind.FinalPosition => element.ShowMotors,
			FlightEventKind.BatteryStart or FlightEventKind.BatteryLevel or FlightEventKind.BatteryEnd or FlightEventKind.LowVoltage or FlightEventKind.CriticalVoltage => element.ShowBattery,
			FlightEventKind.Warning => element.ShowWarnings,
			_ => element.ShowTips
		};
	}

	/// <summary>The title and the detail of an event, in the interface's language (the app's own messages as the log has them).</summary>
	public static (string Title, string Detail) FlightAlertText(FlightEvent flightEvent)
	{
		string Value(string format) => string.Format(format, F(flightEvent.Value ?? 0, flightEvent.Kind is FlightEventKind.LowVoltage or FlightEventKind.CriticalVoltage ? "0.00" : "0"));
		return flightEvent.Kind switch
		{
			FlightEventKind.FlightMode => (CoreStrings.Overlay_FlightMode, flightEvent.Text ?? ""),
			FlightEventKind.MotorsOn => (CoreStrings.Overlay_MotorsOn, CoreStrings.Overlay_MotorsOnDetail),
			FlightEventKind.MotorsOff => (CoreStrings.Overlay_MotorsOff, CoreStrings.Overlay_MotorsOffDetail),
			FlightEventKind.BatteryStart => (CoreStrings.Overlay_BatteryAlert, Value(CoreStrings.Overlay_BatteryStartDetail)),
			FlightEventKind.BatteryLevel => (CoreStrings.Overlay_BatteryAlert, Value(CoreStrings.Overlay_BatteryLevelDetail)),
			FlightEventKind.BatteryEnd => (CoreStrings.Overlay_BatteryAlert, Value(CoreStrings.Overlay_BatteryEndDetail)),
			FlightEventKind.ReturnAltitude => (CoreStrings.Overlay_ReturnAltitude, Value(CoreStrings.Overlay_ReturnAltitudeDetail)),
			FlightEventKind.AltitudeLimit => (CoreStrings.Overlay_AltitudeLimit, Value(CoreStrings.Overlay_AltitudeLimitDetail)),
			FlightEventKind.FinalPosition => (CoreStrings.Overlay_FinalPosition, Value(CoreStrings.Overlay_FinalPositionDetail)),
			FlightEventKind.LowVoltage => (CoreStrings.Overlay_LowVoltage, Value(CoreStrings.Overlay_CellVoltageDetail)),
			FlightEventKind.CriticalVoltage => (CoreStrings.Overlay_CriticalVoltage, Value(CoreStrings.Overlay_CellVoltageDetail)),
			FlightEventKind.Warning => (CoreStrings.Overlay_FlightWarning, WithCode(flightEvent)),
			_ => (CoreStrings.Overlay_FlightNotice, WithCode(flightEvent))
		};

		static string WithCode(FlightEvent e) => string.IsNullOrEmpty(e.Code) ? e.Text ?? "" : string.Format(CoreStrings.Overlay_MessageCode, e.Text, e.Code);
	}

	private static SKColor FlightAlertColor(FlightEventKind kind)
	{
		return kind switch
		{
			FlightEventKind.FlightMode => FlightModeColor,
			FlightEventKind.MotorsOn or FlightEventKind.FinalPosition => FlightMotorsOnColor,
			FlightEventKind.MotorsOff => FlightMotorsOffColor,
			FlightEventKind.BatteryStart or FlightEventKind.BatteryLevel or FlightEventKind.BatteryEnd => FlightBatteryColor,
			FlightEventKind.LowVoltage => FlightLowVoltageColor,
			FlightEventKind.CriticalVoltage or FlightEventKind.Warning => FlightCriticalColor,
			_ => FlightTipColor
		};
	}

	private void DrawFlightMode(SKCanvas canvas, DerivedFrame frame, FlightModeElement element)
	{
		SKColor outline = OutlineColorOf(element);
		SKFont labelFont = TextFont(element, OverlayElementBounds.LabelFontSize), modeFont = TextFont(element, OverlayElementBounds.FlightModeSize);
		string label = (element.Label ?? CoreStrings.Overlay_FlightModeLabel).ToUpperInvariant();
		string mode = string.IsNullOrEmpty(frame.Raw.FlightMode) ? "--" : frame.Raw.FlightMode;
		float box = element.TextAlign == TextAlign.Left ? 0 : Math.Max(OverlayElementBounds.TextBoxRight(element), Math.Max(labelFont.MeasureText(label), modeFont.MeasureText(mode)));
		DrawOutlined(canvas, label, OverlayElementBounds.AlignedX(element.TextAlign, box, labelFont.MeasureText(label)), 0, labelFont, TextColorOf(element),
			outlineColor: outline, outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, mode, OverlayElementBounds.AlignedX(element.TextAlign, box, modeFont.MeasureText(mode)), OverlayElementBounds.FlightModeBaseline, modeFont, ModeColor(element, mode) ?? AccentColorOf(element),
			outlineColor: outline, outlineWidthScale: element.OutlineWidth);
	}

	private void DrawFlightAlerts(SKCanvas canvas, DerivedFrame frame, FlightAlertsElement element)
	{
		double t = frame.Raw.SampleTimeSeconds, fade = Math.Clamp(element.FadeSeconds, 0.02, 2), seconds = Math.Max(element.ShowSeconds, 2 * fade);
		List<FlightAlertNotice> notices = _flightAlerts ??= FindFlightAlerts(_allFrames);

		List<FlightAlertNotice> active = [];
		foreach (FlightAlertNotice notice in notices)
		{
			if (notice.Start > t) break;
			if (t < notice.Start + seconds && IsAnnounced(element, notice.Event.Kind)) active.Add(notice);
		}

		int lines = Math.Clamp(element.MaxLines, 1, OverlayElementBounds.FlightAlertsMaxLines);
		if (active.Count > lines) active.RemoveRange(0, active.Count - lines);

		for (int i = 0; i < active.Count; i++)
		{
			FlightAlertNotice notice = active[i];
			float alpha = (float)Math.Clamp(Math.Min((t - notice.Start) / fade, (notice.Start + seconds - t) / fade), 0, 1);
			if (alpha <= 0f) continue;

			canvas.Save();
			canvas.Translate(0, i * (OverlayElementBounds.GpsAlertHeight + FlightAlertLineGap) - (element.SlideIn ? (1 - alpha) * FlightAlertSlideDistance : 0));
			DrawFlightAlert(canvas, element, notice.Event, alpha);
			canvas.Restore();
		}
	}

	private void DrawFlightAlert(SKCanvas canvas, FlightAlertsElement element, FlightEvent flightEvent, float alpha)
	{
		(string rawTitle, string rawDetail) = FlightAlertText(flightEvent);
		SKFont titleFont = TextFont(element, OverlayElementBounds.GpsAlertTitleSize), detailFont = TextFont(element, OverlayElementBounds.GpsAlertDetailSize);
		string title = rawTitle.ToUpperInvariant(), detail = Ellipsize(rawDetail, detailFont, FlightAlertMaxTextWidth);

		float text = Math.Max(titleFont.MeasureText(title), detailFont.MeasureText(detail));
		float width = OverlayElementBounds.FlightAlertWidth(text);
		var box = new SKRect(-width / 2, 0, width / 2, OverlayElementBounds.GpsAlertHeight);
		const float radius = OverlayElementBounds.GpsAlertHeight / 2;
		SKColor color = FlightAlertColor(flightEvent.Kind);

		SKPaint fill = PanelFillPaint(element);
		fill.Color = fill.Color.WithAlpha((byte)(fill.Color.Alpha * alpha));
		canvas.DrawRoundRect(box, radius, radius, fill);

		_flightAlertPaint.Style = SKPaintStyle.Stroke;
		_flightAlertPaint.StrokeWidth = 3;
		_flightAlertPaint.Color = color.WithAlpha((byte)(150 * alpha));
		canvas.DrawRoundRect(SKRect.Inflate(box, -1.5f, -1.5f), radius - 1.5f, radius - 1.5f, _flightAlertPaint);

		float iconRadius = OverlayElementBounds.GpsAlertIcon / 2;
		var iconCenter = new SKPoint(box.Left + OverlayElementBounds.GpsAlertGap + iconRadius, box.MidY);
		_flightAlertPaint.Style = SKPaintStyle.Fill;
		_flightAlertPaint.Color = color.WithAlpha((byte)(255 * alpha));
		canvas.DrawCircle(iconCenter, iconRadius, _flightAlertPaint);
		DrawFlightGlyph(canvas, flightEvent.Kind, iconCenter, iconRadius, alpha);

		float textX = iconCenter.X + iconRadius + OverlayElementBounds.GpsAlertGap;
		SKColor outline = OutlineColorOf(element);
		DrawOutlined(canvas, title, textX, box.Top + OverlayElementBounds.GpsAlertTitleBaseline, titleFont, color, opacity: alpha, outlineColor: outline,
			outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, detail, textX, box.Top + OverlayElementBounds.GpsAlertDetailBaseline, detailFont, TextColorOf(element), opacity: alpha, outlineColor: outline,
			outlineWidthScale: element.OutlineWidth);
	}

	private static string Ellipsize(string text, SKFont font, float maxWidth)
	{
		if (font.MeasureText(text) <= maxWidth) return text;

		int length = text.Length;
		while (length > 1 && font.MeasureText(text.AsSpan(0, length)) + font.MeasureText("…") > maxWidth) length--;
		return text[..length].TrimEnd() + "…";
	}

	/// <summary>The icon's mark in the panel's dark: a drone for a mode, a power symbol for the motors, a battery, "i" for a notice, "!" for a warning.</summary>
	private void DrawFlightGlyph(SKCanvas canvas, FlightEventKind kind, SKPoint center, float radius, float alpha)
	{
		_flightAlertPaint.Color = GpsGlyphColor.WithAlpha((byte)(255 * alpha));
		_flightAlertPaint.StrokeCap = SKStrokeCap.Round;
		_flightAlertPaint.StrokeJoin = SKStrokeJoin.Round;
		float unit = radius / 32;
		switch (kind)
		{
			case FlightEventKind.FlightMode:
				_flightAlertPaint.Style = SKPaintStyle.Stroke;
				_flightAlertPaint.StrokeWidth = 4 * unit;
				canvas.DrawLine(center.X - 10 * unit, center.Y - 10 * unit, center.X + 10 * unit, center.Y + 10 * unit, _flightAlertPaint);
				canvas.DrawLine(center.X - 10 * unit, center.Y + 10 * unit, center.X + 10 * unit, center.Y - 10 * unit, _flightAlertPaint);
				foreach ((float dx, float dy) in FlightRotors)
					canvas.DrawCircle(center.X + dx * 14 * unit, center.Y + dy * 14 * unit, 5.5f * unit, _flightAlertPaint);
				_flightAlertPaint.Style = SKPaintStyle.Fill;
				canvas.DrawCircle(center, 4.5f * unit, _flightAlertPaint);
				break;
			case FlightEventKind.MotorsOn or FlightEventKind.MotorsOff:
				_flightAlertPaint.Style = SKPaintStyle.Stroke;
				_flightAlertPaint.StrokeWidth = 5 * unit;
				canvas.DrawArc(new SKRect(center.X - 13 * unit, center.Y - 13 * unit, center.X + 13 * unit, center.Y + 13 * unit), -60, 300, false, _flightAlertPaint);
				canvas.DrawLine(center.X, center.Y - 18 * unit, center.X, center.Y - 2 * unit, _flightAlertPaint);
				break;
			case FlightEventKind.BatteryStart or FlightEventKind.BatteryLevel or FlightEventKind.BatteryEnd or FlightEventKind.LowVoltage or FlightEventKind.CriticalVoltage:
				_flightAlertPaint.Style = SKPaintStyle.Stroke;
				_flightAlertPaint.StrokeWidth = 4 * unit;
				canvas.DrawRoundRect(new SKRect(center.X - 10 * unit, center.Y - 14 * unit, center.X + 10 * unit, center.Y + 18 * unit), 3 * unit, 3 * unit, _flightAlertPaint);
				_flightAlertPaint.Style = SKPaintStyle.Fill;
				canvas.DrawRect(new SKRect(center.X - 4 * unit, center.Y - 19 * unit, center.X + 4 * unit, center.Y - 14 * unit), _flightAlertPaint);
				canvas.DrawRect(new SKRect(center.X - 6 * unit, kind == FlightEventKind.CriticalVoltage ? center.Y + 12 * unit : center.Y + 2 * unit, center.X + 6 * unit, center.Y + 14 * unit),
					_flightAlertPaint);
				break;
			case FlightEventKind.Warning:
				_flightAlertPaint.Style = SKPaintStyle.Fill;
				canvas.DrawRoundRect(SKRect.Create(center.X - 3.5f * unit, center.Y - 17 * unit, 7 * unit, 22 * unit), 3.5f * unit, 3.5f * unit, _flightAlertPaint);
				canvas.DrawCircle(center.X, center.Y + 13 * unit, 4.2f * unit, _flightAlertPaint);
				break;
			default:
				_flightAlertPaint.Style = SKPaintStyle.Fill;
				canvas.DrawCircle(center.X, center.Y - 14 * unit, 4.2f * unit, _flightAlertPaint);
				canvas.DrawRoundRect(SKRect.Create(center.X - 3.5f * unit, center.Y - 5 * unit, 7 * unit, 22 * unit), 3.5f * unit, 3.5f * unit, _flightAlertPaint);
				break;
		}
	}
}
