using OsmoOverlay.Core.Localization;
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
	// AutoHeight: the change in the series (m, or km/h for the speed) that fills the whole panel, and the lowest the panel goes.
	private const double ProfileFullSpan = 60;
	private const float ProfileMinPanelHeight = 96f;

	private readonly Dictionary<(ProfileSeries, ProfileAxis, bool), ProfileGeometry> _profiles = [];

	private readonly SKPaint _chartFillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
	private readonly SKPaint _chartLinePaint = new()
		{ IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };

	private void DrawProfileChart(SKCanvas canvas, DerivedFrame frame, ProfileChartElement element)
	{
		const float width = OverlayElementBounds.ChartWidth;
		const float top = OverlayElementBounds.ChartTop;
		ProfileGeometry? profile = Profile(element.Series, element.Axis, element.AutoHeight);
		float height = profile?.PanelHeight ?? OverlayElementBounds.ChartHeight;

		// A shorter panel keeps its bottom edge, so the widget doesn't leave the place it was put at.
		canvas.Save();
		canvas.Translate(0, OverlayElementBounds.ChartHeight - height);
		SKColor textColor = TextColorOf(element);
		SKColor accent = AccentColorOf(element);
		SKColor outlineColor = OutlineColorOf(element);

		DrawOutlined(canvas, (element.Label ?? DefaultLabel(element)).ToUpperInvariant(), 0, 0,
			TextFont(element, OverlayElementBounds.LabelFontSize), textColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		(string value, string unit) = element.Series == ProfileSeries.Speed
			? FormatSpeed(frame.SpeedKmh, element.Units)
			: FormatAltitude(ProfileValue(element.Series, frame), element.Units);
		DrawOutlined(canvas, $"{value} {unit}", width, 0, TextFont(element, OverlayElementBounds.SmallFontSize), accent,
			SKTextAlign.Right, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);

		var panel = new SKRect(0, top, width, top + height);
		canvas.DrawRoundRect(panel, ProfileCornerRadius, ProfileCornerRadius, PanelFillPaint(element));
		canvas.DrawRoundRect(panel, ProfileCornerRadius, ProfileCornerRadius, _thinStroke2White70);

		if (profile is null)
		{
			canvas.Restore();
			return;
		}

		const float plotLeft = ProfileInset;
		const float plotTop = top + ProfileInset;
		float markerX = (float)ProfilePosition(frame, profile.Axis) * ProfilePlotWidth;

		canvas.Save();
		canvas.Translate(plotLeft, plotTop);

		_chartFillPaint.Color = accent.WithAlpha(40);
		canvas.DrawPath(profile.Fill, _chartFillPaint);
		float lineScale = Math.Clamp(element.LineWidth, 0.5f, 3f);
		_chartLinePaint.Color = White.WithAlpha(90);
		_chartLinePaint.StrokeWidth = 3 * lineScale;
		canvas.DrawPath(profile.Line, _chartLinePaint);

		canvas.Save();
		canvas.ClipRect(new SKRect(0, 0, markerX, profile.PlotHeight));
		_chartFillPaint.Color = accent.WithAlpha(110);
		canvas.DrawPath(profile.Fill, _chartFillPaint);
		_chartLinePaint.Color = accent;
		_chartLinePaint.StrokeWidth = 4 * lineScale;
		canvas.DrawPath(profile.Line, _chartLinePaint);
		canvas.Restore();

		if (element.ShowMarker)
		{
			float markerY = profile.YOf(ProfileValue(element.Series, frame));
			canvas.DrawLine(markerX, 0, markerX, profile.PlotHeight, _thinStroke2White70);
			canvas.DrawCircle(markerX, markerY, 11, _dotOutlineBlackFill);
			_chartFillPaint.Color = accent;
			canvas.DrawCircle(markerX, markerY, 8, _chartFillPaint);
		}

		if (element.ShowMinMax)
		{
			// The readings' own extremes, small, in the plot's corners - on the left, where the curve starts at the take-off or at rest.
			SKFont small = TextFont(element, OverlayElementBounds.SmallFontSize * 0.75f);
			(string high, string highUnit) = ProfileReading(element, profile.DataMax);
			(string low, string lowUnit) = ProfileReading(element, profile.DataMin);
			DrawOutlined(canvas, $"{high} {highUnit}", 8, small.Size, small, textColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
			DrawOutlined(canvas, $"{low} {lowUnit}", 8, profile.PlotHeight - 8, small, textColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		}

		canvas.Restore();
		canvas.Restore();
	}

	private static (string Value, string Unit) ProfileReading(ProfileChartElement element, double value)
	{
		return element.Series == ProfileSeries.Speed ? FormatSpeed(value, element.Units) : FormatAltitude(value, element.Units);
	}

	public static string DefaultProfileLabel(ProfileSeries series)
	{
		return series switch
		{
			ProfileSeries.Speed => CoreStrings.Overlay_Speed,
			ProfileSeries.Height => CoreStrings.Overlay_Height,
			_ => CoreStrings.Overlay_Elevation
		};
	}

	/// <summary>What the chart plots for a frame: the frame's own reading, so the marker sits on the value the heading shows (the curve is averaged into columns).</summary>
	private static double ProfileValue(ProfileSeries series, DerivedFrame frame)
	{
		return series switch
		{
			ProfileSeries.Speed => frame.SpeedKmh,
			ProfileSeries.Height => frame.Raw.HeightAboveHomeMeters is { } height && double.IsFinite(height) ? height : 0,
			_ => frame.Raw.AltitudeMeters
		};
	}

	/// <summary>0..1 along the chart's X axis - the share of the distance or time covered at `frame`.</summary>
	private double ProfilePosition(DerivedFrame frame, ProfileAxis axis)
	{
		double fraction = axis == ProfileAxis.Distance
			? frame.CumulativeDistanceMeters / _totalDistanceMeters
			: (frame.Raw.SampleTimeSeconds - _allFrames[0].Raw.SampleTimeSeconds) / _totalDurationSeconds;
		return double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
	}

	/// <summary>
	///     Built on first use for a (series, axis) pair and kept until SetFrames. Null when there's nothing to plot - no
	///     frames, or no time passing. A recording that never moves falls back from distance to time rather than show nothing.
	/// </summary>
	private ProfileGeometry? Profile(ProfileSeries series, ProfileAxis axis, bool fit = false)
	{
		if (_totalDistanceMeters < 1) axis = ProfileAxis.Time;
		if (_profiles.TryGetValue((series, axis, fit), out ProfileGeometry? cached)) return cached;
		if (_allFrames.Count < 2 || _totalDurationSeconds <= 0) return null;

		double[] sums = new double[ProfileBuckets];
		int[] counts = new int[ProfileBuckets];
		foreach (DerivedFrame f in _allFrames)
		{
			int bucket = Math.Min((int)(ProfilePosition(f, axis) * ProfileBuckets), ProfileBuckets - 1);
			sums[bucket] += ProfileValue(series, f);
			counts[bucket]++;
		}

		List<(float X, double Value)> samples = [];
		for (int i = 0; i < ProfileBuckets; i++)
		{
			if (counts[i] > 0)
				samples.Add(((i + 0.5f) / ProfileBuckets, sums[i] / counts[i]));
		}

		double min, max;
		float panelHeight = OverlayElementBounds.ChartHeight;
		if (fit)
		{
			// Scaled to the change the series really has, with a little room each side, in a panel as tall as that change asks for.
			double low = samples.Min(s => s.Value), high = samples.Max(s => s.Value), change = Math.Max(high - low, 1);
			double room = change * 0.1;
			min = low - room;
			max = high + room;
			panelHeight = (float)Math.Clamp(OverlayElementBounds.ChartHeight * change / ProfileFullSpan, ProfileMinPanelHeight, OverlayElementBounds.ChartHeight);
		}
		else if (series == ProfileSeries.Speed)
		{
			min = 0;
			max = Math.Max(samples.Max(s => s.Value) * 1.1, 1);
		}
		else if (series == ProfileSeries.Height)
		{
			// Rests on the ground: the take-off point is the chart's floor, the highest point has room above it.
			min = Math.Min(0, samples.Min(s => s.Value));
			max = Math.Max(samples.Max(s => s.Value) * 1.1, min + ProfileMinElevationSpanMeters);
		}
		else
		{
			min = samples.Min(s => s.Value);
			max = samples.Max(s => s.Value);
			double span = Math.Max(max - min, ProfileMinElevationSpanMeters);
			double mid = (min + max) / 2;
			min = mid - span / 2;
			max = mid + span * 0.6;
		}

		float plotHeight = panelHeight - ProfileInset * 2;
		SKPoint[] points =
		[
			.. samples.Select(s => new SKPoint(s.X * ProfilePlotWidth, (1f - (float)((s.Value - min) / (max - min))) * plotHeight))
		];
		// The line reaches both edges, so the start and the end of the ride sit on the chart's sides.
		points = [new SKPoint(0, points[0].Y), .. points, new SKPoint(ProfilePlotWidth, points[^1].Y)];

		var geometry = new ProfileGeometry(axis, points, min, max, panelHeight, samples.Min(s => s.Value), samples.Max(s => s.Value));
		_profiles[(series, axis, fit)] = geometry;
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

		private readonly double _min, _max;

		public ProfileGeometry(ProfileAxis axis, SKPoint[] points, double min, double max, float panelHeight, double dataMin, double dataMax)
		{
			Axis = axis;
			_points = points;
			_min = min;
			_max = max;
			PanelHeight = panelHeight;
			DataMin = dataMin;
			DataMax = dataMax;
			PlotHeight = panelHeight - ProfileInset * 2;

			using var line = new SKPathBuilder();
			using var fill = new SKPathBuilder();
			line.MoveTo(points[0]);
			fill.MoveTo(0, PlotHeight);
			foreach (SKPoint point in points)
			{
				line.LineTo(point);
				fill.LineTo(point);
			}

			fill.LineTo(ProfilePlotWidth, PlotHeight);
			fill.Close();
			Line = line.Detach();
			Fill = fill.Detach();
		}

		public ProfileAxis Axis { get; }
		public float PanelHeight { get; }
		/// <summary>The lowest and highest reading, not the scale's ends (which have room around them).</summary>
		public double DataMin { get; }
		public double DataMax { get; }
		public float PlotHeight { get; }
		public SKPath Line { get; }
		public SKPath Fill { get; }

		/// <summary>The plot's Y for a value on the chart's scale, kept inside the plot.</summary>
		public float YOf(double value)
		{
			return Math.Clamp((1f - (float)((value - _min) / (_max - _min))) * PlotHeight, 0, PlotHeight);
		}

		/// <summary>The curve's Y at `x`, between the two points around it.</summary>
		public float YAt(float x)
		{
			int i = 1;
			while (i < _points.Length - 1 && _points[i].X < x) i++;
			SKPoint a = _points[i - 1], b = _points[i];
			float t = b.X > a.X ? Math.Clamp((x - a.X) / (b.X - a.X), 0, 1) : 0;
			return a.Y + (b.Y - a.Y) * t;
		}

		public void Dispose()
		{
			Line.Dispose();
			Fill.Dispose();
		}
	}
}
