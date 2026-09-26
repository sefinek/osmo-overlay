using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using OsmoOverlay.Core.Preview;
using SkiaSharp;

namespace OsmoOverlay.Gui;

/// <summary>
///     The preview's video, stretched over the control's bounds like an Image with Stretch="Fill". Played frames are
///     taken and drawn on the compositor's render thread at every display refresh (PlaybackFrameSource), so the UI
///     thread isn't in the path of a played frame: asking the UI thread for every refresh (RequestAnimationFrame)
///     got its callbacks at uneven intervals - measured 90-180 a second on 144/280 Hz displays, frames up to 57 ms
///     late - since each one also had to lay out and commit the window. Stills (paused, seeking) come from the UI
///     thread (ShowStill). Each frame is uploaded to the GPU once and drawn from there; a played frame's buffer goes
///     back to the preview's pool as soon as Skia is done with it, without a copy.
/// </summary>
public sealed class VideoView : Control
{
	private CompositionCustomVisual? _visual;
	private long _shownTicks;
	private long _shownFpsBits;
	private int _shownPosted;

	/// <summary>
	///     A played frame reached the screen - raised on the UI thread with the latest one, at most once per UI turn, and
	///     the played frames a second that reached it over the last second (NaN until there's enough to tell).
	/// </summary>
	public event Action<TimeSpan, double>? PlaybackFrameShown;

	/// <summary>Copies the still (FrameReady's buffer is only lent for the callback) and shows it next.</summary>
	public void ShowStill(ComposedPreviewFrame frame)
	{
		var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
		Send(SKImage.FromPixelCopy(info, frame.Bgra, info.RowBytes));
	}

	/// <summary>From here on until playback stops, frames come from the source on the render thread.</summary>
	public void StartPlayback(PlaybackFrameSource source)
	{
		Send(source);
	}

	public void Clear()
	{
		Send(ClearMessage.Instance);
	}

	private void Send(object message)
	{
		if (_visual is { } visual) visual.SendHandlerMessage(message);
		else if (message is SKImage image) image.Dispose();
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		if (ElementComposition.GetElementVisual(this)?.Compositor is not { } compositor) return;

		_visual = compositor.CreateCustomVisual(new Handler(OnShownOnRenderThread));
		_visual.Size = new Vector(Bounds.Width, Bounds.Height);
		ElementComposition.SetElementChildVisual(this, _visual);
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		Send(ClearMessage.Instance);
		ElementComposition.SetElementChildVisual(this, null);
		_visual = null;
		base.OnDetachedFromVisualTree(e);
	}

	protected override void OnSizeChanged(SizeChangedEventArgs e)
	{
		base.OnSizeChanged(e);
		if (_visual is { } visual) visual.Size = new Vector(e.NewSize.Width, e.NewSize.Height);
	}

	/// <summary>Coalesced: a UI thread that's busy for a moment gets the newest position once, not a queue of them.</summary>
	private void OnShownOnRenderThread(TimeSpan position, double framesPerSecond)
	{
		Interlocked.Exchange(ref _shownTicks, position.Ticks);
		Interlocked.Exchange(ref _shownFpsBits, BitConverter.DoubleToInt64Bits(framesPerSecond));
		if (Interlocked.Exchange(ref _shownPosted, 1) == 1) return;

		Dispatcher.UIThread.Post(() =>
		{
			Volatile.Write(ref _shownPosted, 0);
			PlaybackFrameShown?.Invoke(TimeSpan.FromTicks(Interlocked.Read(ref _shownTicks)),
				BitConverter.Int64BitsToDouble(Interlocked.Read(ref _shownFpsBits)));
		});
	}

	private sealed class ClearMessage
	{
		public static readonly ClearMessage Instance = new();
	}

	/// <summary>Everything here runs on the render thread.</summary>
	private sealed class Handler(Action<TimeSpan, double> onShown) : CompositionCustomVisualHandler
	{
		// Plain linear, like a video player: the preview is sized to its view, and mipmaps built for every frame would
		// only add GPU work next to the decoder's.
		private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

		private PlaybackFrameSource? _source;
		// The newest frame or still waiting for OnRender to upload it - a newer one replaces it unshown.
		private ComposedPreviewFrame? _pendingFrame;
		private SKImage? _pendingStill;
		private SKImage? _image;
		// When the last second's played frames reached the screen, for the frames a second it shows.
		private readonly Queue<long> _shownAt = new();

		public override void OnMessage(object message)
		{
			switch (message)
			{
				case SKImage still:
					DropPending();
					_pendingStill = still;
					Invalidate();
					break;
				case PlaybackFrameSource source:
					_source = source;
					_shownAt.Clear();
					RegisterForNextAnimationFrameUpdate();
					break;
				case ClearMessage:
					DropPending();
					_image?.Dispose();
					_image = null;
					_source = null;
					Invalidate();
					break;
			}
		}

		public override void OnAnimationFrameUpdate()
		{
			if (_source is not { } source) return;

			if (source.TakeDueFrame() is { } frame)
			{
				DropPending();
				_pendingFrame = frame;
				Invalidate();
			}

			if (source.IsPlaying) RegisterForNextAnimationFrameUpdate();
			else _source = null;
		}

		public override void OnRender(ImmediateDrawingContext context)
		{
			if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } skia)
			{
				DropPending();
				return;
			}

			using ISkiaSharpApiLease lease = skia.Lease();
			TimeSpan? playedPosition = _pendingFrame?.Position;
			if (TakePendingImage(lease.GrContext) is { } image)
			{
				_image?.Dispose();
				_image = image;
				if (playedPosition is { } position) onShown(position, CountShown());
			}

			if (_image is null) return;

			Rect bounds = GetRenderBounds();
			lease.SkCanvas.DrawImage(_image, new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom),
				Sampling);
		}

		/// <summary>Frames a second over the last second, counting this one - NaN over less than a quarter of a second.</summary>
		private double CountShown()
		{
			var now = Stopwatch.GetTimestamp();
			_shownAt.Enqueue(now);
			while (Stopwatch.GetElapsedTime(_shownAt.Peek(), now).TotalSeconds > 1) _shownAt.Dequeue();

			var span = Stopwatch.GetElapsedTime(_shownAt.Peek(), now).TotalSeconds;
			return span >= 0.25 ? (_shownAt.Count - 1) / span : double.NaN;
		}

		/// <summary>The pending frame or still as an image to draw - on the GPU when there is one, uploaded once here.</summary>
		private SKImage? TakePendingImage(GRContext? gpu)
		{
			SKImage? raster = null;
			if (_pendingFrame is { } frame)
			{
				_pendingFrame = null;
				raster = Borrow(frame);
			}
			else if (_pendingStill is { } still)
			{
				_pendingStill = null;
				raster = still;
			}

			if (raster is null || gpu is null) return raster;

			SKImage? texture = raster.ToTextureImage(gpu, false);
			if (texture is null) return raster;

			raster.Dispose();
			return texture;
		}

		/// <summary>The frame's buffer as an image without a copy - back to its pool once Skia lets go of the pixels.</summary>
		private static SKImage? Borrow(ComposedPreviewFrame frame)
		{
			var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
			GCHandle pin = GCHandle.Alloc(frame.Bgra, GCHandleType.Pinned);
			using var pixmap = new SKPixmap(info, pin.AddrOfPinnedObject(), info.RowBytes);
			SKImage? image = SKImage.FromPixels(pixmap, (_, _) => GiveBack(pin, frame));
			if (image is null) GiveBack(pin, frame);
			return image;
		}

		private static void GiveBack(GCHandle pin, ComposedPreviewFrame frame)
		{
			pin.Free();
			PlaybackFrameSource.Release(frame);
		}

		private void DropPending()
		{
			if (_pendingFrame is { } frame) PlaybackFrameSource.Release(frame);
			_pendingFrame = null;
			_pendingStill?.Dispose();
			_pendingStill = null;
		}
	}
}
