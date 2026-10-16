using System.Globalization;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     The GPS alert (GpsAlertElement): a panel that says what the GPS is doing while there's something to say - searching for
///     a first fix, the signal lost (for how long, the position held), back (after how long), or a reading the processing
///     corrected (GpsOutliers) - and fades out a few seconds after. Nothing the rest of the time. The notices come from the
///     whole set of frames once (GpsAlerts), so a frame draws the same whatever was drawn before it.
/// </summary>
public sealed partial class OverlayRenderer
{
	// How long "back" and "corrected" stay up, and how long a notice takes to fade in and out.
	private const double GpsBackSeconds = 2.5;
	private const double GpsCorrectedSeconds = 3;
	private const double GpsAlertFadeSeconds = 0.35;

	private static readonly SKColor GpsSearchingColor = SKColor.Parse("#4FC3F7");
	private static readonly SKColor GpsLostColor = SKColor.Parse("#F7C51E");
	private static readonly SKColor GpsBackColor = SKColor.Parse("#3DDC84");
	private static readonly SKColor GpsCorrectedColor = SKColor.Parse("#FF8A3D");
	private static readonly SKColor GpsGlyphColor = SKColor.Parse("#111317");

	private readonly SKPaint _gpsAlertPaint = new() { IsAntialias = true };
	private GpsAlerts? _gpsAlerts;

	private enum GpsNotice
	{
		Searching,
		Lost,
		Found,
		Back,
		Corrected
	}

	/// <summary>One notice: from Start until End, about what began at Since and lasted Lasted (a loss's length, for "back").</summary>
	private readonly record struct GpsAlertEvent(GpsNotice Notice, double Start, double End, double Since, double Lasted, GpsCorrection What);

	/// <summary>The notices in time order, and the stretches they make together (a loss and its "back" run on) - what fades in and out.</summary>
	private sealed record GpsAlerts(List<GpsAlertEvent> Events, List<(double Start, double End)> Shown);

	private void DrawGpsAlert(SKCanvas canvas, DerivedFrame frame, GpsAlertElement element)
	{
		double t = frame.Raw.SampleTimeSeconds;
		GpsAlerts alerts = _gpsAlerts ??= FindGpsAlerts(_allFrames);
		if (ActiveGpsNotice(alerts, t) is not { } notice) return;

		float alpha = 1f;
		foreach ((double start, double end) in alerts.Shown)
		{
			if (t < start || t >= end) continue;

			alpha = (float)Math.Clamp(Math.Min((t - start) / GpsAlertFadeSeconds, (end - t) / GpsAlertFadeSeconds), 0, 1);
			break;
		}

		if (alpha <= 0f) return;

		(SKColor color, string title, string detail) = GpsNoticeText(notice, t);
		SKRect box = OverlayElementBounds.GpsAlertBox(element.FontFamily);
		const float radius = OverlayElementBounds.GpsAlertHeight / 2;

		SKPaint fill = PanelFillPaint(element);
		fill.Color = fill.Color.WithAlpha((byte)(fill.Color.Alpha * alpha));
		canvas.DrawRoundRect(box, radius, radius, fill);

		_gpsAlertPaint.Style = SKPaintStyle.Stroke;
		_gpsAlertPaint.StrokeWidth = 3;
		_gpsAlertPaint.Color = color.WithAlpha((byte)(150 * alpha));
		canvas.DrawRoundRect(SKRect.Inflate(box, -1.5f, -1.5f), radius - 1.5f, radius - 1.5f, _gpsAlertPaint);

		float iconRadius = OverlayElementBounds.GpsAlertIcon / 2;
		var iconCenter = new SKPoint(box.Left + OverlayElementBounds.GpsAlertGap + iconRadius, box.MidY);
		_gpsAlertPaint.Style = SKPaintStyle.Fill;
		_gpsAlertPaint.Color = color.WithAlpha((byte)(255 * alpha));
		canvas.DrawCircle(iconCenter, iconRadius, _gpsAlertPaint);
		DrawGpsGlyph(canvas, notice.Notice, iconCenter, iconRadius, alpha);

		float textX = iconCenter.X + iconRadius + OverlayElementBounds.GpsAlertGap;
		SKColor outline = OutlineColorOf(element);
		DrawOutlined(canvas, title, textX, box.Top + 48, TextFont(element, OverlayElementBounds.GpsAlertTitleSize), color, opacity: alpha,
			outlineColor: outline, outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, detail, textX, box.Top + 87, TextFont(element, OverlayElementBounds.GpsAlertDetailSize), TextColorOf(element), opacity: alpha,
			outlineColor: outline, outlineWidthScale: element.OutlineWidth);
	}

	/// <summary>The notice up at `t`: a loss while it lasts, otherwise the newest - a correction right after the signal came back is the news.</summary>
	private static GpsAlertEvent? ActiveGpsNotice(GpsAlerts alerts, double t)
	{
		GpsAlertEvent? best = null;
		foreach (GpsAlertEvent alert in alerts.Events)
		{
			if (t < alert.Start || t >= alert.End) continue;
			if (alert.Notice is GpsNotice.Searching or GpsNotice.Lost) return alert;
			if (best is not { } current || alert.Start >= current.Start) best = alert;
		}

		return best;
	}

	private static (SKColor Color, string Title, string Detail) GpsNoticeText(GpsAlertEvent notice, double t)
	{
		return notice.Notice switch
		{
			GpsNotice.Searching => (GpsSearchingColor, "SEARCHING FOR GPS", $"NO POSITION YET  ·  {Clock(t - notice.Since)}"),
			GpsNotice.Lost => (GpsLostColor, "GPS SIGNAL LOST", $"LAST POSITION HELD  ·  {Clock(t - notice.Since)}"),
			GpsNotice.Found => (GpsBackColor, "GPS FIX ACQUIRED", $"AFTER {Seconds(notice.Lasted)}"),
			GpsNotice.Back => (GpsBackColor, "GPS SIGNAL BACK", $"AFTER {Seconds(notice.Lasted)}"),
			_ => (GpsCorrectedColor, "GPS CORRECTED", notice.What switch
			{
				GpsCorrection.Position => "POSITION JUMP REMOVED",
				GpsCorrection.Speed => "SPEED SPIKE REMOVED",
				_ => OverlayElementBounds.GpsAlertLongestDetail
			})
		};

		static string Clock(double seconds)
		{
			var span = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
			return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
		}

		static string Seconds(double seconds)
		{
			return seconds.ToString("0.0", CultureInfo.InvariantCulture) + " S";
		}
	}

	/// <summary>The icon's mark in the panel's dark: signal waves while searching, "!" lost, a tick back, a target corrected.</summary>
	private void DrawGpsGlyph(SKCanvas canvas, GpsNotice notice, SKPoint center, float radius, float alpha)
	{
		_gpsAlertPaint.Color = GpsGlyphColor.WithAlpha((byte)(255 * alpha));
		_gpsAlertPaint.StrokeCap = SKStrokeCap.Round;
		_gpsAlertPaint.StrokeJoin = SKStrokeJoin.Round;
		float unit = radius / 32;
		switch (notice)
		{
			case GpsNotice.Searching:
				_gpsAlertPaint.Style = SKPaintStyle.Stroke;
				_gpsAlertPaint.StrokeWidth = 4.5f * unit;
				var dot = new SKPoint(center.X, center.Y + 12 * unit);
				foreach (float r in new[] { 10f, 19f })
				{
					var arc = new SKRect(dot.X - r * unit, dot.Y - r * unit, dot.X + r * unit, dot.Y + r * unit);
					canvas.DrawArc(arc, 225, 90, false, _gpsAlertPaint);
				}

				_gpsAlertPaint.Style = SKPaintStyle.Fill;
				canvas.DrawCircle(dot, 4 * unit, _gpsAlertPaint);
				break;
			case GpsNotice.Lost:
				_gpsAlertPaint.Style = SKPaintStyle.Fill;
				canvas.DrawRoundRect(SKRect.Create(center.X - 3.5f * unit, center.Y - 17 * unit, 7 * unit, 22 * unit), 3.5f * unit, 3.5f * unit, _gpsAlertPaint);
				canvas.DrawCircle(center.X, center.Y + 13 * unit, 4.2f * unit, _gpsAlertPaint);
				break;
			case GpsNotice.Found or GpsNotice.Back:
				_gpsAlertPaint.Style = SKPaintStyle.Stroke;
				_gpsAlertPaint.StrokeWidth = 6 * unit;
				canvas.DrawLine(center.X - 12 * unit, center.Y + 1 * unit, center.X - 3 * unit, center.Y + 10 * unit, _gpsAlertPaint);
				canvas.DrawLine(center.X - 3 * unit, center.Y + 10 * unit, center.X + 13 * unit, center.Y - 9 * unit, _gpsAlertPaint);
				break;
			default:
				_gpsAlertPaint.Style = SKPaintStyle.Stroke;
				_gpsAlertPaint.StrokeWidth = 4 * unit;
				canvas.DrawCircle(center, 10 * unit, _gpsAlertPaint);
				foreach ((float dx, float dy) in new[] { (0f, -1f), (0f, 1f), (-1f, 0f), (1f, 0f) })
					canvas.DrawLine(center.X + dx * 14 * unit, center.Y + dy * 14 * unit, center.X + dx * 20 * unit, center.Y + dy * 20 * unit, _gpsAlertPaint);
				_gpsAlertPaint.Style = SKPaintStyle.Fill;
				canvas.DrawCircle(center, 3.5f * unit, _gpsAlertPaint);
				break;
		}
	}

	/// <summary>
	///     Every notice over the frames: each stretch without a fix (searching before a file's first one - the recording's start
	///     or after a stop - lost otherwise) and its end ("back"), and each stretch of corrected readings. Never across a gap
	///     between files.
	/// </summary>
	private static GpsAlerts FindGpsAlerts(IReadOnlyList<DerivedFrame> frames)
	{
		List<GpsAlertEvent> events = [];
		int lossStart = -1, correctedStart = -1;
		bool hadFix = false;
		GpsCorrection what = GpsCorrection.None;
		for (int i = 0; i <= frames.Count; i++)
		{
			TelemetryFrame? raw = i < frames.Count ? frames[i].Raw : null;
			bool newFile = raw is { StartsAfterGap: true };

			if (lossStart >= 0 && (raw is null || raw.HasGpsFix || newFile))
			{
				double start = frames[lossStart].Raw.SampleTimeSeconds, end = raw?.SampleTimeSeconds ?? frames[^1].Raw.SampleTimeSeconds;
				events.Add(new GpsAlertEvent(hadFix ? GpsNotice.Lost : GpsNotice.Searching, start, end, start, 0, GpsCorrection.None));
				if (raw is { HasGpsFix: true } && !newFile)
					events.Add(new GpsAlertEvent(hadFix ? GpsNotice.Back : GpsNotice.Found, end, end + GpsBackSeconds, end, end - start, GpsCorrection.None));
				lossStart = -1;
			}

			if (correctedStart >= 0 && (raw is null || raw.GpsCorrected == GpsCorrection.None || newFile))
			{
				double start = frames[correctedStart].Raw.SampleTimeSeconds, last = frames[i - 1].Raw.SampleTimeSeconds;
				events.Add(new GpsAlertEvent(GpsNotice.Corrected, start, last + GpsCorrectedSeconds, start, 0, what));
				correctedStart = -1;
				what = GpsCorrection.None;
			}

			if (raw is null) break;
			if (newFile) hadFix = false;

			if (!raw.HasGpsFix && lossStart < 0) lossStart = i;
			if (raw.HasGpsFix) hadFix = true;
			if (raw.GpsCorrected != GpsCorrection.None)
			{
				if (correctedStart < 0) correctedStart = i;
				what |= raw.GpsCorrected;
			}
		}

		events.Sort((a, b) => a.Start.CompareTo(b.Start));
		List<(double Start, double End)> shown = [];
		foreach (GpsAlertEvent alert in events)
		{
			if (shown.Count > 0 && alert.Start <= shown[^1].End) shown[^1] = (shown[^1].Start, Math.Max(shown[^1].End, alert.End));
			else shown.Add((alert.Start, alert.End));
		}

		return new GpsAlerts(events, shown);
	}
}
