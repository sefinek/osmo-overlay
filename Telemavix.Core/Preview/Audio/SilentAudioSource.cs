namespace Telemavix.Core.Preview;

/// <summary>
///     Silence for a recording with no sound of its own (a drone's, a GPS track alone): the device and the clock run as they
///     do with sound, and the music (MusicMixer) is mixed into it - without it the preview of a silent recording couldn't play music.
/// </summary>
internal sealed class SilentAudioSource : IAudioSamples
{
	public const int DefaultSampleRate = 48000;
	public const int DefaultChannels = 2;
	private const double ChunkSeconds = 0.05;

	private readonly float[] _silence;
	private double _position;

	public SilentAudioSource(int sampleRate = DefaultSampleRate, int channels = DefaultChannels)
	{
		SampleRate = sampleRate;
		Channels = channels;
		_silence = new float[(int)(sampleRate * ChunkSeconds) * channels];
	}

	public int SampleRate { get; }
	public int Channels { get; }

	public void Seek(double seconds)
	{
		_position = seconds;
	}

	public ReadOnlySpan<float> Read(double endSeconds)
	{
		int frames = (int)Math.Min(_silence.Length / Channels, Math.Floor((endSeconds - _position) * SampleRate));
		if (frames <= 0) return [];

		_position += (double)frames / SampleRate;
		return _silence.AsSpan(0, frames * Channels);
	}

	public void Dispose()
	{
	}
}
