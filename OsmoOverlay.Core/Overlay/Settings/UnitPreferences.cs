using System.Globalization;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     The units a user reads each kind of quantity in - chosen apart because the world doesn't agree on one set (the UK drives in
///     miles per hour but measures a hill in metres, Canada drives in kilometres and reads the weather in Celsius). A widget carries
///     one UnitSystem of its own; this decides what a new widget, and the built-in layouts, start with. Altitude covers the
///     elevation, a drone's height and its climb rate; Distance the trip, the distance from home and the progress bar.
/// </summary>
public sealed record UnitPreferences(
	UnitSystem Speed = UnitSystem.Metric,
	UnitSystem Distance = UnitSystem.Metric,
	UnitSystem Altitude = UnitSystem.Metric,
	UnitSystem Temperature = UnitSystem.Metric)
{
	public static readonly UnitPreferences Metric = new();
	public static readonly UnitPreferences UnitedStates = new(UnitSystem.Imperial, UnitSystem.Imperial, UnitSystem.Imperial, UnitSystem.Imperial);
	public static readonly UnitPreferences UnitedKingdom = new(UnitSystem.Imperial, UnitSystem.Imperial, UnitSystem.Metric, UnitSystem.Metric);

	/// <summary>What the settings hold; metric until the user chose (the welcome window's Units step).</summary>
	public static UnitPreferences From(OverlaySettings settings)
	{
		return settings.MeasurementUnits ?? Metric;
	}

	/// <summary>The saved choice, read from settings.json.</summary>
	public static UnitPreferences Current => From(OverlaySettingsStore.Load());

	/// <summary>
	///     What most people in a country (ISO 3166 two letters) use: the United States, Liberia and Myanmar all the imperial ones, the
	///     United Kingdom miles with metres and Celsius, the rest metric.
	/// </summary>
	public static UnitPreferences ForRegion(string? region)
	{
		return region?.ToUpperInvariant() switch
		{
			"US" or "LR" or "MM" => UnitedStates,
			"GB" => UnitedKingdom,
			_ => Metric
		};
	}

	/// <summary>The suggestion for this computer's region - what the welcome window shows before a choice is made.</summary>
	public static UnitPreferences ForCurrentRegion()
	{
		try
		{
			return ForRegion(RegionInfo.CurrentRegion.TwoLetterISORegionName);
		}
		catch (ArgumentException)
		{
			return Metric;
		}
	}

	public OverlayPreset ApplyTo(OverlayPreset preset)
	{
		return preset with { Elements = [.. preset.Elements.Select(ApplyTo)] };
	}

	/// <summary>The widget with the units of the quantity it shows; one that shows no measured quantity is returned as it is.</summary>
	public OverlayElement ApplyTo(OverlayElement element)
	{
		return element switch
		{
			SpeedGaugeElement e => e with
			{
				Units = Speed, MaxSpeed = e.MaxSpeed is { } max ? OverlayRenderer.ConvertGaugeMaxSpeed(max, e.Units, Speed) : null
			},
			ElevationElement e => e with { Units = Altitude },
			HeightElement e => e with { Units = Altitude },
			VerticalSpeedElement e => e with { Units = Altitude },
			DistanceElement e => e with { Units = Distance },
			DeviceDistanceElement e => e with { Units = Distance },
			HomeDistanceElement e => e with { Units = Distance },
			TripProgressBarElement e => e with { Units = Distance },
			TemperatureElementBase e => e with { Units = Temperature },
			DroneBatteryElement e => e with { Units = Temperature },
			ProfileChartElement e => e with { Units = e.Series == ProfileSeries.Speed ? Speed : Altitude },
			TripStatElement e => e.Stat switch
			{
				TripStatKind.MaxSpeed or TripStatKind.AverageSpeed => e with { Units = Speed },
				TripStatKind.MaxHomeDistance => e with { Units = Distance },
				TripStatKind.MovingTime => e,
				_ => e with { Units = Altitude }
			},
			_ => element
		};
	}
}
