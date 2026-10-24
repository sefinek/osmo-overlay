using System.Security.Cryptography;
using System.Text.Json;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Core;

/// <summary>
///     A saved editing session (.ovproj, JSON): the recordings in their order, the output path, the cuts (frames of the
///     combined recording), the 360 view and the overlay. The overlay is embedded (Preset) so the project opens the same
///     whatever happened to the presets since; PresetId says which preset it was taken from, so an unchanged one is simply
///     activated again and edits keep going to it.
/// </summary>
public sealed record OverlayProject(
	List<string> InputPaths,
	string? OutputPath,
	List<FrameRange> Cuts,
	ReframeView? Reframe,
	long PreviewFrame,
	string? PresetId,
	OverlayPreset? Preset)
{
	public const string Extension = ".ovproj";

	public const int CurrentFormat = 1;

	public int Format { get; init; } = CurrentFormat;

	/// <summary>The key moments the user marked (frames of the combined recording) - absent in projects saved before they existed.</summary>
	public List<KeyMoment> Moments { get; init; } = [];
	public ExternalTrackSource? ExternalTrack { get; init; }

	/// <summary>The settings preset (SettingsPreset) the project was made with - absent in projects saved before they existed, or when its preset has since been deleted.</summary>
	public string? SettingsPresetId { get; init; }

	/// <summary>The sound's level over the recording (VolumeLine) - absent in projects saved before it existed.</summary>
	public List<VolumePoint> Volume { get; init; } = [];

	/// <summary>The music on the timeline (MusicClip) - absent in projects saved before it existed.</summary>
	public List<MusicClip> Music { get; init; } = [];

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	/// <summary>
	///     A hash of everything a save would keep except where the playhead is (PreviewFrame moves with every click and isn't an edit):
	///     two projects with the same fingerprint are the same work, so a differing one since the last save is an unsaved change.
	/// </summary>
	public string Fingerprint()
	{
		byte[] json = JsonSerializer.SerializeToUtf8Bytes(this with { PreviewFrame = 0 });
		return Convert.ToHexString(SHA256.HashData(json));
	}

	public void Save(string path)
	{
		AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
	}

	/// <summary>The project in `path`; throws on an unreadable file, one from a newer format or with paths off the local drives (a shared project is untrusted - opening a UNC path would reach out to that host).</summary>
	public static OverlayProject Load(string path)
	{
		OverlayProject project = JsonSerializer.Deserialize<OverlayProject>(File.ReadAllText(path))
		                         ?? throw new InvalidDataException(CoreStrings.Project_Empty);
		if (project.Format > CurrentFormat)
			throw new InvalidDataException(CoreStrings.Project_Newer);
		if (project.InputPaths is null || project.Cuts is null)
			throw new InvalidDataException(CoreStrings.Project_Incomplete);
		if (project.InputPaths.Append(project.OutputPath).Append(project.ExternalTrack?.Path).Concat((project.Music ?? []).Select(c => c.Path)).Any(p => !string.IsNullOrWhiteSpace(p) && !OverlayPresetStore.IsLocalFilePath(p)))
			throw new InvalidDataException(CoreStrings.Project_NotLocal);
		project.ExternalTrack?.Validate();

		return project;
	}
}
