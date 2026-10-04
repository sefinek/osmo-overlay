using System.Runtime.InteropServices;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Overlay;

/// <summary>A point of the drawn route. AfterCut: the first point after a part cut out of the render - the route joins it per RouteJoin.</summary>
internal readonly record struct TrailPoint(double East, double North, double Lat, double Lon, double SpeedKmh, bool AfterCut);

/// <summary>
///     The route travelled up to the frame drawn, shared by the Compass and Map trails: points at least MinStepMeters apart
///     and their bounding box in local meters. Always the same function of the frames up to the current one, so a longer
///     trail only ever extends a shorter one. Scrubbing draws frames out of order: moving forwards (by one frame or several,
///     when the preview skips frames or the telemetry runs faster than the video) appends just the new frames, moving back
///     builds it again from the start.
/// </summary>
internal sealed class RouteTrail
{
	private const double MinStepMeters = 3.0;

	private readonly List<TrailPoint> _points = [];
	private int _frameIndex = -1;

	public int Count => _points.Count;
	public TrailPoint this[int index] => _points[index];
	public ReadOnlySpan<TrailPoint> Points => CollectionsMarshal.AsSpan(_points);

	public double MinEast { get; private set; }
	public double MaxEast { get; private set; }
	public double MinNorth { get; private set; }
	public double MaxNorth { get; private set; }

	/// <summary>The trail up to and including frames[index].</summary>
	public void MoveTo(IReadOnlyList<DerivedFrame> frames, int index)
	{
		if (index < _frameIndex) Clear();

		for (int i = _frameIndex + 1; i <= index; i++)
			Append(frames[i]);

		_frameIndex = index;
	}

	public void Clear()
	{
		_points.Clear();
		_frameIndex = -1;
	}

	private void Append(DerivedFrame frame)
	{
		double east = frame.LocalEastMeters, north = frame.LocalNorthMeters;
		// The first point after a cut is always kept, however close - it's where the route resumes.
		if (!frame.StartsAfterCut && _points.Count > 0)
		{
			TrailPoint last = _points[^1];
			double dx = east - last.East, dy = north - last.North;
			if (dx * dx + dy * dy < MinStepMeters * MinStepMeters) return;
		}

		if (_points.Count == 0)
		{
			(MinEast, MaxEast, MinNorth, MaxNorth) = (east, east, north, north);
		}
		else
		{
			MinEast = Math.Min(MinEast, east);
			MaxEast = Math.Max(MaxEast, east);
			MinNorth = Math.Min(MinNorth, north);
			MaxNorth = Math.Max(MaxNorth, north);
		}

		_points.Add(new TrailPoint(east, north, frame.Raw.Latitude, frame.Raw.Longitude, frame.SpeedKmh, frame.StartsAfterCut));
	}
}
