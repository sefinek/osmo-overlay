namespace OsmoOverlay.Core.Preview;

/// <summary>
///     Where the display takes played frames from - on the GUI's render thread, once per refresh, so the UI thread is
///     not in the path of a played frame at all (layout, the timeline or the editor can't hold one up). Hands out
///     nothing between playbacks. One render thread takes frames; the preview attaches and detaches playbacks from
///     the UI thread.
/// </summary>
public sealed class PlaybackFrameSource
{
	private volatile PlaybackSession? _session;

	public bool IsPlaying => _session is not null;

	internal void Attach(PlaybackSession? session)
	{
		_session = session;
	}

	/// <summary>
	///     The frame due at this refresh, if a new one is (see PlaybackSession.TakeDueFrame). The caller owns it until it
	///     hands it back with Release - once it's drawn, or uploaded to the GPU.
	/// </summary>
	public ComposedPreviewFrame? TakeDueFrame()
	{
		if (_session is not { } session) return null;

		ComposedPreviewFrame? frame = session.TakeDueFrame();
		if (frame is null || ReferenceEquals(_session, session)) return frame;

		// Stopped meanwhile - the still decoded for the pause shows next.
		Release(frame);
		return null;
	}

	public static void Release(ComposedPreviewFrame frame)
	{
		frame.Owner?.Return(frame.Bgra);
	}
}
