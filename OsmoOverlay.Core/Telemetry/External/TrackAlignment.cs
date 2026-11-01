namespace OsmoOverlay.Core.Telemetry.External;

public enum TrackAlignmentMethod
{
	/// <summary>The track holds the camera's own start and stop of recording, which is as long as the footage: the start is known to the frame.</summary>
	RecordingSpan,

	/// <summary>The container's creation time falls inside one of the track's recording spans; it is off by the difference of the two clocks.</summary>
	ContainerClock,

	/// <summary>Matched by how the camera and the track moved - an approximation (see TrackSyncEstimate).</summary>
	Motion
}

/// <summary>Where the recording's first frame lies on a track, and how that was found.</summary>
public sealed record TrackAlignment(DateTime VideoStartUtc, TrackAlignmentMethod Method, double? Correlation = null)
{
	/// <summary>Whether the user should check the result on the picture: only a recording span is exact.</summary>
	public bool NeedsVerification => Method != TrackAlignmentMethod.RecordingSpan;
}
