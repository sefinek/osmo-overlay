using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     ProfileChart: the whole recording's elevation or speed over distance or time, the part already covered
///     highlighted and a marker at the frame shown. The curve is averaged into ProfileBuckets columns once per set of
///     frames and kept as paths in the plot's own pixels (ProfileGeometry, the plot's size is fixed), so a frame only
///     clips and draws them - not scaled at draw time, which would stretch the line's width with the plot's aspect.
/// </summary>
public sealed partial class OverlayRenderer
{
	private const int ProfileBuckets = 300;
	private const float ProfileCornerRadius = 14f;
	private const float ProfileInset = 14f;
	private const float ProfilePlotWidth = OverlayElementBounds.ChartWidth - ProfileInset * 2;
	private const float ProfilePlotHeight = OverlayElementBounds.ChartHeight - ProfileInset * 2;
	// A flat ride would otherwise stretch a metre of GPS wobble over the chart's full height.
	private const double ProfileMinElevationSpanMeters = 20;

	private readonly Dictionary<(ProfileSeries, ProfileAxis), ProfileGeometry> _profiles = [];

	private readonly SKPaint _chartFillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
	private readonly SKPaint _chartLinePaint = new()
		{ IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };

	private void DrawProfileChart(SKCanvas canvas, DerivedFrame frame, ProfileChartElement element)
	{
		const float width = OverlayElementBounds.ChartWidth;
		const float top = OverlayElementBounds.ChartTop;
		const float height = OverlayElementBounds.ChartHeight;

		SKColor textColor = TextColorOf(element);
		SKColor accent = AccentColorOf(element);
		SKColor outlineColor = OutlineColorOf(element);

		DrawOutlined(canvas, (element.Label ?? DefaultProfileLabel(element.Series)).ToUpperInvariant(), 0, 0,
			TextFont(element, OverlayElementBounds.LabelFontSize), textColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		var (value, unit) = element.Series == ProfileSeries.Speed
			? FormatSpeed(frame.SpeedKmh, element.Units)
			: FormatAltitude(frame.Raw.AltitudeMeters, element.Units);
		DrawOutlined(canvas, $"{value} {unit}", width, 0, TextFont(element, OverlayElementBounds.SmallFontSize), accent,
			SKTextAlign.Right, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);

		var panel = new SKRect(0, top, width, top + height);
		canvas.DrawRoundRect(panel, ProfileCornerRadius, ProfileCornerRadius, _panelFillPaint);
		canvas.DrawRoundRect(panel, ProfileCornerRadius, ProfileCornerRadius, _thinStroke2White70);

		if (Profile(element.Series, element.Axis) is not { } profile) return;

		const float plotLeft = ProfileInset;
		const float plotTop = top + ProfileInset;
		var markerX = (float)ProfilePosition(frame, profile.Axis) * ProfilePlotWidth;

		canvas.Save();
		canvas.Translate(plotLeft, plotTop);

		_chartFillPaint.Color = accent.WithAlpha(40);
		canvas.DrawPath(profile.Fill, _chartFillPaint);
		_chartLinePaint.Color = White.WithAlpha(90);
		_chartLinePaint.StrokeWidth = 3;
		canvas.DrawPath(profile.Line, _chartLinePaint);

		canvas.Save();
		canvas.ClipRect(new SKRect(0, 0, markerX, ProfilePlotHeight));
		_chartFillPaint.Color = accent.WithAlpha(110);
		canvas.DrawPath(profile.Fill, _chartFillPaint);
		_chartLinePaint.Color = accent;
		_chartLinePaint.StrokeWidth = 4;
		canvas.DrawPath(profile.Line, _chartLinePaint);
		canvas.Restore();

		var markerY = profile.YAt(markerX);
		canvas.DrawLine(markerX, 0, markerX, ProfilePlotHeight, _thinStroke2White70);
		canvas.DrawCircle(markerX, markerY, 11, _dotOutlineBlackFill);
		_chartFillPaint.Color = accent;
		canvas.DrawCircle(markerX, markerY, 8, _chartFillPaint);
		canvas.Restore();
	}

	public static string DefaultProfileLabel(ProfileSeries series)
	{
		return series == ProfileSeries.Speed ? "SPEED" : "ELEVATION";
	}

	/// <summary>0..1 along the chart's X axis - the share of the distance or time covered at `frame`.</summary>
	private double ProfilePosition(DerivedFrame frame, ProfileAxis axis)
	{
		var fraction = axis == ProfileAxis.Distance
			? frame.CumulativeDistanceMeters / _totalDistanceMeters
			: (frame.Raw.SampleTimeSeconds - _allFrames[0].Raw.SampleTimeSeconds) / _totalDurationSeconds;
		return double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
	}

	/// <summary>
	///     Built on first use for a (series, axis) pair and kept until SetFrames. Null when there's nothing to plot - no
	///     frames, or no time passing. A recording that never moves falls back from distance to time rather than show nothing.
	/// </summary>
	private ProfileGeometry? Profile(ProfileSeries series, ProfileAxis axis)
	{
		if (_totalDistanceMeters < 1) axis = ProfileAxis.Time;
		if (_profiles.TryGetValue((series, axis), out ProfileGeometry? cached)) return cached;
		if (_allFrames.Count < 2 || _totalDurationSeconds <= 0) return null;

		var sums = new double[ProfileBuckets];
		var counts = new int[ProfileBuckets];
		foreach (DerivedFrame f in _allFrames)
		{
			var bucket = Math.Min((int)(ProfilePosition(f, axis) * ProfileBuckets), ProfileBuckets - 1);
			sums[bucket] += series == ProfileSeries.Speed ? f.SpeedKmh : f.Raw.AltitudeMeters;
			counts[bucket]++;
		}

		List<(float X, double Value)> samples = [];
		for (var i = 0; i < ProfileBuckets; i++)
			if (counts[i] > 0)
				samples.Add(((i + 0.5f) / ProfileBuckets, sums[i] / counts[i]));

		double min, max;
		if (series == ProfileSeries.Speed)
		{
			min = 0;
			max = Math.Max(samples.Max(s => s.Value) * 1.1, 1);
		}
		else
		{
			min = samples.Min(s => s.Value);
			max = samples.Max(s => s.Value);
			var span = Math.Max(max - min, ProfileMinElevationSpanMeters);
			var mid = (min + max) / 2;
			min = mid - span / 2;
			max = mid + span * 0.6;
		}

		SKPoint[] points =
		[
			.. samples.Select(s => new SKPoint(s.X * ProfilePlotWidth, (1f - (float)((s.Value - min) / (max - min))) * ProfilePlotHeight))
		];
		// The line reaches both edges, so the start and the end of the ride sit on the chart's sides.
		points = [new SKPoint(0, points[0].Y), .. points, new SKPoint(ProfilePlotWidth, points[^1].Y)];

		var geometry = new ProfileGeometry(axis, points);
		_profiles[(series, axis)] = geometry;
		return geometry;
	}

	private void ClearProfiles()
	{
		foreach (ProfileGeometry profile in _profiles.Values) profile.Dispose();
		_profiles.Clear();
	}

	/// <summary>The curve in the plot's pixels, (0, 0) its top-left.</summary>
	private sealed class ProfileGeometry : IDisposable
	{
		private readonly SKPoint[] _points;

		public ProfileGeometry(ProfileAxis axis, SKPoint[] points)
		{
			Axis = axis;
			_points = points;

			using var line = new SKPathBuilder();
			using var fill = new SKPathBuilder();
			line.MoveTo(points[0]);
			fill.MoveTo(0, ProfilePlotHeight);
			foreach (SKPoint point in points)
			{
				line.LineTo(point);
				fill.LineTo(point);
			}

			fill.LineTo(ProfilePlotWidth, ProfilePlotHeight);
			fill.Close();
			Line = line.Detach();
			Fill = fill.Detach();
		}

		public ProfileAxis Axis { get; }
		public SKPath Line { get; }
		public SKPath Fill { get; }

		/// <summary>The curve's Y at `x`, between the two points around it.</summary>
		public float YAt(float x)
		{
			var i = 1;
			while (i < _points.Length - 1 && _points[i].X < x) i++;
			SKPoint a = _points[i - 1], b = _points[i];
			var t = b.X > a.X ? Math.Clamp((x - a.X) / (b.X - a.X), 0, 1) : 0;
			return a.Y + (b.Y - a.Y) * t;
		}

		public void Dispose()
		{
			Line.Dispose();
			Fill.Dispose();
		}
	}
}
