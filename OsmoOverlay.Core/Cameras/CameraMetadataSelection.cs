namespace OsmoOverlay.Core.Cameras;

/// <summary>
///     Which parts of the camera's metadata a camera format's CopyMetadata carries over. SerialNumber only matters for
///     what's otherwise kept (DJI: the serial lives in the telemetry track, the udta device id in the thumbnails/info
///     block).
/// </summary>
public sealed record CameraMetadataSelection(bool Telemetry, bool DebugTrack, bool ThumbnailsAndInfo, bool SerialNumber)
{
	public bool Any => Telemetry || DebugTrack || ThumbnailsAndInfo;
}
