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

	// A shorter stretch without a fix says nothing: it would be over before the panel faded in, leaving "back after 0.0 s".
	internal const double GpsLossMinSeconds = 1;

	private const string GpsSearchingTitle = "SEARCHING FOR GPS";
	private const string GpsLostTitle = "GPS SIGNAL LOST";
	private const string GpsFoundTitle = "GPS FIX ACQUIRED";
	private const string GpsBackTitle = "GPS SIGNAL BACK";
	private const string GpsCorrectedTitle = "GPS CORRECTED";
	private const string GpsSearchingDetail = "NO POSITION YET  ·  {0}";
	private const string GpsLostDetail = "LAST POSITION HELD  ·  {0}";
	private const string GpsAfterDetail = "AFTER {0} S";
	private const string GpsPositionDetail = "POSITION JUMP REMOVED";
	private const string GpsSpeedDetail = "SPEED SPIKE REMOVED";
	private const string GpsPositionAndSpeedDetail = "POSITION AND SPEED JUMPS REMOVED";

	/// <summary>Every title, and every detail at its widest - what OverlayElementBounds.GpsAlertBox fits the panel to.</summary>
	internal static readonly string[] GpsAlertTitles = [GpsSearchingTitle, GpsLostTitle, GpsFoundTitle, GpsBackTitle, GpsCorrectedTitle];

	internal static readonly string[] GpsAlertDetails =
	[
		string.Format(CultureInfo.InvariantCulture, GpsSearchingDetail, "00:00"),
		string.Format(CultureInfo.InvariantCulture, GpsLostDetail, "00:00"),
		string.Format(CultureInfo.InvariantCulture, GpsAfterDetail, "000.0"),
		GpsPositionDetail, GpsSpeedDetail, GpsPositionAndSpeedDetail
	];

	private static readonly SKColor GpsSearchingColor = SKColor.Parse("#4FC3F7");
	private static readonly SKColor GpsLostColor = SKColor.Parse("#F7C51E");
	private static readonly SKColor GpsBackColor = SKColor.Parse("#3DDC84");
	private static readonly SKColor GpsCorrectedColor = SKColor.Parse("#FF8A3D");
	private static readonly SKColor GpsGlyphColor = SKColor.Parse("#111317");

	private static readonly float[] GpsSearchingArcRadii = [10f, 19f];
	private static readonly (float X, float Y)[] GpsTargetTicks = [(0f, -1f), (0f, 1f), (-1f, 0f), (1f, 0f)];

	private readonly SKPaint _gpsAlertPaint = new() { IsAntialias = true };
	private GpsAlerts? _gpsAlerts;
	private (string? Family, SKRect Box)? _gpsAlertBox;

	internal enum GpsNotice
	{
		Searching,
		Lost,
		Found,
		Back,
		Corrected
	}

	/// <summary>
	///     One notice, up from Start until End on the frames' timeline (the output's, after cuts). Since (a loss) and Lasted (how
	///     long the loss before "back" was) are in recording seconds, so a cut through a loss doesn't make it look shorter.
	/// </summary>
	internal readonly record struct GpsAlertEvent(GpsNotice Notice, double Start, double End, double Since, double Lasted, GpsCorrection What);

	/// <summary>The notices in time order, and the stretches they make together (a loss and its "back" run on) - what fades in and out.</summary>
	internal sealed record GpsAlerts(List<GpsAlertEvent> Events, List<(double Start, double End)> Shown);

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

		if (_gpsAlertBox is not { } cached || cached.Family != element.FontFamily)
			_gpsAlertBox = cached = (element.FontFamily, OverlayElementBounds.GpsAlertBox(element.FontFamily));
		SKRect box = cached.Box;
		const float radius = OverlayElementBounds.GpsAlertHeight / 2;

		(SKColor color, string title, string detail) = GpsNoticeText(notice, frame.Raw.RecordingTimeSeconds);

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
		DrawOutlined(canvas, title, textX, box.Top + OverlayElementBounds.GpsAlertTitleBaseline,
			TextFont(element, OverlayElementBounds.GpsAlertTitleSize), color, opacity: alpha, outlineColor: outline, outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, detail, textX, box.Top + OverlayElementBounds.GpsAlertDetailBaseline,
			TextFont(element, OverlayElementBounds.GpsAlertDetailSize), TextColorOf(element), opacity: alpha, outlineColor: outline,
			outlineWidthScale: element.OutlineWidth);
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

	/// <summary>The notice's color and lines at `recordingSeconds` - a loss counts in recording time, like its Since.</summary>
	private static (SKColor Color, string Title, string Detail) GpsNoticeText(GpsAlertEvent notice, double recordingSeconds)
	{
		return notice.Notice switch
		{
			GpsNotice.Searching => (GpsSearchingColor, GpsSearchingTitle, Format(GpsSearchingDetail, Clock(recordingSeconds - notice.Since))),
			GpsNotice.Lost => (GpsLostColor, GpsLostTitle, Format(GpsLostDetail, Clock(recordingSeconds - notice.Since))),
			GpsNotice.Found => (GpsBackColor, GpsFoundTitle, Format(GpsAfterDetail, notice.Lasted.ToString("0.0", CultureInfo.InvariantCulture))),
			GpsNotice.Back => (GpsBackColor, GpsBackTitle, Format(GpsAfterDetail, notice.Lasted.ToString("0.0", CultureInfo.InvariantCulture))),
			_ => (GpsCorrectedColor, GpsCorrectedTitle, notice.What switch
			{
				GpsCorrection.Position => GpsPositionDetail,
				GpsCorrection.Speed => GpsSpeedDetail,
				_ => GpsPositionAndSpeedDetail
			})
		};

		static string Format(string format, string value)
		{
			return string.Format(CultureInfo.InvariantCulture, format, value);
		}

		static string Clock(double seconds)
		{
			var span = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
			return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
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
				foreach (float r in GpsSearchingArcRadii)
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
				foreach ((float dx, float dy) in GpsTargetTicks)
					canvas.DrawLine(center.X + dx * 14 * unit, center.Y + dy * 14 * unit, center.X + dx * 20 * unit, center.Y + dy * 20 * unit, _gpsAlertPaint);
				_gpsAlertPaint.Style = SKPaintStyle.Fill;
				canvas.DrawCircle(center, 3.5f * unit, _gpsAlertPaint);
				break;
		}
	}

	/// <summary>
	///     Every notice over the frames: each stretch without a fix of at least GpsLossMinSeconds (searching before a file's
	///     first one - the recording's start or after a stop - lost otherwise) and its end ("back"), and each run of corrected
	///     readings - those closer together than a notice stays up are one notice. Never across a gap between files.
	/// </summary>
	internal static GpsAlerts FindGpsAlerts(IReadOnlyList<DerivedFrame> frames)
	{
		List<GpsAlertEvent> events = [];
		int lossStart = -1, correctedStart = -1, lastCorrected = -1;
		bool hadFix = false;
		GpsCorrection what = GpsCorrection.None;
		for (int i = 0; i <= frames.Count; i++)
		{
			TelemetryFrame? raw = i < frames.Count ? frames[i].Raw : null;
			bool newFile = raw is { StartsAfterGap: true };

			if (lossStart >= 0 && (raw is null || raw.HasGpsFix || newFile))
			{
				TelemetryFrame first = frames[lossStart].Raw, until = raw ?? frames[^1].Raw;
				double lasted = until.RecordingTimeSeconds - first.RecordingTimeSeconds;
				if (lasted >= GpsLossMinSeconds)
				{
					events.Add(new GpsAlertEvent(hadFix ? GpsNotice.Lost : GpsNotice.Searching, first.SampleTimeSeconds, until.SampleTimeSeconds,
						first.RecordingTimeSeconds, 0, GpsCorrection.None));
					if (raw is { HasGpsFix: true } && !newFile)
						events.Add(new GpsAlertEvent(hadFix ? GpsNotice.Back : GpsNotice.Found, raw.SampleTimeSeconds, raw.SampleTimeSeconds + GpsBackSeconds,
							0, lasted, GpsCorrection.None));
				}

				lossStart = -1;
			}

			if (correctedStart >= 0 && (raw is null || raw.GpsCorrected == GpsCorrection.None || newFile))
			{
				double start = frames[correctedStart].Raw.SampleTimeSeconds, end = frames[i - 1].Raw.SampleTimeSeconds + GpsCorrectedSeconds;
				// Merged only with the notice still up and nothing else since, so a loss in between keeps its place.
				if (lastCorrected >= 0 && lastCorrected == events.Count - 1 && start <= events[lastCorrected].End)
				{
					events[lastCorrected] = events[lastCorrected] with { End = end, What = events[lastCorrected].What | what };
				}
				else
				{
					events.Add(new GpsAlertEvent(GpsNotice.Corrected, start, end, 0, 0, what));
					lastCorrected = events.Count - 1;
				}

				correctedStart = -1;
				what = GpsCorrection.None;
			}

			if (raw is null) break;

			if (newFile)
			{
				hadFix = false;
				lastCorrected = -1;
			}

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
