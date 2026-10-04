namespace OsmoOverlay.Cameras.Dji;

/// <summary>
///     The Osmo Action writes its GPS time ("yyyy-MM-dd HH:mm:ss", read as UTC) a whole hour off: on every recording with a
///     fix (Osmo Action 6 in Poland, summer time) it ran exactly 60 min behind the container's creation_time, which agrees
///     with the file name, the timecode and the camera's clock - summer time likely taken off twice. A whole number of
///     quarter hours (what time zones differ by) between the two is that error, and the GPS time is moved onto the camera's
///     clock; anything else - seconds, a minute or two - is just the camera's clock drifting, and the GPS time stays.
/// </summary>
internal static class GpsClockZone
{
	private static readonly TimeSpan ZoneStep = TimeSpan.FromMinutes(15);
	private static readonly TimeSpan ClockDrift = TimeSpan.FromMinutes(2);
	private static readonly TimeSpan MaxZoneOffset = TimeSpan.FromHours(14);

	/// <summary>What to add to the GPS time so it agrees with the camera's clock - null when it already does, or either is unknown.</summary>
	public static TimeSpan? Shift(DateTime? gpsStartUtc, DateTime? cameraStartUtc)
	{
		if (gpsStartUtc is not { } gps || cameraStartUtc is not { } camera) return null;

		TimeSpan difference = camera - gps;
		if (difference.Duration() > MaxZoneOffset + ClockDrift) return null;

		TimeSpan zones = ZoneStep * Math.Round(difference / ZoneStep);
		return zones != TimeSpan.Zero && (difference - zones).Duration() <= ClockDrift ? zones : null;
	}
}
