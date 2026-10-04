namespace OsmoOverlay.Cameras.Dji;

/// <summary>
///     A dropped GPS fix (tunnel, indoors) must not be treated as (0,0): carry the last known fix
///     forward instead, so distance/speed/heading derived from it don't spike. Shared by both the
///     native djmd decoder and the exiftool fallback so the two pipelines can't drift apart.
/// </summary>
internal sealed class GpsForwardFill
{
	private double _altitudeMeters;
	private double _lat;
	private double _lon;

	/// <summary>
	///     HasFix is true only when this call was given a real lat/lon (not carried forward from an earlier sample) - see
	///     TelemetryFrame.HasGpsFix. The altitude is taken only with a fix: the camera keeps writing one without (0 m on every
	///     recording that never had a fix), which would drop the elevation to 0 for a tunnel and count the climb back as gain.
	/// </summary>
	public (double Lat, double Lon, double AltitudeMeters, bool HasFix) Apply(double? lat, double? lon, double? altitudeMeters)
	{
		if (lat is not { } fixLat || lon is not { } fixLon) return (_lat, _lon, _altitudeMeters, false);

		_lat = fixLat;
		_lon = fixLon;
		_altitudeMeters = altitudeMeters ?? _altitudeMeters;
		return (_lat, _lon, _altitudeMeters, true);
	}
}
