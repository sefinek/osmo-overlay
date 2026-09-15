namespace OsmoOverlay.Gui;

/// <summary>
///     Reference "known-good" specs for a camera model, used only for the pass/fail hints (checkmarks)
///     shown next to video/audio fields in the INPUT card - purely a visual hint, never blocks
///     anything. Add more entries to ByCameraModel as reference footage for other cameras becomes
///     available; a model with no entry here simply shows no checkmarks at all.
/// </summary>
public sealed record RecommendedSettings(
	int Width,
	int Height,
	double Fps,
	long MinVideoBitrate,
	long MinAudioBitrate)
{
	// Probed from the user's own known-good recording (dji osmo filmy/testowe/..._wysokie.MP4) -
	// DJI Osmo Action 6 at its highest quality mode.
	private static readonly RecommendedSettings DjiOsmoAction6 = new(
		3840,
		2160,
		59.94,
		70_000_000,
		256_000);

	private static readonly Dictionary<string, RecommendedSettings> ByCameraModel =
		new(StringComparer.OrdinalIgnoreCase) { ["DJI AC006"] = DjiOsmoAction6 };

	public static RecommendedSettings? ForCameraModel(string? cameraModel)
	{
		return cameraModel is not null && ByCameraModel.TryGetValue(cameraModel, out RecommendedSettings? settings)
			? settings
			: null;
	}
}
