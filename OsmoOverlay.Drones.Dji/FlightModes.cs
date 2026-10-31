using System.Globalization;

namespace OsmoOverlay.Drones.Dji;

/// <summary>
///     The flight mode's name by the OSD's state number. 6, 10, 31, 33 and 41 were checked against a real log; the rest come from
///     the open dji-log-parser and are not yet confirmed.
/// </summary>
internal static class FlightModes
{
	public static string Name(int mode)
	{
		return mode switch
		{
			1 => "ATTI",
			6 => "P-GPS",
			10 => "Manual Takeoff",
			11 => "Auto Takeoff",
			12 => "Auto Landing",
			15 => "Go Home",
			31 => "Sport",
			33 => "Confirm Landing",
			41 => "Starting Motors",
			_ => "Mode " + mode.ToString(CultureInfo.InvariantCulture)
		};
	}
}
