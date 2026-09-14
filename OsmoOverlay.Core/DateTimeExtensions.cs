namespace OsmoOverlay.Core;

public static class DateTimeExtensions
{
	public static DateTime ToLocalFromUtc(this DateTime utc)
	{
		return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local);
	}
}
