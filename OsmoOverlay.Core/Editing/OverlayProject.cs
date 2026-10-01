using System.Text.Json;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Reframe;

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

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public void Save(string path)
	{
		AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
	}

	/// <summary>The project in `path`; throws on an unreadable file, one from a newer format or with paths off the local drives (a shared project is untrusted - opening a UNC path would reach out to that host).</summary>
	public static OverlayProject Load(string path)
	{
		OverlayProject project = JsonSerializer.Deserialize<OverlayProject>(File.ReadAllText(path))
		                         ?? throw new InvalidDataException("The project file is empty.");
		if (project.Format > CurrentFormat)
			throw new InvalidDataException("The project was saved by a newer version of OsmoOverlay.");
		if (project.InputPaths is null || project.Cuts is null)
			throw new InvalidDataException("The project file is incomplete.");
		if (project.InputPaths.Append(project.OutputPath).Any(p => !string.IsNullOrWhiteSpace(p) && !OverlayPresetStore.IsLocalFilePath(p)))
			throw new InvalidDataException("The project points at a file that isn't on a local drive (a network share or a relative path).");

		return project;
	}
}
