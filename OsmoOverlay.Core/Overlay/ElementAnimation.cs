namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Where a widget's appear/disappear animation is at a moment: Progress 0..1 (its opacity, and how far a slide has
///     come), and the animation running - Leaving on the way out. Progress 1 is at rest.
/// </summary>
public readonly record struct ElementState(float Progress, OverlayAnimationType Animation, bool Leaving)
{
	public static readonly ElementState Shown = new(1f, OverlayAnimationType.None, false);
	public static readonly ElementState Hidden = new(0f, OverlayAnimationType.None, false);
}

/// <summary>
///     A widget's timing (OverlayElement.AppearAtSeconds etc.) resolved into what the renderer draws. The way in and the way
///     out are separate: AnimationType/AnimationDurationSeconds for coming in, OutAnimationType/OutAnimationDurationSeconds
///     for going out - none set means no way out (a hard cut, like None), its length AnimationDurationSecondsDefault until
///     one is set. None is a hard cut. A window too short for both ramps
///     starts the way out only once the way in finished, rather than the two crossing. A widget with no DisappearAtSeconds
///     stays to the end of the video - its way out, if it has one, ends with the video (endSeconds).
/// </summary>
public static class ElementAnimation
{
	private const double MinDurationSeconds = 0.05;

	public static OverlayAnimationType OutType(OverlayAnimationType? outType)
	{
		return outType ?? OverlayAnimationType.None;
	}

	public static double OutDuration(double? outSeconds)
	{
		return outSeconds ?? OverlayRenderer.AnimationDurationSecondsDefault;
	}

	/// <param name="endSeconds">The rendered video's length, on the same (output) timeline as `seconds`.</param>
	public static ElementState At(OverlayElement element, double seconds, double endSeconds = double.PositiveInfinity)
	{
		OverlayAnimationType inType = element.AnimationType;
		OverlayAnimationType outType = OutType(element.OutAnimationType);
		if (element.AppearAtSeconds is null && element.DisappearAtSeconds is null && inType == OverlayAnimationType.None &&
		    outType == OverlayAnimationType.None)
			return ElementState.Shown;

		var appear = element.AppearAtSeconds ?? 0;
		var disappear = element.DisappearAtSeconds ?? endSeconds;
		if (seconds < appear || seconds >= disappear) return ElementState.Hidden;

		var inLength = inType == OverlayAnimationType.None ? 0 : Math.Max(element.AnimationDurationSeconds, MinDurationSeconds);
		var outLength = outType == OverlayAnimationType.None ? 0 : Math.Max(OutDuration(element.OutAnimationDurationSeconds), MinDurationSeconds);
		var inEnd = appear + inLength;
		var outStart = Math.Max(disappear - outLength, inEnd);

		if (inLength > 0 && seconds < inEnd) return new ElementState((float)((seconds - appear) / inLength), inType, false);
		if (outLength > 0 && seconds > outStart && disappear > outStart)
			return new ElementState((float)((disappear - seconds) / (disappear - outStart)), outType, true);

		return ElementState.Shown;
	}
}
