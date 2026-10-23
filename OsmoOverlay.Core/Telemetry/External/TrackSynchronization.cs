using System.Globalization;
using OsmoOverlay.Core.Localization;

namespace OsmoOverlay.Core.Telemetry.External;

public sealed record TrackSyncAnchor(double VideoSeconds, DateTime TrackUtc)
{
	public string ToArgument() => VideoSeconds.ToString("0.#################", CultureInfo.InvariantCulture) + "=" + TrackUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

	public static bool TryParse(string value, out TrackSyncAnchor? anchor)
	{
		anchor = null;
		string[] parts = value.Split('=', 2);
		if (parts.Length != 2 || !TimeText.TryParse(parts[0], out double seconds) || !double.IsFinite(seconds) || seconds < 0 ||
		    !DateTimeOffset.TryParseExact(parts[1], ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset utc)) return false;
		anchor = new TrackSyncAnchor(seconds, utc.UtcDateTime);
		return true;
	}
}

public sealed record TrackSynchronization(TrackSyncAnchor First, TrackSyncAnchor? Second = null)
{
	public void Validate()
	{
		if (First is null || !Valid(First) || Second is { } second && (!Valid(second) || second.VideoSeconds <= First.VideoSeconds || second.TrackUtc <= First.TrackUtc))
			throw new InvalidDataException(CoreStrings.ExternalTrack_InvalidAnchors);
		if (!double.IsFinite(Rate) || Rate <= 0) throw new InvalidDataException(CoreStrings.ExternalTrack_InvalidAnchors);
	}

	private static bool Valid(TrackSyncAnchor anchor) => double.IsFinite(anchor.VideoSeconds) && anchor.VideoSeconds >= 0 && anchor.TrackUtc.Kind == DateTimeKind.Utc;

	public double Rate => Second is { } second ? (second.TrackUtc - First.TrackUtc).TotalSeconds / (second.VideoSeconds - First.VideoSeconds) : 1;

	public DateTime At(double videoSeconds) => First.TrackUtc.AddSeconds((videoSeconds - First.VideoSeconds) * Rate);
}
