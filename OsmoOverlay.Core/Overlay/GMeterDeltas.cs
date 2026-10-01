using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     What the G-meter shows at every frame: the dynamic acceleration (lateral, longitudinal) relative to the camera's own
///     slow-moving baseline. A raw reading sits off-center depending on how the camera is mounted, so each axis is
///     measured against an 8 s average, and the signal itself goes through a fast 0.3 s average to tame vibration - two
///     EMAs racing toward the same reading. Computed once per set of frames instead of while drawing: a value that
///     depends on the frames drawn before it reads 0 on a frame drawn again (a pause, a seek, a layout edit).
///     The averages start over at the first frame, after a cut and after a gap longer than ResetGapSeconds.
/// </summary>
internal static class GMeterDeltas
{
	private const double BaselineSeconds = 8.0;
	private const double SmoothingSeconds = 0.3;
	private const double ResetGapSeconds = 2.0;

	public static (double Lateral, double Longitudinal)[] Compute(IReadOnlyList<DerivedFrame> frames)
	{
		var deltas = new (double, double)[frames.Count];
		double baselineLateral = 0, baselineLongitudinal = 0, smoothedLateral = 0, smoothedLongitudinal = 0;
		double previousSeconds = 0;

		for (int i = 0; i < frames.Count; i++)
		{
			DerivedFrame frame = frames[i];
			double seconds = frame.Raw.SampleTimeSeconds;
			double dt = seconds - previousSeconds;

			if (i == 0 || frame.StartsAfterCut || dt < 0 || dt > ResetGapSeconds)
			{
				baselineLateral = smoothedLateral = frame.LateralAccelG;
				baselineLongitudinal = smoothedLongitudinal = frame.LongitudinalAccelG;
			}
			else if (dt > 0)
			{
				double baselineAlpha = 1.0 - Math.Exp(-dt / BaselineSeconds);
				double smoothingAlpha = 1.0 - Math.Exp(-dt / SmoothingSeconds);
				baselineLateral += (frame.LateralAccelG - baselineLateral) * baselineAlpha;
				baselineLongitudinal += (frame.LongitudinalAccelG - baselineLongitudinal) * baselineAlpha;
				smoothedLateral += (frame.LateralAccelG - smoothedLateral) * smoothingAlpha;
				smoothedLongitudinal += (frame.LongitudinalAccelG - smoothedLongitudinal) * smoothingAlpha;
			}

			previousSeconds = seconds;
			deltas[i] = (smoothedLateral - baselineLateral, smoothedLongitudinal - baselineLongitudinal);
		}

		return deltas;
	}
}
