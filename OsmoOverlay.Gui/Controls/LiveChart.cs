using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OsmoOverlay.Gui;

/// <summary>
///     Lines over the last WindowSeconds, drawn as values arrive - the performance test's live readings. The scale runs from
///     0 to Maximum, or to the highest value shown (and Reference) when Maximum is NaN; Reference is a dashed line (the
///     recording's frame rate). Colors are the palette's, read at every draw so a theme change recolors them.
/// </summary>
public sealed class LiveChart : Control
{
	private const double StrokeWidth = 1.6;
	private readonly List<Series> _series = [];

	public double WindowSeconds { get; set; } = 30;
	public double Maximum { get; set; } = double.NaN;
	public double? Reference { get; set; }

	/// <summary>Room left above the plot for a title laid over the chart's top edge - the lines stay under it.</summary>
	public double PlotTop { get; set; }

	/// <summary>A new line in the palette's `brush` - its index for Add.</summary>
	public int AddSeries(Func<IBrush> brush)
	{
		_series.Add(new Series(brush));
		return _series.Count - 1;
	}

	public void Add(int series, double seconds, double? value)
	{
		List<(double Seconds, double? Value)> points = _series[series].Points;
		points.Add((seconds, value is { } v && double.IsFinite(v) ? Math.Max(0, v) : null));
		while (points.Count > 2 && points[1].Seconds < seconds - WindowSeconds) points.RemoveAt(0);
		InvalidateVisual();
	}

	public void Clear()
	{
		foreach (Series series in _series) series.Points.Clear();
		InvalidateVisual();
	}

	public override void Render(DrawingContext context)
	{
		var bounds = new Rect(Bounds.Size);
		context.FillRectangle(Palette.SurfaceSunken, bounds, 6);
		if (bounds.Width < 2 || bounds.Height < 2) return;

		double plotHeight = Math.Max(1, bounds.Height - PlotTop);
		var grid = new Pen(Palette.Stroke);
		for (int i = 1; i < 4; i++)
		{
			double y = Math.Round(PlotTop + plotHeight * i / 4) + 0.5;
			context.DrawLine(grid, new Point(0, y), new Point(bounds.Width, y));
		}

		double end = _series.Max(s => s.Points.Count > 0 ? s.Points[^1].Seconds : 0);
		double start = end - WindowSeconds;
		double top = double.IsNaN(Maximum)
			? Math.Max(Reference ?? 0, _series.SelectMany(s => s.Points).Where(p => p.Seconds >= start).Select(p => p.Value ?? 0).DefaultIfEmpty(0).Max()) * 1.15
			: Maximum;
		if (top <= 0) return;

		Point At(double seconds, double value)
		{
			return new Point((seconds - start) / WindowSeconds * bounds.Width, bounds.Height - Math.Min(value, top) / top * (plotHeight - 2) - 1);
		}

		if (Reference is { } reference)
		{
			var dashed = new Pen(Palette.TextMuted, 1, new DashStyle([4, 4], 0));
			double y = At(start, reference).Y;
			context.DrawLine(dashed, new Point(0, y), new Point(bounds.Width, y));
		}

		using (context.PushClip(bounds))
		{
			foreach (Series series in _series)
			{
				var pen = new Pen(series.Brush(), StrokeWidth, lineJoin: PenLineJoin.Round);
				var geometry = new StreamGeometry();
				using (StreamGeometryContext path = geometry.Open())
				{
					bool open = false;
					foreach ((double seconds, double? value) in series.Points)
					{
						if (value is not { } v)
						{
							if (open) path.EndFigure(false);
							open = false;
							continue;
						}

						if (open) path.LineTo(At(seconds, v));
						else path.BeginFigure(At(seconds, v), false);
						open = true;
					}

					if (open) path.EndFigure(false);
				}

				context.DrawGeometry(null, pen, geometry);
			}
		}
	}

	private sealed record Series(Func<IBrush> Brush)
	{
		public List<(double Seconds, double? Value)> Points { get; } = [];
	}
}
