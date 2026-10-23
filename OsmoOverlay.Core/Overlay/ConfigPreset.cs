using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     A named set of the settings that differ from one kind of ride to another (a bike's speed correction, a car's render
///     quality, a route card with other stats): the values in ConfigPresetValues, and which overlay layout (OverlayPreset)
///     it opens with. The active one's values are what OverlaySettings holds - every reader keeps reading settings.json,
///     and OverlaySettingsStore.Save writes an edit back into this preset (ConfigPresetStore.Sync).
/// </summary>
public sealed record ConfigPreset(string Id, string Name, ConfigPresetValues Values)
{
	/// <summary>The overlay layout this preset switches to; null leaves the layout as it is.</summary>
	public string? LayoutPresetId { get; init; }
}

/// <summary>
///     What a ConfigPreset holds, and so what OverlaySettings fields it covers. Left out on purpose: whatever belongs to the
///     computer or the user rather than the ride (interface, window and panel sizes, hardware decoding, shadows' switches,
///     second screen, project handling, cache), the shared map keys and tile servers (secrets), the output folder (a path of
///     one machine) and the render speed history - so a preset can be exported and sent to someone. Every default is
///     OverlaySettings' own, which a missing field of an older file falls back to.
/// </summary>
public sealed record ConfigPresetValues(
	bool ShowWatermark = true,
	bool SmoothGpsMotion = true,
	double SpeedCorrectionPercent = 0,
	OrientationSource OrientationSource = OrientationSource.Accelerometer,
	bool ShowRouteIntro = true,
	double RouteIntroDurationSeconds = 12.0,
	RouteIntroStats RouteIntroStats = RouteIntroStats.Default,
	Dictionary<string, string>? RouteIntroLabels = null,
	UnitSystem RouteIntroUnits = UnitSystem.Metric,
	bool RouteIntroColorBySpeed = false,
	RouteJoin RouteIntroGpsLossJoin = RouteJoins.Default,
	bool RouteIntroShowStartFinish = true,
	bool RouteIntroWhenTrimmed = false,
	string? RouteIntroStartLabel = null,
	string? RouteIntroFinishLabel = null,
	string? RouteIntroMapProvider = null,
	bool RouteIntroShowEsriMapLabels = false,
	bool PreserveCameraMetadata = false,
	bool TagExportedVideo = true,
	bool MetadataKeepTelemetry = true,
	bool MetadataKeepDebugTrack = true,
	bool MetadataKeepThumbnails = true,
	bool MetadataKeepSerialNumber = false,
	string NvencPreset = "p7",
	double OutputBitrateMultiplier = 1.0,
	int? OutputResolution = null,
	string? OutputCodec = null,
	bool OutputEightBit = false,
	bool FastStart = false,
	bool NoAudio = false)
{
	public static ConfigPresetValues From(OverlaySettings settings)
	{
		return new ConfigPresetValues(settings.ShowWatermark, settings.SmoothGpsMotion, settings.SpeedCorrectionPercent, settings.OrientationSource,
			settings.ShowRouteIntro, settings.RouteIntroDurationSeconds, settings.RouteIntroStats, settings.RouteIntroLabels, settings.RouteIntroUnits,
			settings.RouteIntroColorBySpeed, settings.RouteIntroGpsLossJoin, settings.RouteIntroShowStartFinish, settings.RouteIntroWhenTrimmed,
			settings.RouteIntroStartLabel, settings.RouteIntroFinishLabel, settings.RouteIntroMapProvider, settings.RouteIntroShowEsriMapLabels,
			settings.PreserveCameraMetadata, settings.TagExportedVideo, settings.MetadataKeepTelemetry, settings.MetadataKeepDebugTrack,
			settings.MetadataKeepThumbnails, settings.MetadataKeepSerialNumber, settings.NvencPreset, settings.OutputBitrateMultiplier,
			settings.OutputResolution, settings.OutputCodec, settings.OutputEightBit, settings.FastStart, settings.NoAudio);
	}

	/// <summary>The reverse of From: these values written into the settings, everything else in them kept.</summary>
	public OverlaySettings ApplyTo(OverlaySettings settings)
	{
		return settings with
		{
			ShowWatermark = ShowWatermark,
			SmoothGpsMotion = SmoothGpsMotion,
			SpeedCorrectionPercent = SpeedCorrectionPercent,
			OrientationSource = OrientationSource,
			ShowRouteIntro = ShowRouteIntro,
			RouteIntroDurationSeconds = RouteIntroDurationSeconds,
			RouteIntroStats = RouteIntroStats,
			RouteIntroLabels = RouteIntroLabels,
			RouteIntroUnits = RouteIntroUnits,
			RouteIntroColorBySpeed = RouteIntroColorBySpeed,
			RouteIntroGpsLossJoin = RouteIntroGpsLossJoin,
			RouteIntroShowStartFinish = RouteIntroShowStartFinish,
			RouteIntroWhenTrimmed = RouteIntroWhenTrimmed,
			RouteIntroStartLabel = RouteIntroStartLabel,
			RouteIntroFinishLabel = RouteIntroFinishLabel,
			RouteIntroMapProvider = RouteIntroMapProvider,
			RouteIntroShowEsriMapLabels = RouteIntroShowEsriMapLabels,
			PreserveCameraMetadata = PreserveCameraMetadata,
			TagExportedVideo = TagExportedVideo,
			MetadataKeepTelemetry = MetadataKeepTelemetry,
			MetadataKeepDebugTrack = MetadataKeepDebugTrack,
			MetadataKeepThumbnails = MetadataKeepThumbnails,
			MetadataKeepSerialNumber = MetadataKeepSerialNumber,
			NvencPreset = NvencPreset,
			OutputBitrateMultiplier = OutputBitrateMultiplier,
			OutputResolution = OutputResolution,
			OutputCodec = OutputCodec,
			OutputEightBit = OutputEightBit,
			FastStart = FastStart,
			NoAudio = NoAudio
		};
	}
}
