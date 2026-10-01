using System.Globalization;

namespace OsmoOverlay.Core;

/// <summary>
///     The first two fade through a color (no overlap); the rest overlap the part before the cut with the part after it
///     (ffmpeg's xfade) and so shorten the output - see CutTransition.
/// </summary>
public enum CutTransitionKind
{
	FadeBlack,
	FadeWhite,
	Crossfade,
	/// <summary>The next part comes in from the right edge, covering the one before.</summary>
	WipeLeft,
	/// <summary>The next part comes in from the left edge.</summary>
	WipeRight,
	/// <summary>The next part pushes the one before out to the left.</summary>
	SlideLeft,
	/// <summary>The next part pushes the one before out to the right.</summary>
	SlideRight
}

/// <summary>
///     What happens where a cut joins the part kept before it to the part kept after it. LengthSeconds is how long the
///     transition lasts on screen.
///     A fade through black or white (a "dip") fades the picture and the sound out into the color and the next part in from
///     it, half each, both halves inside the kept parts: no frame is added or removed.
///     The others (Crossfade, Wipe*, Slide*) show the end of the part before and the start of the part after at the same
///     time, so the two overlap for LengthSeconds and the output is that much shorter at each such cut; the telemetry and
///     the overlay go on from the earlier part through the overlap. The overlay itself isn't faded or moved either way.
///     A cut at the very start or end has nothing to join, so it has no transition.
/// </summary>
public sealed record CutTransition(CutTransitionKind Kind, double LengthSeconds = CutTransition.DefaultLengthSeconds)
{
	public const double DefaultLengthSeconds = 1.0;
	public const double MinLengthSeconds = 0.2;
	public const double MaxLengthSeconds = 5.0;

	public bool IsWhite => Kind == CutTransitionKind.FadeWhite;

	/// <summary>True for the transitions that overlap the two parts (and shorten the output) instead of fading through a color.</summary>
	public bool Overlaps => Kind >= CutTransitionKind.Crossfade;

	/// <summary>The color ffmpeg's fade filter takes.</summary>
	public string ColorName => IsWhite ? "white" : "black";

	/// <summary>The name of ffmpeg's xfade transition for an overlapping kind.</summary>
	public string XfadeName => Kind switch
	{
		CutTransitionKind.WipeLeft => "wipeleft",
		CutTransitionKind.WipeRight => "wiperight",
		CutTransitionKind.SlideLeft => "slideleft",
		CutTransitionKind.SlideRight => "slideright",
		_ => "fade"
	};

	/// <summary>Frames each half of a dip fades over: half the length, at least one, and never more than half the part it lies in - so its two fades never meet.</summary>
	public int HalfFrames(double fps, long pieceFrames)
	{
		long frames = (long)Math.Round(Math.Clamp(LengthSeconds, MinLengthSeconds, MaxLengthSeconds) / 2 * fps);
		return (int)Math.Clamp(frames, 1, Math.Max(1, pieceFrames / 2));
	}

	/// <summary>
	///     Frames the two parts overlap by: the length, but at most half of the shorter part - so a part with a transition
	///     on both its sides never has them meet. 0 when a part is too short to overlap at all (a plain cut then).
	/// </summary>
	public int OverlapFrames(double fps, long frames1, long frames2)
	{
		long most = Math.Min(frames1, frames2) / 2;
		if (most < 1) return 0;

		long wanted = (long)Math.Round(Math.Clamp(LengthSeconds, MinLengthSeconds, MaxLengthSeconds) * fps);
		return (int)Math.Clamp(wanted, 1, most);
	}

	/// <summary>"fade", "white", "cross", "wipeleft", "wiperight", "slideleft" or "slideright", then "=seconds" - the CLI's --cut suffix ("1:00-1:10@fade=1").</summary>
	public string ToArgument()
	{
		string name = Kind switch
		{
			CutTransitionKind.FadeWhite => "white",
			CutTransitionKind.Crossfade => "cross",
			CutTransitionKind.WipeLeft => "wipeleft",
			CutTransitionKind.WipeRight => "wiperight",
			CutTransitionKind.SlideLeft => "slideleft",
			CutTransitionKind.SlideRight => "slideright",
			_ => "fade"
		};
		return string.Create(CultureInfo.InvariantCulture, $"{name}={LengthSeconds:0.###}");
	}

	public static bool TryParse(string text, out CutTransition transition)
	{
		transition = new CutTransition(CutTransitionKind.FadeBlack);
		string[] parts = text.Split('=', 2, StringSplitOptions.TrimEntries);
		CutTransitionKind kind;
		switch (parts[0].ToLowerInvariant())
		{
			case "fade" or "black":
				kind = CutTransitionKind.FadeBlack;
				break;
			case "white":
				kind = CutTransitionKind.FadeWhite;
				break;
			case "cross" or "crossfade" or "dissolve":
				kind = CutTransitionKind.Crossfade;
				break;
			case "wipeleft":
				kind = CutTransitionKind.WipeLeft;
				break;
			case "wiperight":
				kind = CutTransitionKind.WipeRight;
				break;
			case "slideleft":
				kind = CutTransitionKind.SlideLeft;
				break;
			case "slideright":
				kind = CutTransitionKind.SlideRight;
				break;
			default:
				return false;
		}

		double length = DefaultLengthSeconds;
		if (parts.Length == 2 && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out length)) return false;

		transition = new CutTransition(kind, Math.Clamp(length, MinLengthSeconds, MaxLengthSeconds));
		return true;
	}
}
