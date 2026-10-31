namespace OsmoOverlay.Drones.Dji;

/// <summary>
///     The groups ("feature points") a log's record types belong to, which decide the AES key of the record. The numbers are
///     DJI's order; Plaintext's records are only xored.
/// </summary>
internal static class FlightLogFeaturePoints
{
	public const int Base = 1, Vision = 2, Waypoint = 3, Agriculture = 4, AirLink = 5, AfterSales = 6, DjiFlyCustom = 7, Plaintext = 8, FlightHub = 9,
		Gimbal = 10, Rc = 11, Camera = 12, Battery = 13, FlySafe = 14, Security = 15;

	private static readonly string[] Names =
	[
		"", "FR_Standardization_Feature_Base_1", "FR_Standardization_Feature_Vision_2", "FR_Standardization_Feature_Waypoint_3",
		"FR_Standardization_Feature_Agriculture_4", "FR_Standardization_Feature_AirLink_5", "FR_Standardization_Feature_AfterSales_6",
		"FR_Standardization_Feature_DJIFlyCustom_7", "FR_Standardization_Feature_Plaintext_8", "FR_Standardization_Feature_FlightHub_9",
		"FR_Standardization_Feature_Gimbal_10", "FR_Standardization_Feature_RC_11", "FR_Standardization_Feature_Camera_12",
		"FR_Standardization_Feature_Battery_13", "FR_Standardization_Feature_FlySafe_14", "FR_Standardization_Feature_Security_15"
	];

	public static string Name(int featurePoint)
	{
		return Names[featurePoint is >= 1 and <= 15 ? featurePoint : Plaintext];
	}

	public static int FromName(string name)
	{
		int index = Array.IndexOf(Names, name);
		return index > 0 ? index : 0;
	}

	public static int FromRecordType(byte type, int version)
	{
		bool v13 = version == 13;
		return type switch
		{
			1 or 2 or 6 or 13 or 14 or 15 or 40 or 58 or 59 or 63 => Base,
			3 => v13 ? Base : Gimbal,
			4 or 11 or 29 or 33 => v13 ? Base : Rc,
			5 or 9 or 10 or 20 or 24 or 30 or 54 => DjiFlyCustom,
			7 or 8 => v13 ? Base : Battery,
			12 or 16 or 19 or 26 or 27 => AfterSales,
			17 or 18 => Vision,
			21 or 41 or 43 or 44 or 45 or 46 or 47 or 48 => Agriculture,
			22 => v13 ? AfterSales : Battery,
			25 => v13 ? Base : Camera,
			28 or 51 or 52 => v13 ? AfterSales : FlySafe,
			31 or 32 or 34 or 35 or 36 or 38 or 39 => Waypoint,
			49 => AirLink,
			53 => v13 ? AfterSales : FlightHub,
			55 => Security,
			62 => Rc,
			_ => Plaintext
		};
	}
}
