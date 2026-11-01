namespace OsmoOverlay.Drones.Dji;

/// <summary>Byte offsets and masks inside an OSD record (type 1) - see the remarks of FlightLog for what was checked.</summary>
internal static class OsdLayout
{
	public const int MinLength = 44;
	public const int Mode = 30, Flags = 32, GpsFlags = 33, GpsLevel = 34, Satellites = 36;
	public const byte ModeMask = 0x7F, MotorsOnFlag = 0x04, GpsValidFlag = 0x80;

	public static int FlightMode(byte[] record)
	{
		return record[Mode] & ModeMask;
	}

	public static bool MotorsOn(byte[] record)
	{
		return (record[Flags] & MotorsOnFlag) != 0;
	}
}
