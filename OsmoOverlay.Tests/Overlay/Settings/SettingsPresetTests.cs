using System.Text.Json;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class SettingsPresetTests
{
	// OverlaySettings fields a settings preset deliberately doesn't carry - see SettingsPresetValues.
	private static readonly string[] SharedSettings =
	[
		nameof(OverlaySettings.ActivePresetId), nameof(OverlaySettings.ActiveSettingsPresetId), nameof(OverlaySettings.WelcomeShown), nameof(OverlaySettings.MeasurementUnits),
		nameof(OverlaySettings.DefaultOutputFolder), nameof(OverlaySettings.PreviewMaxWidth), nameof(OverlaySettings.MapApiKeys),
		nameof(OverlaySettings.CustomMapUrlTemplate), nameof(OverlaySettings.CustomMapAttribution), nameof(OverlaySettings.DefaultMapProvider),
		nameof(OverlaySettings.CustomMapCreditBriefly), nameof(OverlaySettings.ShowEsriMapLabels), nameof(OverlaySettings.PreviewSnapToGrid),
		nameof(OverlaySettings.PreviewTimeFormat), nameof(OverlaySettings.PreviewTimelineExpanded), nameof(OverlaySettings.PreviewAudioMuted),
		nameof(OverlaySettings.PreviewAudioVolume), nameof(OverlaySettings.PreviewGridMode), nameof(OverlaySettings.HardwareDecoding),
		nameof(OverlaySettings.RenderFpsHistory), nameof(OverlaySettings.SkippedAppUpdate), nameof(OverlaySettings.InterfaceScale),
		nameof(OverlaySettings.UiLanguage), nameof(OverlaySettings.AppTheme), nameof(OverlaySettings.RestoreWindowPlacement),
		nameof(OverlaySettings.MainWindowPlacement), nameof(OverlaySettings.LayerRowsVisible), nameof(OverlaySettings.SourcePanelWidth),
		nameof(OverlaySettings.OverlayPanelWidth), nameof(OverlaySettings.LogHeight), nameof(OverlaySettings.TimelineHeight),
		nameof(OverlaySettings.PreviewMonitor), nameof(OverlaySettings.SecondScreenEnabled), nameof(OverlaySettings.SecondScreenMonitor),
		nameof(OverlaySettings.ReopenLastProject), nameof(OverlaySettings.LastProject), nameof(OverlaySettings.AutoSaveMinutes),
		nameof(OverlaySettings.CacheCleanupDays), nameof(OverlaySettings.LoopByDefault), nameof(OverlaySettings.PreviewShadows),
		nameof(OverlaySettings.ConfirmCloseWhileRendering), nameof(OverlaySettings.DisableShadows), nameof(OverlaySettings.DebugMode), nameof(OverlaySettings.DjiApiKey), nameof(OverlaySettings.WorkingAnimation), nameof(OverlaySettings.TrackMap)
	];

	[TestMethod]
	public void EverySettingIsEitherInAPresetOrDeliberatelyShared()
	{
		string[] inPreset = [.. typeof(SettingsPresetValues).GetProperties().Select(p => p.Name)];
		string[] unplaced =
		[
			.. typeof(OverlaySettings).GetProperties()
				.Select(p => p.Name)
				.Where(n => n != nameof(OverlaySettings.PreviewDrawsShadows) && !inPreset.Contains(n) && !SharedSettings.Contains(n))
		];

		Assert.IsEmpty(unplaced, $"Add to SettingsPresetValues (and From/ApplyTo) or to SharedSettings here: {string.Join(", ", unplaced)}");
		Assert.IsEmpty(inPreset.Where(n => typeof(OverlaySettings).GetProperty(n) is null).ToArray());
	}

	[TestMethod]
	public void Defaults_AreTheSettingsOwn()
	{
		string preset = JsonSerializer.Serialize(new SettingsPresetValues());
		string settings = JsonSerializer.Serialize(SettingsPresetValues.From(new OverlaySettings()));

		Assert.AreEqual(settings, preset);
	}

	[TestMethod]
	public void ApplyTo_WritesOnlyWhatThePresetCovers()
	{
		var other = new OverlaySettings(ActivePresetId: "layout", AppTheme: "Amoled", HardwareDecoding: false, DefaultOutputFolder: "out",
			SpeedCorrectionPercent: 3);
		var values = new SettingsPresetValues(SpeedCorrectionPercent: 12.5, NoAudio: true, OutputCodec: "hevc", OutputAspect: "4:5", OutputPadToAspect: true, RouteIntroDurationSeconds: 20,
			OrientationSource: OrientationSource.Quaternion);

		OverlaySettings applied = values.ApplyTo(other);

		Assert.AreEqual(12.5, applied.SpeedCorrectionPercent);
		Assert.IsTrue(applied.NoAudio);
		Assert.AreEqual("hevc", applied.OutputCodec);
		Assert.AreEqual("4:5", applied.OutputAspect);
		Assert.IsTrue(applied.OutputPadToAspect);
		Assert.AreEqual(20, applied.RouteIntroDurationSeconds);
		Assert.AreEqual(OrientationSource.Quaternion, applied.OrientationSource);
		Assert.AreEqual("layout", applied.ActivePresetId);
		Assert.AreEqual("Amoled", applied.AppTheme);
		Assert.IsFalse(applied.HardwareDecoding);
		Assert.AreEqual("out", applied.DefaultOutputFolder);
		Assert.AreEqual(values, SettingsPresetValues.From(applied) with { RouteIntroLabels = values.RouteIntroLabels });
	}

	[TestMethod]
	public void OlderFile_MissingFields_FallBackToTheSettingsDefaults()
	{
		SettingsPreset preset = JsonSerializer.Deserialize<SettingsPreset>("""{"Id":"a","Name":"Bike","Values":{"SpeedCorrectionPercent":7}}""")!;

		Assert.AreEqual(7, preset.Values.SpeedCorrectionPercent);
		Assert.IsTrue(preset.Values.TagExportedVideo);
		Assert.AreEqual("p7", preset.Values.NvencPreset);
		Assert.IsNull(preset.LayoutPresetId);
	}

	[TestMethod]
	public void Sanitize_BringsForeignValuesIntoRange()
	{
		SettingsPresetValues clean = SettingsPresetStore.Sanitize(new SettingsPresetValues(
			SpeedCorrectionPercent: 900, RouteIntroDurationSeconds: double.NaN, NvencPreset: "-preset ultrafast", OutputBitrateMultiplier: 100,
			OutputResolution: 99, OutputCodec: "prores", OutputAspect: "7:3"));

		Assert.AreEqual(50, clean.SpeedCorrectionPercent);
		Assert.AreEqual(12, clean.RouteIntroDurationSeconds);
		Assert.AreEqual("p7", clean.NvencPreset);
		Assert.AreEqual(4, clean.OutputBitrateMultiplier);
		Assert.IsNull(clean.OutputResolution);
		Assert.IsNull(clean.OutputCodec);
		Assert.IsNull(clean.OutputAspect);
		Assert.AreEqual("9:16", SettingsPresetStore.Sanitize(new SettingsPresetValues(OutputAspect: "9:16")).OutputAspect);
	}
}
