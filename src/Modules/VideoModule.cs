using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// PLAY YOUR OWN VIDEO — on YOUR screen, in a world's player.
	//
	// Finds the world's video players and points one at a URL of your choosing. Useful when a
	// world's queue is stuck, when the player is empty, or when you simply want to watch something
	// else in a world you like.
	//
	// LOCAL ONLY, and that is a deliberate line, not a limitation I failed to lift.
	//
	// Setting the URL and reloading on THIS client changes what YOU see. Pushing the same change
	// over the network changes what everyone in the instance sees and hears, on a screen they are
	// already watching — that is taking a shared thing away from the people using it, and a
	// "force it for everyone" button has no use that is not that. So this module writes to the
	// local behaviour and raises the reload locally; it never takes ownership and never sends.
	//
	// The URL goes through VRChat's OWN TryCreateAllowlistedVRCUrl, so the allowlist applies exactly
	// as it does for a world author. A URL the game would refuse is refused here too.
	public class VideoModule : IModule
	{
		public override string Name => "Video";

		public sealed class Player
		{
			public Behaviour B;
			public string Path;
			public string Short;
			public float Dist;
			public string UrlVar;     // the program variable that holds the URL
		}

		private static readonly List<Player> Found = new List<Player>();
		private static readonly object Gate = new object();

		public static string Status = "";
		public static string Url = "";
		public static int Count { get { lock (Gate) return Found.Count; } }
		public static List<Player> Snapshot() { lock (Gate) return new List<Player>(Found); }

		// The names world authors give the URL variable. UdonSharp keeps the field name, so these
		// cover the common video prefabs; anything else is reported so it can be added.
		private static readonly string[] UrlVarNames =
		{
			"url", "Url", "URL", "videoUrl", "VideoUrl", "mainUrl", "playlistUrl",
			"_url", "_videoUrl", "defaultUrl", "currentUrl",
		};

		// On demand only: this walks the world's Udon behaviours, which is not something to do on
		// a timer — the same rule the rest of the mod learned the hard way.
		public static void Rescan()
		{
			var list = new List<Player>();
			try
			{
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				if (ub == null) { Status = "Udon not available"; return; }

				Vector3 me = Vector3.zero;
				try { var t = PlayerRef.LocalTransform(); if (t != null) me = t.position; } catch { }

				var getVarType = ub.GetMethod("GetProgramVariableType", new[] { typeof(string) });
				var found = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.From(ub));
				if (found == null) { Status = "no Udon in this world"; return; }

				for (int i = 0; i < found.Length && list.Count < 40; i++)
				{
					try
					{
						var b = found[i]?.TryCast<Behaviour>();
						if (b == null) continue;
						var go = b.gameObject;
						if (go == null) continue;
						try { if (!go.scene.IsValid() || !go.scene.isLoaded) continue; } catch { continue; }

						// Which variable holds a URL? Ask the behaviour for the TYPE of each candidate
						// name — a video player is simply an Udon script with a VRCUrl variable, so
						// this identifies one without guessing from its object name.
						string hit = null;
						if (getVarType != null)
							foreach (string n in UrlVarNames)
							{
								try
								{
									var t = getVarType.Invoke(b, new object[] { n }) as Type;
									if (t != null && t.Name.IndexOf("VRCUrl", StringComparison.Ordinal) >= 0) { hit = n; break; }
								}
								catch { }
							}
						if (hit == null) continue;

						list.Add(new Player
						{
							B = b,
							Path = PathOf(b.transform),
							Short = b.transform.name ?? "?",
							Dist = Vector3.Distance(me, b.transform.position),
							UrlVar = hit,
						});
					}
					catch { }
				}

				list.Sort((a, c) => a.Dist.CompareTo(c.Dist));
				lock (Gate) { Found.Clear(); Found.AddRange(list); }
				Status = list.Count + " video player(s) in this world";
			}
			catch (Exception e) { Status = "scan failed: " + e.Message; }
		}

		// Point a player at a URL, ON THIS CLIENT.
		public static bool PlayLocal(Player p, string url)
		{
			try
			{
				if (p == null || p.B == null) { Status = "no player selected"; return false; }
				if (string.IsNullOrWhiteSpace(url)) { Status = "no URL"; return false; }

				object vrcUrl = MakeUrl(url);
				if (vrcUrl == null) return false;   // MakeUrl set the reason

				Type ub = FindType("VRC.Udon.UdonBehaviour");
				var setVar = ub?.GetMethod("SetProgramVariable", new[] { typeof(string), typeof(object) });
				if (setVar == null) { Status = "SetProgramVariable not available"; return false; }
				setVar.Invoke(p.B, new object[] { p.UrlVar, vrcUrl });

				// Ask the script to pick it up. SendCustomEvent runs the entry point HERE only —
				// the networked sibling is deliberately not used, see the class comment.
				var send = ub.GetMethod("SendCustomEvent", new[] { typeof(string) });
				string used = "(none)";
				if (send != null)
					foreach (string ev in new[] { "OnURLChanged", "PlayVideo", "_PlayVideo", "Play", "_Play", "LoadURL", "_LoadURL" })
					{
						try { send.Invoke(p.B, new object[] { ev }); used = ev; break; }
						catch { }
					}

				Status = "playing on your client only (" + Trunc(p.Short, 24) + ", via " + used + ")";
				VRChatArchiveModPlugin.Logger.LogInfo($"[Video] local URL set on {p.Path} ({p.UrlVar}), reload event {used}.");
				return true;
			}
			catch (Exception e)
			{
				Status = "could not set it: " + e.Message;
				return false;
			}
		}

		// VRChat's own allowlist check builds the object. A URL the game would refuse for a world
		// author is refused here too — this does not get to skip that.
		private static object MakeUrl(string url)
		{
			try
			{
				Type t = FindType("VRC.SDKBase.VRCUrl");
				if (t == null) { Status = "VRCUrl not available"; return null; }

				var mi = t.GetMethod("TryCreateAllowlistedVRCUrl",
					BindingFlags.Public | BindingFlags.Static);
				if (mi == null) { Status = "the allowlist check is missing — refusing to build a URL around it"; return null; }

				var args = new object[] { url, null };
				bool ok = false;
				try { ok = (bool)mi.Invoke(null, args); } catch (Exception e) { Status = "URL check threw: " + e.Message; return null; }
				if (!ok || args[1] == null)
				{
					Status = "VRChat does not allow that URL (its own allowlist refused it)";
					return null;
				}
				return args[1];
			}
			catch (Exception e) { Status = "URL failed: " + e.Message; return null; }
		}

		private static Type FindType(string full)
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type t = null;
				try { t = asm.GetType(full, false); } catch { }
				if (t != null) return t;
			}
			return null;
		}

		private static string PathOf(Transform t)
		{
			var sb = new System.Text.StringBuilder(96);
			try
			{
				var stack = new List<string>();
				for (Transform p = t; p != null; p = p.parent) stack.Add(p.name);
				for (int i = stack.Count - 1; i >= 0; i--) { sb.Append(stack[i]); if (i > 0) sb.Append('/'); }
			}
			catch { }
			return sb.ToString();
		}

		private static string Trunc(string s, int n)
			=> string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");
	}
}
