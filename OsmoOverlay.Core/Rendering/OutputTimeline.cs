using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

/// <summary>
///     The output video's own timeline for a RenderPlan: time runs from 0 through the kept pieces only. The
///     overlay is drawn from telemetry moved onto it (MapFrames), so everything it shows describes the video
///     as watched - the route intro, elapsed time, distance, trip progress, the map's route, max speed - while
///     the wall clock (GPS time) stays real.
/// </summary>
public sealed class OutputTimeline
{
	private readonly double _fps;
	private readonly long[] _outputStartFrames;

	public OutputTimeline(RenderPlan plan, double fps)
	{
		Plan = plan;
		_fps = fps;
		_outputStartFrames = new long[plan.Pieces.Count];
		long outputFrame = 0;
		for (int i = 0; i < plan.Pieces.Count; i++)
		{
			// An overlapping transition starts the piece while the one before it is still on screen.
			outputFrame -= plan.Pieces[i].OverlapIn;
			_outputStartFrames[i] = outputFrame;
			outputFrame += plan.Pieces[i].FrameCount;
		}
	}

	public RenderPlan Plan { get; }

	/// <summary>The output's length - every kept frame back to back.</summary>
	public double DurationSeconds => Plan.TotalFrames / _fps;

	/// <summary>Where a moment of the recording lands in the output, or null if it was cut out.</summary>
	public double? ToOutputSeconds(double recordingSeconds)
	{
		double frame = recordingSeconds * _fps;
		for (int i = 0; i < Plan.Pieces.Count; i++)
		{
			RenderPiece piece = Plan.Pieces[i];
			// Half a frame of slack on each side: preview positions sit a little off exact frame times.
			if (frame >= piece.SourceStartFrame - 0.5 && frame < piece.SourceEndFrame - 0.5)
				return Math.Max(0, _outputStartFrames[i] + frame - piece.SourceStartFrame) / _fps;
		}

		return null;
	}

	/// <summary>
	///     How far the picture at a moment of the recording is faded by the transition of a cut next to its piece (see
	///     CutTransition) - null outside every fade. The overlay isn't faded.
	/// </summary>
	public (double Amount, bool White)? TransitionAt(double recordingSeconds)
	{
		double frame = recordingSeconds * _fps;
		foreach (RenderPiece piece in Plan.Pieces)
		{
			if (frame < piece.SourceStartFrame - 0.5 || frame >= piece.SourceEndFrame - 0.5) continue;
			if (piece.TransitionIn is not { Overlaps: false } && piece.TransitionOut is not { Overlaps: false }) return null;

			long k = Math.Clamp((long)Math.Round(frame - piece.SourceStartFrame), 0, piece.FrameCount - 1);
			(double amount, bool white) = CutTransitionFade.At(piece, k, _fps);
			return amount > 0 ? (amount, white) : null;
		}

		return null;
	}

	/// <summary>
	///     Like ToOutputSeconds, but a moment cut out (or before the first piece) lands where the output goes on - the start
	///     of the next kept piece - and one past the last piece at the output's end. For dragging something on the
	///     recording's timeline that is timed on the output's (a widget's appear/disappear).
	/// </summary>
	public double NearestOutputSeconds(double recordingSeconds)
	{
		if (ToOutputSeconds(recordingSeconds) is { } output) return output;

		double frame = recordingSeconds * _fps;
		for (int i = 0; i < Plan.Pieces.Count; i++)
		{
			if (frame < Plan.Pieces[i].SourceStartFrame)
				return _outputStartFrames[i] / _fps;
		}

		return Plan.TotalFrames / _fps;
	}

	/// <summary>The moment of the recording shown at a time of the output - the output's end maps to the last piece's end.</summary>
	public double ToRecordingSeconds(double outputSeconds)
	{
		double frame = Math.Max(0, outputSeconds * _fps);
		for (int i = 0; i < Plan.Pieces.Count; i++)
		{
			RenderPiece piece = Plan.Pieces[i];
			if (frame < _outputStartFrames[i] + piece.FrameCount || i == Plan.Pieces.Count - 1)
				return (piece.SourceStartFrame + Math.Min(frame - _outputStartFrames[i], piece.FrameCount)) / _fps;
		}

		return 0;
	}

	/// <summary>
	///     Where playback from a moment of the recording goes on, in recording seconds: the rest of the kept
	///     piece it is in, or the whole next piece if it was cut out; null once no piece is left. Passing the
	///     returned End gives the piece after it.
	/// </summary>
	public (double Start, double End)? NextKeptStretch(double recordingSeconds)
	{
		double frame = recordingSeconds * _fps;
		foreach (RenderPiece piece in Plan.Pieces)
		{
			if (frame >= piece.SourceEndFrame - 0.5) continue;

			// The first frames of a piece that overlaps the one before it are shown as the end of that one (the preview doesn't
			// blend them), so playback goes on after them - the output's time stays what the render's is.
			long playFrom = piece.SourceStartFrame + piece.OverlapIn;
			double start = frame >= playFrom - 0.5 ? recordingSeconds : playFrom / _fps;
			return (start, piece.SourceEndFrame / _fps);
		}

		return null;
	}

	/// <summary>
	///     The recording's telemetry for the output: only frames inside kept pieces, SampleTimeSeconds on the
	///     output timeline (the recording time kept as SourceTimeSeconds), and the running distance summed
	///     over the kept pieces only - a cut-out stretch adds nothing, and neither does the jump across it.
	///     Speed, heading, gradient and smoothing come from processing the whole recording, so they stay
	///     right at the edges of a piece.
	/// </summary>
	public List<DerivedFrame> MapFrames(IReadOnlyList<DerivedFrame> recording)
	{
		List<DerivedFrame> output = [];
		double distanceBefore = 0;

		for (int p = 0; p < Plan.Pieces.Count; p++)
		{
			RenderPiece piece = Plan.Pieces[p];
			double pieceStart = piece.SourceStartFrame / _fps;
			double pieceEnd = piece.SourceEndFrame / _fps;
			double outputStart = _outputStartFrames[p] / _fps;

			// The overlap's frames are the previous piece's telemetry, already mapped - the output has one sample there, not two.
			int first = TelemetryProcessor.FindIndex(recording, pieceStart + piece.OverlapIn / _fps - 0.5 / _fps);
			List<DerivedFrame> kept = [];
			for (int i = first; i < recording.Count && recording[i].Raw.SampleTimeSeconds < pieceEnd - 0.5 / _fps; i++)
				kept.Add(recording[i]);
			if (kept.Count == 0) continue;

			double[] distances = TelemetryProcessor.SteppedDistances([.. kept.Select(f => f.Raw)]);
			for (int i = 0; i < kept.Count; i++)
			{
				DerivedFrame frame = kept[i];
				double outputSeconds = outputStart + Math.Max(0, frame.Raw.SampleTimeSeconds - pieceStart);
				output.Add(frame with
				{
					Raw = frame.Raw with { SampleTimeSeconds = outputSeconds, SourceTimeSeconds = frame.Raw.RecordingTimeSeconds },
					CumulativeDistanceMeters = distanceBefore + distances[i],
					StartsAfterCut = frame.StartsAfterCut || (i == 0 && output.Count > 0)
				});
			}

			distanceBefore += distances[^1];
		}

		return output;
	}
}
