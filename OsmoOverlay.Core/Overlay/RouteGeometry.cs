using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     A drawn route's paths, extended point by point instead of rebuilt every frame: the compass and map trails
///     only grow while playing (a seek backwards resets them), so a frame costs a few path draws instead of one
///     LineTo per point of the whole route. Kept in the caller's own space (meters, map pixels) and drawn through a
///     uniform-scale matrix, with the stroke width divided by that scale so it stays in canvas pixels.
///     Travelled segments go into one path, or with ColorBySpeed one per SpeedColorScale bucket (drawn slow to
///     fast, so the faster parts stay on top where the route crosses itself); a join across a cut is left out,
///     dashed or drawn straight on per RouteJoin.
///     Two things keep a long route (tens of thousands of points) cheap: every path is split into chunks with their
///     own bounds, so the parts outside the clip (most of a long route on the map widget) are skipped instead of
///     stroked, and only the chunk being extended is ever snapshotted again; and points closer than MinStep to
///     the last one kept are left out (the caller picks it as a fraction of a device pixel, see
///     OverlayRenderer.SyncRoute), since a whole-route compass dial packs many of them into one pixel. The first
///     point after a cut and the last one before it are always kept. Only ever used from the renderer's own
///     sequential draw calls.
/// </summary>
internal sealed class RouteGeometry : IDisposable
{
	private readonly Segment _solid = new();
	private readonly Segment?[]? _buckets;
	private readonly SKPath _dashedOnCanvas = new();
	private readonly double _speedScaleKmh;
	private Segment? _dashed;
	private SKPoint _previous;
	private double _previousSpeedKmh;
	private int _lastBucket = -1;
	// The latest point left out by MinStep - still added before a cut, so the route reaches it.
	private (SKPoint Point, double SpeedKmh)? _skipped;

	/// <param name="speedScaleKmh">The speed a route colored by speed reaches full red at.</param>
	/// <param name="minStep">In the route's own units - 0 keeps every point.</param>
	public RouteGeometry(RouteJoin join, bool colorBySpeed, double speedScaleKmh, float minStep = 0)
	{
		Join = join;
		MinStep = minStep;
		_speedScaleKmh = speedScaleKmh;
		if (colorBySpeed) _buckets = new Segment?[SpeedColorScale.Buckets];
	}

	public RouteJoin Join { get; }
	public float MinStep { get; }
	public bool ColorBySpeed => _buckets is not null;

	/// <summary>Points handed to Add so far, the left-out ones included.</summary>
	public int Count { get; private set; }

	/// <param name="afterCut">The point is the first after a part cut out of the render - joined per Join.</param>
	public void Add(SKPoint point, double speedKmh, bool afterCut)
	{
		Count++;
		if (Count == 1)
		{
			_solid.MoveTo(point);
			Keep(point, speedKmh);
			return;
		}

		if (!afterCut)
		{
			var dx = point.X - _previous.X;
			var dy = point.Y - _previous.Y;
			if (dx * dx + dy * dy < MinStep * MinStep)
			{
				_skipped = (point, speedKmh);
				return;
			}

			AddTravelled(point, speedKmh);
			return;
		}

		if (_skipped is { } last) AddTravelled(last.Point, last.SpeedKmh);

		if (Join == RouteJoin.Straight)
		{
			AddTravelled(point, speedKmh);
			return;
		}

		if (Join == RouteJoin.Dashed)
		{
			_dashed ??= new Segment();
			_dashed.MoveTo(_previous);
			_dashed.LineTo(point);
		}

		_solid.MoveTo(point);
		_lastBucket = -1;
		Keep(point, speedKmh);
	}

	private void AddTravelled(SKPoint point, double speedKmh)
	{
		if (_buckets is null)
		{
			_solid.LineTo(point);
		}
		else
		{
			var bucket = SpeedColorScale.Bucket((_previousSpeedKmh + speedKmh) / 2 / _speedScaleKmh);
			Segment path = _buckets[bucket] ??= new Segment();
			if (bucket != _lastBucket) path.MoveTo(_previous);
			path.LineTo(point);
			_lastBucket = bucket;
		}

		Keep(point, speedKmh);
	}

	private void Keep(SKPoint point, double speedKmh)
	{
		_previous = point;
		_previousSpeedKmh = speedKmh;
		_skipped = null;
	}

	/// <summary>
	///     `stroke` is set up for round caps/joins, `dashed` with the dash effect for `width` - both are only
	///     recolored/resized here. The dashed joins are moved onto the canvas first rather than drawn through the
	///     matrix, so the dash lengths stay in canvas pixels too (there are only a few of them).
	/// </summary>
	public void Draw(SKCanvas canvas, SKMatrix toCanvas, SKColor color, float width, SKPaint stroke, SKPaint dashed)
	{
		if (Count < 2) return;

		var scale = UniformScale(toCanvas);
		if (scale <= 0 || !float.IsFinite(scale)) return;

		canvas.Save();
		canvas.Concat(toCanvas);
		var localWidth = width / scale;
		stroke.StrokeWidth = localWidth;
		if (_buckets is null)
		{
			stroke.Color = color;
			_solid.Draw(canvas, stroke, localWidth);
		}
		else
		{
			SKColor[] colors = SpeedColorScale.Colors(color);
			for (var b = 0; b < _buckets.Length; b++)
			{
				if (_buckets[b] is not { } bucket) continue;
				stroke.Color = colors[b];
				bucket.Draw(canvas, stroke, localWidth);
			}
		}

		canvas.Restore();

		if (_dashed is null) return;

		// White when colored by speed - nothing was travelled there at a known speed.
		SKColor dashColor = _buckets is null ? color : SKColors.White;
		dashed.Color = dashColor.WithAlpha((byte)(dashColor.Alpha * 0.85f));
		dashed.StrokeWidth = width;
		foreach (SKPath path in _dashed.Paths())
		{
			path.Transform(toCanvas, _dashedOnCanvas);
			canvas.DrawPath(_dashedOnCanvas, dashed);
		}
	}

	/// <summary>How many canvas units one unit becomes - the matrices here only ever scale uniformly (or mirror).</summary>
	public static float UniformScale(SKMatrix matrix)
	{
		return MathF.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
	}

	public void Dispose()
	{
		_solid.Dispose();
		_dashed?.Dispose();
		_dashedOnCanvas.Dispose();
		if (_buckets is null) return;

		foreach (Segment? bucket in _buckets) bucket?.Dispose();
	}

	/// <summary>One path, as chunks of at most ChunkPoints points, each continuing where the one before ended.</summary>
	private sealed class Segment : IDisposable
	{
		private const int ChunkPoints = 256;

		private readonly List<Chunk> _chunks = [];
		private SKPoint _last;

		public void MoveTo(SKPoint point)
		{
			Writable().MoveTo(point);
			_last = point;
		}

		public void LineTo(SKPoint point)
		{
			if (_chunks.Count == 0 || _chunks[^1].Points >= ChunkPoints)
			{
				Chunk next = NewChunk();
				next.MoveTo(_last);
			}

			_chunks[^1].LineTo(point);
			_last = point;
		}

		private Chunk Writable()
		{
			return _chunks.Count > 0 && _chunks[^1].Points < ChunkPoints ? _chunks[^1] : NewChunk();
		}

		private Chunk NewChunk()
		{
			var chunk = new Chunk();
			_chunks.Add(chunk);
			return chunk;
		}

		/// <summary>Draws the chunks that can reach the clip - the canvas already carries the route's matrix.</summary>
		public void Draw(SKCanvas canvas, SKPaint stroke, float localWidth)
		{
			foreach (Chunk chunk in _chunks)
			{
				if (chunk.Points < 2 || canvas.QuickReject(SKRect.Inflate(chunk.Bounds, localWidth, localWidth))) continue;
				canvas.DrawPath(chunk.Path, stroke);
			}
		}

		public IEnumerable<SKPath> Paths()
		{
			foreach (Chunk chunk in _chunks)
				if (chunk.Points >= 2)
					yield return chunk.Path;
		}

		public void Dispose()
		{
			foreach (Chunk chunk in _chunks) chunk.Dispose();
		}
	}

	/// <summary>A builder that keeps growing plus its last snapshot, re-taken only once points were added since.</summary>
	private sealed class Chunk : IDisposable
	{
		private readonly SKPathBuilder _builder = new();
		private SKPath? _snapshot;

		public int Points { get; private set; }
		public SKRect Bounds { get; private set; }
		public SKPath Path => _snapshot ??= _builder.Snapshot();

		public void MoveTo(SKPoint point)
		{
			_builder.MoveTo(point);
			Grow(point);
		}

		public void LineTo(SKPoint point)
		{
			_builder.LineTo(point);
			Grow(point);
		}

		private void Grow(SKPoint point)
		{
			Bounds = Points == 0
				? new SKRect(point.X, point.Y, point.X, point.Y)
				: new SKRect(Math.Min(Bounds.Left, point.X), Math.Min(Bounds.Top, point.Y), Math.Max(Bounds.Right, point.X),
					Math.Max(Bounds.Bottom, point.Y));
			Points++;
			_snapshot?.Dispose();
			_snapshot = null;
		}

		public void Dispose()
		{
			_snapshot?.Dispose();
			_builder.Dispose();
		}
	}
}
