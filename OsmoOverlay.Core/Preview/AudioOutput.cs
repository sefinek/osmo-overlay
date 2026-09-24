using OsmoOverlay.Core.Logging;
using SDL;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     The default playback device through SDL3, fed by pushing samples: SDL's audio stream converts the pushed
///     format/rate/channels to the device's and follows device changes (headphones plugged in) on its own. Opened
///     paused; Stop also drops whatever is still queued, so a pause is silent at once. Thread-safe; a no-op after
///     Dispose.
/// </summary>
public sealed unsafe class AudioOutput : IAudioClockSource, IDisposable
{
	private static readonly Lock InitGate = new();
	private static bool _sdlAudioReady;

	private readonly Lock _lock = new();
	private readonly SDL_AudioStream* _stream;
	private readonly int _bytesPerSecond;
	private bool _disposed;

	private AudioOutput(SDL_AudioStream* stream, int sampleRate, int channels)
	{
		_stream = stream;
		_bytesPerSecond = sampleRate * channels * sizeof(float);

		SDL_AudioSpec deviceSpec;
		int deviceFrames;
		if (SDL3.SDL_GetAudioDeviceFormat(SDL3.SDL_GetAudioStreamDevice(stream), &deviceSpec, &deviceFrames) && deviceSpec.freq > 0)
			DeviceLatencySeconds = (double)deviceFrames / deviceSpec.freq;
	}

	/// <summary>The device's own buffer, played after what's still queued in the stream.</summary>
	public double DeviceLatencySeconds { get; }

	/// <summary>Audio pushed but not yet handed to the device.</summary>
	public double QueuedSeconds
	{
		get
		{
			lock (_lock)
			{
				return _disposed ? 0 : (double)SDL3.SDL_GetAudioStreamQueued(_stream) / _bytesPerSecond;
			}
		}
	}

	/// <summary>Null (logged) when SDL or the device can't be opened - the preview then plays silent, paced by a stopwatch.</summary>
	public static AudioOutput? TryOpen(int sampleRate, int channels)
	{
		lock (InitGate)
		{
			if (!_sdlAudioReady && !(_sdlAudioReady = SDL3.SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO)))
			{
				AppLogger.Warn($"Preview audio unavailable - SDL couldn't start audio: {SDL3.SDL_GetError()}");
				return null;
			}
		}

		var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = channels, freq = sampleRate };
		SDL_AudioStream* stream = SDL3.SDL_OpenAudioDeviceStream(SDL3.SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, null, IntPtr.Zero);
		if (stream is null)
		{
			AppLogger.Warn($"Preview audio unavailable - no playback device: {SDL3.SDL_GetError()}");
			return null;
		}

		return new AudioOutput(stream, sampleRate, channels);
	}

	public void Push(ReadOnlySpan<float> samples)
	{
		if (samples.IsEmpty) return;

		lock (_lock)
		{
			if (_disposed) return;

			fixed (float* data = samples)
			{
				SDL3.SDL_PutAudioStreamData(_stream, (IntPtr)data, samples.Length * sizeof(float));
			}
		}
	}

	/// <summary>0 = silent, 1 = the recording's own level.</summary>
	public void SetGain(float gain)
	{
		lock (_lock)
		{
			if (!_disposed)
				SDL3.SDL_SetAudioStreamGain(_stream, gain);
		}
	}

	public void Start()
	{
		lock (_lock)
		{
			if (!_disposed)
				SDL3.SDL_ResumeAudioStreamDevice(_stream);
		}
	}

	public void Stop()
	{
		lock (_lock)
		{
			if (_disposed) return;

			SDL3.SDL_PauseAudioStreamDevice(_stream);
			SDL3.SDL_ClearAudioStream(_stream);
		}
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (_disposed) return;

			_disposed = true;
			SDL3.SDL_DestroyAudioStream(_stream);
		}
	}
}
