using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     The music of the preview's playback (MusicTrack): mixed into each chunk of the recording's sound as it is pushed,
///     the way the render mixes it - the clips on the output's timeline, the recording's own sound lowered where a clip asks
///     for it. Each clip has a decoder of its own, opened when it is first needed and converted to the recording's sound's
///     rate and channels. Used by the feed thread only; not thread-safe.
/// </summary>
internal sealed class MusicMixer : IDisposable
{
	// How far a clip's decoder may be from where the next chunk needs it before it is moved (seconds).
	private const double SeekToleranceSeconds = 0.01;

	private readonly Func<IReadOnlyList<MusicClip>> _clips;
	private readonly Func<double, double?> _outputSeconds;
	private readonly int _sampleRate;
	private readonly int _channels;
	private readonly Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);
	private float[] _mixed = [];
	private IReadOnlyList<MusicClip>? _crossfadeSource;
	private IReadOnlyList<MusicClip> _crossfaded = [];

	/// <param name="outputSeconds">A moment of the recording as a moment of the output (OutputTimeline.ToOutputSeconds), null where it is cut out.</param>
	public MusicMixer(Func<IReadOnlyList<MusicClip>> clips, Func<double, double?> outputSeconds, int sampleRate, int channels)
	{
		_clips = clips;
		_outputSeconds = outputSeconds;
		_sampleRate = sampleRate;
		_channels = channels;
	}

	/// <summary>True when there is any music to mix - the chunk can then go through unchanged.</summary>
	public bool HasMusic => _clips().Count > 0;

	/// <summary>
	///     The chunk (interleaved samples, starting at `recordingSeconds`) with the music mixed in and the recording's sound
	///     lowered under it - a copy, the caller's buffer is the decoder's. Returns the chunk itself when there is nothing to change.
	/// </summary>
	public ReadOnlySpan<float> Mix(ReadOnlySpan<float> samples, double recordingSeconds)
	{
		IReadOnlyList<MusicClip> clips = Crossfaded(_clips());
		if (clips.Count == 0 || _outputSeconds(recordingSeconds) is not { } start) return samples;

		int frames = samples.Length / _channels;
		double end = start + (double)frames / _sampleRate;
		if (!clips.Any(c => c.StartSeconds < end && c.EndSeconds > start)) return samples;

		if (_mixed.Length < samples.Length) _mixed = new float[samples.Length];
		Span<float> mixed = _mixed.AsSpan(0, samples.Length);

		double duck0 = MusicTrack.DuckDbAt(clips, start), duck1 = MusicTrack.DuckDbAt(clips, end);
		for (int i = 0; i < frames; i++)
		{
			float gain = duck0 == 0 && duck1 == 0 ? 1 : (float)VolumeLine.Gain(duck0 + (duck1 - duck0) * i / frames);
			for (int c = 0; c < _channels; c++) mixed[i * _channels + c] = samples[i * _channels + c] * gain;
		}

		foreach (MusicClip clip in clips)
		{
			if (clip.StartSeconds >= end || clip.EndSeconds <= start) continue;

			AddClip(clip, mixed, start, frames);
		}

		return mixed;
	}

	/// <summary>The clips with their crossfades (MusicTrack.WithCrossfades), worked out again only when the list changes.</summary>
	private IReadOnlyList<MusicClip> Crossfaded(IReadOnlyList<MusicClip> clips)
	{
		if (!ReferenceEquals(clips, _crossfadeSource))
		{
			_crossfadeSource = clips;
			_crossfaded = MusicTrack.WithCrossfades(clips);
		}

		return _crossfaded;
	}

	private void AddClip(MusicClip clip, Span<float> mixed, double chunkStart, int chunkFrames)
	{
		if (!_tracks.TryGetValue(clip.Path, out Track? track)) _tracks[clip.Path] = track = Track.Open(clip.Path, _sampleRate, _channels);
		if (track.Source is not { } source) return;

		int first = (int)Math.Clamp(Math.Round((clip.StartSeconds - chunkStart) * _sampleRate), 0, chunkFrames);
		int limit = (int)Math.Min(chunkFrames, Math.Round((clip.EndSeconds - chunkStart) * _sampleRate));
		if (limit <= first) return;

		double clipTime = Math.Max(0, chunkStart - clip.StartSeconds);
		if (Math.Abs(track.ClipTime - clipTime) > SeekToleranceSeconds)
		{
			source.Seek(MusicTrack.FilePosition(clip, clipTime));
			track.Clear();
			track.ClipTime = clipTime;
		}

		int count = track.Fill(limit - first, clip, _channels);
		if (count == 0 && !track.Reported)
		{
			track.Reported = true;
			AppLogger.Info($"Music preview: no samples decoded from {clip.Path}");
		}

		double gain = VolumeLine.Gain(clip.Db);
		ReadOnlySpan<float> queued = track.Queued;
		for (int i = 0; i < count; i++)
		{
			float level = (float)(gain * MusicTrack.FadeAt(clip, clipTime + (double)i / _sampleRate));
			for (int c = 0; c < _channels; c++) mixed[(first + i) * _channels + c] += queued[i * _channels + c] * level;
		}

		track.Consume(count * _channels);
		track.ClipTime += (double)count / _sampleRate;
	}

	public void Dispose()
	{
		foreach (Track track in _tracks.Values) track.Source?.Dispose();
		_tracks.Clear();
	}

	private sealed class Track
	{
		private float[] _queue = [];
		private int _head;
		private int _length;

		public LibavAudioSource? Source { get; private init; }

		/// <summary>Where in the clip (seconds) the first queued sample is.</summary>
		public double ClipTime { get; set; } = double.NegativeInfinity;

		public bool Reported { get; set; }

		public ReadOnlySpan<float> Queued => _queue.AsSpan(_head, _length);

		public void Clear()
		{
			_head = 0;
			_length = 0;
		}

		public void Consume(int samples)
		{
			_head += samples;
			_length -= samples;
			if (_length == 0) _head = 0;
		}

		/// <summary>Decodes until `frames` sample frames are queued (fewer at the end of the file) and returns how many are.</summary>
		public int Fill(int frames, MusicClip clip, int channels)
		{
			bool wrapped = false;
			while (_length < frames * channels && Source is { } source)
			{
				ReadOnlySpan<float> read = source.Read(double.MaxValue);
				if (read.IsEmpty)
				{
					if (!clip.Repeats || wrapped) break;

					source.Seek(clip.InSeconds);
					wrapped = true;
					continue;
				}

				wrapped = false;
				if (_head + _length + read.Length > _queue.Length)
				{
					float[] grown = _length + read.Length > _queue.Length ? new float[(_length + read.Length) * 2] : _queue;
					Array.Copy(_queue, _head, grown, 0, _length);
					_queue = grown;
					_head = 0;
				}

				read.CopyTo(_queue.AsSpan(_head + _length));
				_length += read.Length;
			}

			return Math.Min(_length / channels, frames);
		}

		public static Track Open(string path, int sampleRate, int channels)
		{
			try
			{
				return new Track { Source = LibavAudioSource.TryOpen([new PlaybackSegment(path, 0, 0)], sampleRate, channels) };
			}
			catch (Exception ex) when (ex is InvalidOperationException or IOException)
			{
				AppLogger.Warn(ex, string.Format(CoreStrings.Preview_MusicFailed, path));
				return new Track();
			}
		}
	}
}
