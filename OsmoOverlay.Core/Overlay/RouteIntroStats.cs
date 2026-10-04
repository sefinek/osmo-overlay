namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     The stats the route overview card can show (the bits are what settings store; the card's order is RouteIntroStat.All),
///     two to a row, so each pair is related (distance with climbing, the two speeds, ...) and one turned off doesn't move
///     the rest into another pair.
/// </summary>
[Flags]
public enum RouteIntroStats
{
	None = 0,
	Distance = 1 << 0,
	ElevationGain = 1 << 1,
	MaxSpeed = 1 << 2,
	AverageSpeed = 1 << 3,
	Date = 1 << 4,
	Duration = 1 << 5,
	MovingTime = 1 << 6,
	ElevationLoss = 1 << 7,
	HighestPoint = 1 << 8,
	LowestPoint = 1 << 9,
	MaxLean = 1 << 10,
	MaxGForce = 1 << 11,
	CameraModel = 1 << 12,
	Default = Date | Distance | MovingTime | Duration | AverageSpeed | MaxSpeed | ElevationGain | ElevationLoss
}

public static class RouteIntroStat
{
	/// <summary>
	///     Every stat, in the card's order, two to a row: when and how far, the two times, the two speeds, climbing and
	///     descending, the two altitudes, the two peaks of the ride. Of a pair the smaller (or the average) is on the left.
	/// </summary>
	public static readonly RouteIntroStats[] All =
	[
		RouteIntroStats.Date, RouteIntroStats.Distance,
		RouteIntroStats.MovingTime, RouteIntroStats.Duration,
		RouteIntroStats.AverageSpeed, RouteIntroStats.MaxSpeed,
		RouteIntroStats.ElevationGain, RouteIntroStats.ElevationLoss,
		RouteIntroStats.LowestPoint, RouteIntroStats.HighestPoint,
		RouteIntroStats.MaxLean, RouteIntroStats.MaxGForce,
		RouteIntroStats.CameraModel
	];

	/// <summary>The caption on the card when the user gave none - English like the overlay's other built-in texts.</summary>
	public static string DefaultLabel(RouteIntroStats stat)
	{
		return stat switch
		{
			RouteIntroStats.Distance => "DISTANCE",
			RouteIntroStats.ElevationGain => "ELEVATION GAIN",
			RouteIntroStats.MaxSpeed => "MAX SPEED",
			RouteIntroStats.AverageSpeed => "AVG SPEED",
			RouteIntroStats.Date => "DATE",
			RouteIntroStats.Duration => "DURATION",
			RouteIntroStats.MovingTime => "MOVING TIME",
			RouteIntroStats.ElevationLoss => "ELEVATION LOSS",
			RouteIntroStats.HighestPoint => "HIGHEST POINT",
			RouteIntroStats.LowestPoint => "LOWEST POINT",
			RouteIntroStats.MaxLean => "MAX LEAN",
			RouteIntroStats.MaxGForce => "MAX G-FORCE",
			RouteIntroStats.CameraModel => "CAMERA",
			_ => stat.ToString().ToUpperInvariant()
		};
	}
}

/// <summary>
///     The user's own captions for the card's stats, by stat name (RouteIntroStats); a stat missing or blank here keeps
///     its default. Equal by content, so a settings record holding it compares as the values it carries.
/// </summary>
public sealed record RouteIntroLabels(IReadOnlyDictionary<string, string> Texts)
{
	public static readonly RouteIntroLabels None = new(new Dictionary<string, string>());

	public static RouteIntroLabels From(IReadOnlyDictionary<string, string>? texts)
	{
		return texts is null ? None : new RouteIntroLabels(texts.Where(t => !string.IsNullOrWhiteSpace(t.Value))
			.ToDictionary(t => t.Key, t => t.Value.Trim()));
	}

	public string For(RouteIntroStats stat)
	{
		return Texts.TryGetValue(stat.ToString(), out string? text) ? text : RouteIntroStat.DefaultLabel(stat);
	}

	/// <summary>For OverlaySettings: null when there's nothing of the user's.</summary>
	public Dictionary<string, string>? ToSettings()
	{
		return Texts.Count == 0 ? null : new Dictionary<string, string>(Texts);
	}

	public bool Equals(RouteIntroLabels? other)
	{
		return other is not null && Texts.Count == other.Texts.Count &&
		       Texts.All(t => other.Texts.TryGetValue(t.Key, out string? text) && text == t.Value);
	}

	public override int GetHashCode()
	{
		int hash = Texts.Count;
		foreach ((string key, string value) in Texts) hash ^= HashCode.Combine(key, value);
		return hash;
	}
}
