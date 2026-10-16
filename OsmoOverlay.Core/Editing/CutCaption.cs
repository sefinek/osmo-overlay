namespace OsmoOverlay.Core;

/// <summary>
///     A text shown over the picture where a cut joins the part kept before it to the one after: from the join for `Seconds`,
///     fading in and out, in a panel at the middle of the frame. It says what was left out ("a military unit was here") - a cut
///     at the very start or end has no join, so no caption. Unlike CutTransition it never changes the frame count.
/// </summary>
public sealed record CutCaption(string Text, double Seconds = CutCaption.DefaultSeconds)
{
	public const double DefaultSeconds = 3;
	public const double MinSeconds = 0.5;
	public const double MaxSeconds = 30;

	/// <summary>The caption as typed - null for a blank one (which is no caption).</summary>
	public static CutCaption? Of(string? text, double seconds)
	{
		return string.IsNullOrWhiteSpace(text) ? null : new CutCaption(text.Trim(), Math.Clamp(seconds, MinSeconds, MaxSeconds));
	}
}

/// <summary>
///     A cut's caption placed on the output's timeline: shown from StartSeconds until EndSeconds (never past the output's end),
///     fading in and out over FadeSeconds - as long as the cut's transition, so the two move together.
/// </summary>
public sealed record CutCaptionAt(double StartSeconds, double EndSeconds, string Text, double FadeSeconds = CutCaptionAt.DefaultFadeSeconds)
{
	/// <summary>The fade of a caption at a cut with no transition to match.</summary>
	public const double DefaultFadeSeconds = 0.4;
}
