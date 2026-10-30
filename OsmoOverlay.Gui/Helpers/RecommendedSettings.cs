using OsmoOverlay.Cameras.Dji;
using OsmoOverlay.Core;

namespace OsmoOverlay.Gui;

/// <summary>
///     The recommended recording settings (RecommendedRecording) per camera model, used only for the checkmarks shown next to
///     the video and audio fields in the INPUT card - purely a visual hint, never blocks anything. Add an entry when reference
///     footage for another model is available; a model with none simply shows no marks at all.
/// </summary>
public static class RecommendedSettings
{
	/// <summary>4K at 50 (or 59.94) frames a second in HEVC - for the Osmo Action 5 Pro and 6.</summary>
	private static readonly RecommendedRecording DjiOsmo4K = new(3840, 2160, 50, 59.94, 70_000_000, 256_000);

	/// <summary>
	///     4K at 50 (or 59.94) in HEVC, Bit Rate: High. 10-bit isn't recommended: GoPro lists it for 4K at 60/30/24 and doesn't confirm
	///     4K/50, so 8-bit HEVC is as good. The bitrate floor is below the measured High bitrates (about 75 Mbps at 4K/24, 90 at 30, 120 at 60 - iXBT's
	///     test; 4K/50 isn't published) so Standard shows a warning; the audio floor is under the ~192 kbps AAC measured there. Full range is
	///     what a HERO12 Black writes (yuvj420p, Rec.709 - one recording).
	/// </summary>
	private static readonly RecommendedRecording GoPro4K = new(3840, 2160, 50, 59.94, 60_000_000, 128_000, true, false);

	/// <summary>
	///     Mavic Air 2: 4K up to 60 frames a second in 8-bit HEVC at up to 120 Mbps (DJI's specification), so 100 Mbps is the floor. No sound,
	///     and the MP4 carries no colour tags (Rec.709 limited range is what it records). The drone is known by the aircraft's name in the flight log -
	///     the owner's own name, which stays the model's until it is renamed.
	/// </summary>
	private static readonly RecommendedRecording MavicAir2 = new(3840, 2160, 50, 59.94, 100_000_000, 0, false, false, true);

	private const string GoProHero12Black = "HERO12 Black";
	private const string DjiMavicAir2 = "Mavic Air 2";

	private static readonly Dictionary<string, RecommendedRecording> ByCameraModel =
		new(StringComparer.OrdinalIgnoreCase)
		{
			[DjiModels.OsmoAction5Pro] = DjiOsmo4K,
			[DjiModels.OsmoAction6] = DjiOsmo4K,
			[GoProHero12Black] = GoPro4K,
			[DjiMavicAir2] = MavicAir2
		};

	/// <summary>
	///     The model's entry: by the name as it is, else by a known model's name within it - a drone's name is its owner's ("Mavic Air 2 Jan",
	///     "My Mavic Air 2"). The longest known name is tried first, so one that holds another stays its own.
	/// </summary>
	public static RecommendedRecording? ForCameraModel(string? cameraModel)
	{
		if (cameraModel is null) return null;
		if (ByCameraModel.TryGetValue(cameraModel, out RecommendedRecording? settings)) return settings;

		string? known = ByCameraModel.Keys.OrderByDescending(name => name.Length).FirstOrDefault(name => cameraModel.Contains(name, StringComparison.OrdinalIgnoreCase));
		return known is null ? null : ByCameraModel[known];
	}
}
