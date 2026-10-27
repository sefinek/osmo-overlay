using System.Text.Json;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Core;

/// <summary>
///     What a queued render is, for showing it: the project's name (what the user called it, not the queue's copy's file name), the
///     recordings it renders, where the result goes, and a count of what the project holds. Problem is set, and the rest filled in as
///     well as it can be, when the copy can't be read.
/// </summary>
public sealed record QueueDetails(
	string Name,
	IReadOnlyList<string> Recordings,
	string OutputPath,
	int Cuts,
	int MusicClips,
	bool EditedVolume,
	bool ExternalTrack,
	string? Problem = null)
{
	/// <summary>Reads the request's project; never throws for a project that is gone or unreadable.</summary>
	public static QueueDetails Of(QueueRequest request, OverlaySettings settings)
	{
		try
		{
			var project = OverlayProject.Load(request.ProjectPath);
			string output = request.Output ?? ProjectRender.OutputPathFor(project, settings);
			if (request.Alpha is { } alpha) output = RenderOptions.AlphaOutputPath(output, alpha);
			return new QueueDetails(NameOf(request, project.InputPaths.FirstOrDefault()), [.. project.InputPaths], output, project.Cuts.Count,
				project.Music?.Count ?? 0, project.Volume is { Count: > 0 }, project.ExternalTrack is not null);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
		{
			return new QueueDetails(NameOf(request, null), [], request.Output ?? "", 0, 0, false, false, ex.Message);
		}
	}

	/// <summary>The name it was queued under; else its first recording's; else the copy's own file name.</summary>
	private static string NameOf(QueueRequest request, string? firstRecording)
	{
		if (!string.IsNullOrWhiteSpace(request.Name)) return request.Name;

		return Path.GetFileNameWithoutExtension(firstRecording ?? request.ProjectPath);
	}
}
