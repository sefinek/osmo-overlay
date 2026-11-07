using SDL;
using Telemavix.Core.Localization;
using Telemavix.Core.Logging;

namespace Telemavix.Core.Preview;

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

	private const int MaxLevelSlices = 250;

	private readonly Lock _lock = new();
	private readonly SDL_AudioStream* _stream;
	private readonly int _bytesPerSecond;
	private readonly int _sampleRate;
	private readonly int _channels;
	private readonly Queue<LevelSlice> _levels = new();
	private long _pushedFrames;
	private float _gain = 1;
	private bool _disposed;

	private AudioOutput(SDL_AudioStream* stream, int sampleRate, int channels)
	{
		_stream = stream;
		_sampleRate = sampleRate;
		_channels = Math.Max(1, channels);
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
			lock (_lock) return _disposed ? 0 : (double)SDL3.SDL_GetAudioStreamQueued(_stream) / _bytesPerSecond;
		}
	}

	/// <summary>Null (logged) when SDL or the device can't be opened - the preview then plays silent, paced by a stopwatch.</summary>
	public static AudioOutput? TryOpen(int sampleRate, int channels)
	{
		lock (InitGate)
		{
			if (!_sdlAudioReady && !(_sdlAudioReady = SDL3.SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO)))
			{
				AppLogger.Warn(string.Format(CoreStrings.Preview_AudioSdlFailed, SDL3.SDL_GetError()));
				return null;
			}
		}

		var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = channels, freq = sampleRate };
		SDL_AudioStream* stream = SDL3.SDL_OpenAudioDeviceStream(SDL3.SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, null, IntPtr.Zero);
		if (stream is null)
		{
			AppLogger.Warn(string.Format(CoreStrings.Preview_AudioNoDevice, SDL3.SDL_GetError()));
			return null;
		}

		return new AudioOutput(stream, sampleRate, channels);
	}

	/// <summary>
	///     What is coming out of the speakers right now, per channel (left, right; mono gives both the same), as peaks 0-1 after
	///     the gain. Taken from the peaks of what was pushed, looked up at the point the device has reached (pushed minus what's
	///     still queued and the device's buffer). Zero while paused or stopped.
	/// </summary>
	public (float Left, float Right) Level
	{
		get
		{
			lock (_lock)
			{
				if (_disposed) return default;

				long played = _pushedFrames - SDL3.SDL_GetAudioStreamQueued(_stream) / (_channels * sizeof(float))
				                            - (long)(DeviceLatencySeconds * _sampleRate);
				while (_levels.Count > 0 && _levels.Peek().EndFrame <= played) _levels.Dequeue();
				if (_levels.Count == 0) return default;

				LevelSlice slice = _levels.Peek();
				return (slice.Left * _gain, slice.Right * _gain);
			}
		}
	}

	public void Push(ReadOnlySpan<float> samples)
	{
		if (samples.IsEmpty) return;

		lock (_lock)
		{
			if (_disposed) return;

			RecordLevels(samples);
			fixed (float* data = samples) SDL3.SDL_PutAudioStreamData(_stream, (IntPtr)data, samples.Length * sizeof(float));
		}
	}

	private void RecordLevels(ReadOnlySpan<float> samples)
	{
		int frames = samples.Length / _channels;
		int sliceFrames = Math.Max(1, _sampleRate / 50);
		for (int start = 0; start < frames; start += sliceFrames)
		{
			int end = Math.Min(frames, start + sliceFrames);
			float left = 0, right = 0;
			for (int i = start; i < end; i++)
			{
				float l = Math.Abs(samples[i * _channels]);
				float r = _channels > 1 ? Math.Abs(samples[i * _channels + 1]) : l;
				if (l > left) left = l;
				if (r > right) right = r;
			}

			_levels.Enqueue(new LevelSlice(_pushedFrames + end, left, right));
		}

		// Nobody may be reading Level (it is only dequeued there) - keep a few seconds, the queue never holds more than the device does.
		while (_levels.Count > MaxLevelSlices) _levels.Dequeue();

		_pushedFrames += frames;
	}

	private void ResetLevels()
	{
		_levels.Clear();
		_pushedFrames = 0;
	}

	private readonly record struct LevelSlice(long EndFrame, float Left, float Right);

	/// <summary>0 = silent, 1 = the recording's own level.</summary>
	public void SetGain(float gain)
	{
		lock (_lock)
		{
			if (_disposed) return;

			_gain = gain;
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

	public void Flush()
	{
		lock (_lock)
		{
			if (!_disposed) SDL3.SDL_FlushAudioStream(_stream);
		}
	}

	/// <summary>Drops what's queued but keeps the device playing - the sound restarting somewhere else (a speed change).</summary>
	public void Clear()
	{
		lock (_lock)
		{
			if (_disposed) return;

			SDL3.SDL_ClearAudioStream(_stream);
			ResetLevels();
		}
	}

	public void Stop()
	{
		lock (_lock)
		{
			if (_disposed) return;

			SDL3.SDL_PauseAudioStreamDevice(_stream);
			SDL3.SDL_ClearAudioStream(_stream);
			ResetLevels();
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
