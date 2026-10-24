using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     One widget on a layer: what it shows (Label) and when it's on screen, in output seconds - OverlayElement's own timing,
///     the way out as stored (null: none, see ElementAnimation). StartLocked: its start isn't its own to drag (a progress bar
///     waiting for a speed) - its edge doesn't grab and its body doesn't move it in time; EndLocked the same for the end
///     (a progress bar leaving after the finish). TimingFixed: the widget times itself (the GPS alert), so nothing about its
///     timing is dragged - no edge, no fade handle, no move in time (a drop onto another layer still works). A locked clip
///     shows it (a padlock at the locked edge, a dashed outline when all of its timing is) and says why in LockHint, its tooltip.
/// </summary>
public sealed record LayerClip(
	string Id,
	string Label,
	double? AppearAtSeconds,
	double? DisappearAtSeconds,
	OverlayAnimationType Animation,
	double AnimationDurationSeconds,
	OverlayAnimationType? OutAnimation,
	double? OutAnimationDurationSeconds,
	bool Available,
	bool StartLocked = false,
	bool EndLocked = false,
	bool TimingFixed = false,
	string? LockHint = null)
{
	public bool AnyLocked => StartLocked || EndLocked || TimingFixed;

	public OverlayAnimationType ResolvedOut => ElementAnimation.OutType(OutAnimation);
	public double ResolvedOutSeconds => ElementAnimation.OutDuration(OutAnimationDurationSeconds);
}

/// <summary>
///     A layer (OverlayLayer): its widgets in draw order (the last drawn in front). Silenced: not drawn right now - muted, or
///     another layer is soloed.
/// </summary>
public sealed record LayerTrack(string Key, string Name, IReadOnlyList<LayerClip> Clips, bool Muted, bool Solo, bool Locked, bool Silenced);

public enum LayerSwitch
{
	Mute,
	Solo,
	Lock
}

public sealed record LayerTiming(
	string Id,
	double? AppearAtSeconds,
	double? DisappearAtSeconds,
	OverlayAnimationType Animation,
	double AnimationDurationSeconds,
	OverlayAnimationType? OutAnimation,
	double? OutAnimationDurationSeconds);
