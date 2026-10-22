using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Cameras.Dji;

/// <summary>
///     What DJI's GpsBasic message says about a fix (dvtm_library.proto): PositionCoord.position_coord_unit is 0 radians /
///     1 degrees, GpsBasic.gps_status is 0 normal / 1 invalid / 2 RTK. proto3 leaves a zero off the wire, so a missing
///     field is the first value of its enum.
/// </summary>
internal static class DjiGpsFix
{
	private const ulong UnitRadians = 0;
	private const ulong UnitDegrees = 1;
	private const ulong StatusInvalid = 1;

	/// <summary>
	///     Null when the sample has no usable position: the receiver calls the fix invalid, the unit is not one we know, or
	///     the numbers are not a place on Earth (this is also where a no-fix sample that still carries (0, 0), or radians read
	///     as degrees, ends up).
	/// </summary>
	public static (double Lat, double Lon)? Position(ulong? unit, ulong? status, double? lat, double? lon)
	{
		if (status == StatusInvalid || lat is not { } rawLat || lon is not { } rawLon) return null;

		(double lat, double lon) degrees;
		switch (unit ?? UnitRadians)
		{
			case UnitDegrees:
				degrees = (rawLat, rawLon);
				break;
			case UnitRadians:
				degrees = (AngleMath.RadToDeg(rawLat), AngleMath.RadToDeg(rawLon));
				break;
			default:
				return null;
		}

		bool onEarth = double.IsFinite(degrees.lat) && double.IsFinite(degrees.lon)
		                                            && Math.Abs(degrees.lat) <= 90 && Math.Abs(degrees.lon) <= 180;
		return onEarth && !(degrees.lat == 0 && degrees.lon == 0) ? degrees : null;
	}
}
