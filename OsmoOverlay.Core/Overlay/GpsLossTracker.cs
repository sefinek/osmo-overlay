using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Walks frames in order and says which one is the first fix after the GPS lost it for at least
///     OverlayRenderer.GpsLossMinSeconds - where a drawn route has to be joined across the stretch without one. A recording's first fix
///     is no resume (there was nothing to lose before it).
/// </summary>
internal struct GpsLossTracker
{
	private double? _lossStart;
	private bool _hadFix;

	public bool Resumes(DerivedFrame frame)
	{
		if (!frame.Raw.HasGpsFix)
		{
			if (_hadFix) _lossStart ??= frame.Raw.SampleTimeSeconds;
			return false;
		}

		bool resumes = _lossStart is { } lost && frame.Raw.SampleTimeSeconds - lost >= OverlayRenderer.GpsLossMinSeconds;
		_lossStart = null;
		_hadFix = true;
		return resumes;
	}
}
