using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Core;

/// <summary>
///     A saved project (OverlayProject) as RenderJob takes it: the render the GUI would start from the open project, without the
///     GUI - what `--project` and the render queue run. The project's own settings preset is applied to a copy of the settings
///     (RenderOptions.Settings), never written to the settings file.
/// </summary>
public static class ProjectRender
{
	public readonly record struct Timeline(double Fps, long Frames);

	/// <summary>Probes the project's recordings (ffprobe) - throws when one can't be read.</summary>
	public static Timeline Probe(OverlayProject project)
	{
		IReadOnlyList<VideoSegment> segments = VideoSegments.ProbeAll(project.InputPaths);
		return new Timeline(segments[0].Source.Video.Fps, segments.TotalFrameCount());
	}

	/// <summary>
	///     What a queue renders for recordings that were never opened in a project: one render of them joined (a camera's split files)
	///     with the settings and default output of the moment and the overlay layout in use when its turn comes - no cuts, no 360 view,
	///     no sound edits. Making them a project is what lets the queue treat every job the same way.
	/// </summary>
	public static OverlayProject ForRecordings(IReadOnlyList<string> recordings, OverlaySettings settings)
	{
		return new OverlayProject([.. recordings], RenderOptions.DefaultOutputPath(recordings, settings.DefaultOutputFolder), [], null, 0, null, null)
		{
			SettingsPresetId = settings.ActiveSettingsPresetId
		};
	}

	/// <summary>The settings the project was made with: its settings preset's values over `current`, or `current` when the preset is gone.</summary>
	public static OverlaySettings SettingsFor(OverlayProject project, OverlaySettings current, IReadOnlyList<SettingsPreset> presets)
	{
		return presets.FirstOrDefault(p => p.Id == project.SettingsPresetId) is { } preset ? preset.Values.ApplyTo(current) : current;
	}

	/// <summary>The project's output path, or the default one next to its first recording when it has none.</summary>
	public static string OutputPathFor(OverlayProject project, OverlaySettings settings)
	{
		return string.IsNullOrWhiteSpace(project.OutputPath)
			? RenderOptions.DefaultOutputPath(project.InputPaths, settings.DefaultOutputFolder)
			: project.OutputPath;
	}

	/// <summary>The recordings and the music files the project names that aren't there (any other file isn't needed to render).</summary>
	public static List<string> MissingFiles(OverlayProject project, Func<string, bool>? exists = null)
	{
		exists ??= File.Exists;
		return [.. project.InputPaths.Concat(project.Music.Select(c => c.Path)).Where(path => !exists(path))];
	}

	/// <summary>
	///     The render of the whole project: its recordings, cuts (with their transitions, captions and route joins), 360 view, overlay
	///     (muted and unsoloed layers left out, as the preview shows it), sound level, music and external track. Music files that are
	///     missing are left out (MissingFiles says which); the recordings must be there.
	/// </summary>
	public static RenderOptions Options(OverlayProject project, Timeline timeline, string outputPath, OverlaySettings settings, Func<string, bool>? exists = null)
	{
		exists ??= File.Exists;
		List<TimeRange> cuts = CutList.ToTimeRanges(CutList.Normalize(project.Cuts, timeline.Frames), timeline.Fps);
		return new RenderOptions(project.InputPaths, outputPath,
			Layout: project.Preset is { } preset ? OverlayLayers.Drawn(preset.Elements, preset.Layers) : null,
			CutOuts: cuts.Count > 0 ? cuts : null,
			Reframe: project.Reframe,
			ExternalTrack: project.ExternalTrack,
			Volume: [.. project.Volume],
			Music: [.. project.Music.Where(c => exists(c.Path))],
			Settings: settings);
	}
}
