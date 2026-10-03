using System;
using System.Text.RegularExpressions;

namespace Killiorim.Core
{
	// GO TO A WORLD OR AN INSTANCE BY ID.
	//
	// Uses VRChat's OWN documented entry point, VRC.SDKBase.Networking.GoToRoom — the same call a
	// world's Udon portal makes. That matters: it goes through the game's normal room-transition flow
	// (leave, fetch, download, join) instead of poking at internals, so it behaves exactly like
	// clicking a world in the menu and it survives VRChat updates the way the SDK does.
	//
	// It is a NAVIGATION, not an access bypass. The server decides whether the instance will have you:
	// an invite-only or friends-only instance you were not invited to refuses this exactly as it
	// refuses a pasted link, and a full instance is still full. What this saves is the trip through a
	// browser to open a link you already have.
	public static class ForceJoin
	{
		/// <summary>What was last attempted, for the client's status line.</summary>
		public static string LastStatus = "";

		// Accepts anything a person is likely to have on their clipboard, because the alternative is a
		// feature that works only if you happened to paste the one shape it expected:
		//   wrld_xxx
		//   wrld_xxx:12345
		//   wrld_xxx:12345~region(eu)~private(usr_…)~nonce(…)
		//   https://vrchat.com/home/launch?worldId=wrld_xxx&instanceId=12345~region(eu)
		//   https://vrchat.com/home/world/wrld_xxx
		//   vrchat://launch?worldId=…&instanceId=…
		private static readonly Regex WorldRe = new Regex(@"wrld_[0-9a-fA-F-]{36}", RegexOptions.Compiled);

		/// <summary>Normalises any of the forms above into what GoToRoom wants, or "" if there is no
		/// world id in there at all.</summary>
		public static string Normalise(string raw)
		{
			try
			{
				string s = (raw ?? "").Trim();
				if (s.Length == 0) return "";

				Match w = WorldRe.Match(s);
				if (!w.Success) return "";
				string world = w.Value;

				// A launch URL keeps the instance in its own parameter; everything else keeps it after
				// a colon. Both end up as "world:instance".
				string instance = "";
				Match q = Regex.Match(s, @"[?&]instanceId=([^&\s]+)", RegexOptions.IgnoreCase);
				if (q.Success)
				{
					instance = Uri.UnescapeDataString(q.Groups[1].Value);
				}
				else
				{
					int colon = s.IndexOf(world, StringComparison.Ordinal) + world.Length;
					if (colon < s.Length && s[colon] == ':')
					{
						instance = s.Substring(colon + 1).Trim();
						// A trailing fragment or query on a pasted link is not part of the instance.
						int cut = instance.IndexOfAny(new[] { ' ', '&', '#', '"', '\'' });
						if (cut >= 0) instance = instance.Substring(0, cut);
					}
				}

				return instance.Length > 0 ? world + ":" + instance : world;
			}
			catch { return ""; }
		}

		/// <summary>Goes there. MUST be called on the main thread — GoToRoom starts a room transition.</summary>
		public static bool Go(string raw)
		{
			string room = Normalise(raw);
			if (room.Length == 0)
			{
				LastStatus = "no world id found in \"" + Trim(raw) + "\"";
				Killiorim.Logger.LogWarning("[ForceJoin] " + LastStatus);
				return false;
			}

			try
			{
				Killiorim.Logger.LogInfo("[ForceJoin] going to " + room);
				VRC.SDKBase.Networking.GoToRoom(room);
				// SAID, NOT PROVED. GoToRoom returns immediately and the transition happens over the
				// next seconds; whether the instance accepts us is the server's answer, not ours, and
				// it arrives long after this returns. Claiming success here would be a lie the moment
				// an invite-only instance refuses.
				LastStatus = "asked VRChat to join " + room;
				return true;
			}
			catch (Exception e)
			{
				LastStatus = "join failed: " + e.Message;
				Killiorim.Logger.LogWarning("[ForceJoin] " + LastStatus);
				return false;
			}
		}

		private static string Trim(string s) =>
			string.IsNullOrEmpty(s) ? "" : (s.Length <= 60 ? s : s.Substring(0, 59) + "…");
	}
}
