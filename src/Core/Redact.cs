using System;
using System.Text.RegularExpressions;

namespace VRChatArchiveMod.Core
{
	// Strips secrets out of anything the mod writes to a file a user will hand to somebody else.
	//
	// This exists because of a real leak, not a hypothetical one: VRChat prints its own command line
	// at startup, the loader is launched with the user's KEY on that command line, and the mod's log
	// capture wrote the lot verbatim. The resulting file is exactly the file people paste into a
	// chat when asking for help — so the key travelled with it.
	//
	// Applied to the full game log AND the diagnostics report. Cheap enough for the log path: the
	// patterns only run when the line actually contains one of the trigger words.
	public static class Redact
	{
		private const string Mask = "[redacted]";

		// key=..., password=..., token=..., main=..., -eac_key=..., with or without dashes/quotes.
		private static readonly Regex Args = new Regex(
			@"(?i)\b(-{0,2}(?:main|key|password|passwd|pwd|token|secret|auth|session|licen[cs]e)\s*[=:]\s*)(""?)([^\s""]+)",
			RegexOptions.Compiled);

		// Long base64-ish blobs that show up bare, e.g. a pasted key with no name in front of it.
		private static readonly Regex Blob = new Regex(@"\b[A-Za-z0-9+/]{28,}={0,2}\b", RegexOptions.Compiled);

		public static string Line(string s)
		{
			if (string.IsNullOrEmpty(s)) return s;
			try
			{
				// Fast path: most log lines carry none of the trigger words, and a regex per line on
				// a stream this loud is not free.
				if (Looks(s)) s = Args.Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
				return s;
			}
			catch { return s; }
		}

		// The report is small and written once, so it can afford the stricter pass.
		public static string Block(string s)
		{
			if (string.IsNullOrEmpty(s)) return s;
			try
			{
				s = Args.Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
				s = Blob.Replace(s, Mask);
				return s;
			}
			catch { return s; }
		}

		private static bool Looks(string s)
		{
			return s.IndexOf("key", StringComparison.OrdinalIgnoreCase) >= 0
				|| s.IndexOf("pass", StringComparison.OrdinalIgnoreCase) >= 0
				|| s.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0
				|| s.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0
				|| s.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0
				|| s.IndexOf("main=", StringComparison.OrdinalIgnoreCase) >= 0
				|| s.IndexOf("licen", StringComparison.OrdinalIgnoreCase) >= 0;
		}
	}
}
