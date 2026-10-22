namespace OsmoOverlay.Core.Telemetry;

/// <summary>Where the roll, pitch and G-meter take the camera's orientation from (OverlaySettings.OrientationSource).</summary>
public enum OrientationSource
{
	/// <summary>The accelerometer, the vehicle's own acceleration taken out of the tilt with GPS (CameraTilt) - every camera.</summary>
	Accelerometer,

	/// <summary>
	///     The orientation the camera reports itself (TelemetryFrame.Orientation), where it does: tilt without GPS, and a
	///     G-meter that no longer reads turning the camera as a force. A camera or a frame without one stays on the accelerometer.
	/// </summary>
	Quaternion
}
