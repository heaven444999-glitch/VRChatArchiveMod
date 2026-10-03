using System;
using UnityEngine;

namespace Killiorim.Core
{
	// Minimal 16-bit PCM WAV -> AudioClip decoder. This build's IL2CPP has no
	// DownloadHandlerAudioClip(string, AudioType), so every embedded sound ships as a plain
	// PCM WAV and is turned into a clip here via AudioClip.Create/SetData. Shared by the
	// loading-screen music and the spawn stinger so there is exactly one decoder.
	public static class WavAudio
	{
		public static AudioClip Decode(byte[] d, string name)
		{
			try
			{
				if (d == null || d.Length < 44) return null;
				if (d[0] != (byte)'R' || d[1] != (byte)'I' || d[2] != (byte)'F' || d[3] != (byte)'F') return null;

				int channels = 0, sampleRate = 0, bits = 0, dataAt = -1, dataLen = 0;
				int pos = 12;                                  // skip "RIFF####WAVE"
				while (pos + 8 <= d.Length)
				{
					string id = "" + (char)d[pos] + (char)d[pos + 1] + (char)d[pos + 2] + (char)d[pos + 3];
					int size = BitConverter.ToInt32(d, pos + 4);
					int body = pos + 8;
					if (size < 0 || body + size > d.Length) size = d.Length - body;
					if (id == "fmt ")
					{
						channels = BitConverter.ToInt16(d, body + 2);
						sampleRate = BitConverter.ToInt32(d, body + 4);
						bits = BitConverter.ToInt16(d, body + 14);
					}
					else if (id == "data") { dataAt = body; dataLen = size; }
					pos = body + size + (size & 1);            // chunks are word-aligned
				}
				if (dataAt < 0 || channels <= 0 || sampleRate <= 0 || bits != 16) return null;

				int count = dataLen / 2;
				var samples = new float[count];
				for (int i = 0; i < count; i++)
					samples[i] = BitConverter.ToInt16(d, dataAt + i * 2) / 32768f;

				var clip = AudioClip.Create(name, count / channels, channels, sampleRate, false);
				if (clip == null) return null;
				clip.SetData(samples, 0);
				clip.hideFlags = HideFlags.HideAndDontSave;
				return clip;
			}
			catch (Exception e)
			{
				Killiorim.Logger.LogWarning($"[WavAudio] decode failed: {e.Message}");
				return null;
			}
		}
	}
}
