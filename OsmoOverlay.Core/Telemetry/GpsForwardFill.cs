namespace OsmoOverlay.Core.Telemetry;

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

	/// <summary>HasFix is true only when this call was given a real lat/lon (not carried forward from an earlier sample) - see TelemetryFrame.HasGpsFix.</summary>
	public (double Lat, double Lon, double AltitudeMeters, bool HasFix) Apply(double? lat, double? lon, double? altitudeMeters)
	{
		var hasFix = lat is not null && lon is not null;
		_lat = lat ?? _lat;
		_lon = lon ?? _lon;
		_altitudeMeters = altitudeMeters ?? _altitudeMeters;
		return (_lat, _lon, _altitudeMeters, hasFix);
	}
}
