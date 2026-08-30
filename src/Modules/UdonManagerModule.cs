using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// UDON MANAGER — switch a world's scripts off, one at a time, on YOUR client.
	//
	// The mod already had the blunt version of this: "Anti-crasher" and PANIC, which block Udon
	// wholesale. PANIC works but takes the world with it — no doors, no video player, no game. This
	// is the same idea with a scalpel: find the one behaviour that is spamming, flashing or lagging
	// you, and turn off just that.
	//
	// LOCAL ONLY, and that is not a detail. Disabling a component stops it running in THIS process;
	// nothing is sent, and no other player's world changes. What it can do is desync you — a script
	// you switched off is a script that stops tracking the state everyone else still shares, so
	// doors, scores and games can drift until you turn it back on.
	//
	// Everything we switch off is remembered, so RESTORE puts back exactly what we touched and
	// never re-enables something the world had already disabled itself.
	public class UdonManagerModule : IModule
	{
		public override string Name => "UdonManager";

		public sealed class Entry
		{
			// THE HANDLE THE CLIENT HOLDS. The desktop app cannot keep a Behaviour reference across a
			// socket, so every row it draws is addressed by this number and nothing else. Unity gives
			// each object an instance id unique for its lifetime, and it survives a rescan — exactly
			// the lifetime a selection needs.
			public int Id;
			public Behaviour B;
			public string Path;
			public string Short;
			public float Dist;
			public bool OffByUs;
		}

		private static readonly List<Entry> Items = new List<Entry>();
		private static readonly object Gate = new object();
		private static readonly HashSet<int> OurOff = new HashSet<int>();

		public static string Status = "";
		public static int Count { get { lock (Gate) return Items.Count; } }
		public static int DisabledByUs { get { lock (Gate) return OurOff.Count; } }

		public static List<Entry> Snapshot() { lock (Gate) return new List<Entry>(Items); }

		// Address a row by instance id — the only way the desktop client can name one, since it
		// cannot hold a Behaviour. Null when the world changed underneath it, which is the honest
		// answer: the object it was looking at does not exist any more.
		public static Entry ById(int id)
		{
			lock (Gate)
				for (int i = 0; i < Items.Count; i++)
					if (Items[i] != null && Items[i].Id == id) return Items[i];
			return null;
		}

		// On demand only. This walks every loaded object, which is the single most expensive call
		// the mod can make — it is not something to run on a timer.
		public static void Rescan()
		{
			try
			{
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				if (ub == null) { Status = "UdonBehaviour type not found"; return; }

				Vector3 me = Vector3.zero;
				try { var t = PlayerRef.LocalTransform(); if (t != null) me = t.position; } catch { }

				var found = Resources.FindObjectsOfTypeAll(Il2CppType.From(ub));
				var list = new List<Entry>(found != null ? found.Length : 0);

				if (found != null)
					for (int i = 0; i < found.Length; i++)
					{
						try
						{
							var b = found[i]?.TryCast<Behaviour>();
							if (b == null) continue;

							var go = b.gameObject;
							if (go == null) continue;
							var tr = b.transform;
							if (tr == null) continue;

							// WORLD SCRIPTS ONLY. FindObjectsOfTypeAll returns everything in memory,
							// which is why the list was full of the wrong things:
							//   * PREFAB ASSETS — not instantiated, so their scene is invalid AND
							//     their .enabled is meaninglessly false. That is why every row read
							//     OFF. A real scene object has a LOADED scene, not just a valid one.
							//   * AVATAR objects — FollowHead, FollowHandL, Udon_BreathSystem: those
							//     hang under a player, not the world. Excluded by walking up to the
							//     root and rejecting anything parented under a VRCPlayer.
							if (b.hideFlags == HideFlags.HideAndDontSave) continue;
							Scene sc;
							try { sc = go.scene; } catch { continue; }
							if (!sc.IsValid() || !sc.isLoaded) continue;   // drops prefab assets
							if (IsUnderPlayer(tr)) continue;               // drops avatar objects

							string path = PathOf(tr);
							list.Add(new Entry
							{
								Id = b.GetInstanceID(),
								B = b,
								Path = path,
								Short = tr.name ?? "?",
								Dist = Vector3.Distance(me, tr.position),
								OffByUs = OurOff.Contains(b.GetInstanceID()),
							});
						}
						catch { }
					}

				// Nearest first: the script bothering you is almost always the one you are standing
				// next to, and a 1200-entry list sorted by nothing is unusable.
				list.Sort((x, y) => x.Dist.CompareTo(y.Dist));

				lock (Gate) { Items.Clear(); Items.AddRange(list); }
				Status = list.Count + " Udon behaviour(s) in this world";
				VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] {list.Count} behaviour(s) found.");
			}
			catch (Exception e)
			{
				Status = "scan failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[UdonManager] " + e.Message);
			}
		}

		public static void SetEnabled(Entry e, bool on)
		{
			try
			{
				if (e == null || e.B == null) return;
				e.B.enabled = on;
				int id = e.B.GetInstanceID();
				lock (Gate)
				{
					if (on) OurOff.Remove(id);
					else OurOff.Add(id);
				}
				e.OffByUs = !on;
				Status = (on ? "re-enabled " : "disabled ") + Trunc(e.Short, 40);
				VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] {(on ? "enabled" : "disabled")} {e.Path}");
			}
			catch (Exception ex) { Status = "could not change it: " + ex.Message; }
		}

		// Only what WE switched off. A world ships plenty of behaviours disabled on purpose, and
		// turning those on would be a different kind of breaking it.
		public static void RestoreAll()
		{
			int n = 0;
			try
			{
				List<Entry> all = Snapshot();
				foreach (var e in all)
				{
					if (e == null || e.B == null || !e.OffByUs) continue;
					try { e.B.enabled = true; e.OffByUs = false; n++; } catch { }
				}
				lock (Gate) OurOff.Clear();
			}
			catch { }
			Status = n > 0 ? ("restored " + n + " behaviour(s)") : "nothing of ours was off";
			VRChatArchiveModPlugin.Logger.LogInfo("[UdonManager] " + Status);
		}

		// Bulk switch for everything currently listed by the filter — the case where one prefab is
		// instantiated two hundred times and turning them off one by one is not a real option.
		public static void SetMany(List<Entry> rows, bool on)
		{
			int n = 0;
			foreach (var e in rows)
			{
				if (e == null || e.B == null) continue;
				try { SetEnabled(e, on); n++; } catch { }
			}
			Status = (on ? "re-enabled " : "disabled ") + n + " behaviour(s)";
		}

		// A world change destroys them all; our record of what we switched off means nothing after
		// that, and keeping it would make RESTORE claim to fix things that no longer exist.
		public override void OnSceneLoaded(int buildIndex)
		{
			lock (Gate) { Items.Clear(); OurOff.Clear(); }
			Status = "";
		}

		// ------------------------------------------------------------------ entry points

		// The events a behaviour exposes. Read once per selected row, never during the scan — this
		// costs a reflected call per behaviour and a world can hold a thousand of them.
		public static List<string> EntryPoints(Entry e)
		{
			var outp = new List<string>();
			try
			{
				if (e == null || e.B == null) return outp;
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				var mi = ub?.GetMethod("GetPrograms");
				object arr = mi?.Invoke(e.B, null);
				if (arr == null) return outp;

				// ImmutableArray<string>: enumerate it properly. Calling ToString() here is what
				// made every earlier dump print the type name instead of the events.
				var at = arr.GetType();
				var lenP = at.GetProperty("Length") ?? at.GetProperty("Count");
				var item = at.GetProperty("Item");
				if (!(lenP?.GetValue(arr) is int len) || item == null) return outp;
				for (int i = 0; i < len && i < 120; i++)
				{
					try
					{
						string n = item.GetValue(arr, new object[] { i }) as string;
						if (!string.IsNullOrEmpty(n)) outp.Add(n);
					}
					catch { }
				}
			}
			catch { }
			return outp;
		}

		// GLOBAL or LOCAL — VRChat's own network boundary. VRChat's SendCustomNetworkEvent REFUSES
		// any entry point whose name starts with '_': the Udon/Unity lifecycle hooks (_start,
		// _update, _interact, _onPickup…) and every author event they chose to prefix can ONLY ever
		// run on the client that owns them. Everything else is network-ELIGIBLE, so we badge it
		// GLOBAL.
		//
		// "Eligible" is the honest word. A non-'_' event CAN be broadcast, but whether a given world
		// actually broadcasts it is a property of that world's CODE, not of the event name — a world
		// is free to define "ObjectOrbit" and only ever fire it locally, so your friends never see
		// it. No client-side check can tell that apart from a truly global event without decompiling
		// the world's Udon program. So GLOBAL = "VRChat would let this be sent to everyone", LOCAL =
		// "VRChat guarantees it never can be".
		//
		// The old GetNetworkCallingMetadata(name) probe returned non-null for EVERY non-'_' event —
		// it measured this exact boundary through a fragile reflected native call. The name test is
		// the same answer for free, and never wrong about the LOCAL set.
		public static bool IsGlobalEvent(Entry e, string ev)
			=> !string.IsNullOrEmpty(ev) && ev[0] != '_';

		// RUNS THE EVENT ON THIS CLIENT ONLY.
		//
		// SendCustomEvent executes the entry point in our own process. Nothing is transmitted, no
		// other player's world moves, and the world's networked state stays whatever the owner says
		// it is. That is what makes it a debugging tool: you can see what a script does from your
		// side and undo it by leaving the instance.
		//
		// There is a sibling call, SendCustomNetworkEvent, which broadcasts the same event to
		// everyone. It is deliberately NOT wired here and should not be added: it is the same
		// function whether you are inspecting a door or ending someone else's game, and the
		// difference is not something the mod can judge.
		public static bool RunLocal(Entry e, string ev)
		{
			try
			{
				if (e == null || e.B == null || string.IsNullOrEmpty(ev)) return false;
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				var mi = ub?.GetMethod("SendCustomEvent", new[] { typeof(string) });
				if (mi == null) { Status = "SendCustomEvent not found"; return false; }
				mi.Invoke(e.B, new object[] { ev });
				Status = "ran " + Trunc(ev, 32) + " locally";
				VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] local SendCustomEvent '{ev}' on {e.Path}");
				return true;
			}
			catch (Exception ex)
			{
				Status = "event failed: " + ex.Message;
				return false;
			}
		}

		// RUNS THE EVENT FOR EVERYONE. SendCustomNetworkEvent broadcasts the entry point to every
		// client in the instance over VRChat's own networking — the same path the world's own
		// scripts use. VRChat refuses this for '_'-prefixed events (see IsGlobalEvent), so it is
		// only ever OFFERED for network-eligible ones, and the menu colours the button apart from
		// RUN so the "affects the whole instance" one is never a slip of the mouse. Use it to drive
		// a world's shared state — reset a game, open a door for the room — not to disrupt it.
		private static MethodInfo _sendNet;
		private static Type _netTarget;
		private static bool _netResolved;

		public static bool RunGlobal(Entry e, string ev)
		{
			try
			{
				if (e == null || e.B == null || string.IsNullOrEmpty(ev)) return false;
				if (ev[0] == '_') { Status = "'" + Trunc(ev, 22) + "' is local-only — VRChat blocks networking it"; return false; }

				if (!_netResolved)
				{
					_netResolved = true;
					Type ub = FindType("VRC.Udon.UdonBehaviour");
					_netTarget = FindType("VRC.Udon.Common.Interfaces.NetworkEventTarget");
					if (ub != null && _netTarget != null)
						foreach (var m in ub.GetMethods(BindingFlags.Public | BindingFlags.Instance))
						{
							if (m.Name != "SendCustomNetworkEvent") continue;
							var ps = m.GetParameters();
							if (ps.Length == 2 && ps[1].ParameterType == typeof(string)) { _sendNet = m; break; }
						}
				}
				if (_sendNet == null || _netTarget == null) { Status = "network event API not found"; return false; }

				object all;
				try { all = Enum.Parse(_netTarget, "All"); }
				catch { all = Enum.ToObject(_netTarget, 0); }   // All is the first value of the enum

				_sendNet.Invoke(e.B, new object[] { all, ev });
				Status = "sent " + Trunc(ev, 26) + " to EVERYONE";
				VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] GLOBAL SendCustomNetworkEvent '{ev}' on {e.Path}");
				return true;
			}
			catch (Exception ex)
			{
				Status = "global event failed: " + ex.Message;
				return false;
			}
		}

		// WHAT DID THE EVENT DO? Snapshot the behaviour's variables, run the entry point LOCALLY,
		// snapshot again, and report what moved. This is the empirical answer the mod can give
		// without decompiling the world: fire "_OpenDoor" and watch "doorOpen" flip false->true.
		// LOCAL only (SendCustomEvent): a networked run's effects land on OTHER clients, so there
		// would be nothing here to diff. SYNCHRONOUS only — a variable a coroutine sets a frame later
		// is not caught, and the diff honestly says "no change" rather than guessing.
		public sealed class VarChange { public string Name; public string Before; public string After; }

		public static List<VarChange> RunAndDiff(Entry e, string ev)
		{
			var changes = new List<VarChange>();
			if (e == null || e.B == null || string.IsNullOrEmpty(ev)) return changes;
			var before = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var v in Variables(e)) before[v.Name] = v.Value;
			RunLocal(e, ev);
			foreach (var v in Variables(e))
			{
				string b;
				bool had = before.TryGetValue(v.Name, out b);
				if (!had || !string.Equals(b, v.Value, StringComparison.Ordinal))
					changes.Add(new VarChange { Name = v.Name, Before = had ? b : "(new)", After = v.Value });
			}
			Status = changes.Count == 0
				? "ran " + Trunc(ev, 24) + " locally — no variable changed"
				: "ran " + Trunc(ev, 24) + " — " + changes.Count + " variable(s) changed";
			return changes;
		}

		// ------------------------------------------------------------------ variables
		//
		// THE STATE, beside the events. Switching a script off tells you what it stops doing; its
		// variables tell you what it THINKS — which door is open, whose turn it is, how many points are
		// on the board. That is the difference between silencing a world and understanding it.
		//
		// PUBLIC VARIABLES ONLY. They are the set a world author chose to expose, they are the set
		// VRChat keeps synced, and they are the one table reachable without walking the compiled
		// program. A private symbol is left out rather than guessed at.
		public sealed class Var
		{
			public string Name;
			public string Type;      // the declared type's short name, "?" when it will not say
			public string Value;     // a SAFE rendering — see Render; never a call into a live object
			public bool Editable;    // text can become this type, AND a setter was actually resolved
		}

		public static List<Var> Variables(Entry e)
		{
			var outp = new List<Var>();
			try
			{
				if (e == null || e.B == null) return outp;
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				if (ub == null) return outp;

				object table = ub.GetProperty("publicVariables")?.GetValue(e.B);
				if (table == null) return outp;
				object symbols = table.GetType().GetProperty("VariableSymbols")?.GetValue(table);
				if (symbols == null) return outp;

				var getVar = ub.GetMethod("GetProgramVariable", new[] { typeof(string) });
				var getType = ub.GetMethod("GetProgramVariableType", new[] { typeof(string) });
				ResolveSetter(ub);

				foreach (string sym in Strings(symbols))
				{
					if (string.IsNullOrEmpty(sym)) continue;
					if (outp.Count >= 200) break;   // a table this long is a generated one, not a read

					string tn = "?";
					try { tn = TypeNameOf(getType?.Invoke(e.B, new object[] { sym })); } catch { }

					object val = null;
					try { val = getVar?.Invoke(e.B, new object[] { sym }); } catch { }

					outp.Add(new Var
					{
						Name = sym,
						Type = tn,
						Value = Render(val),
						Editable = CanWrite && Parsable(tn),
					});
				}
			}
			catch (Exception ex) { Status = "could not read its variables: " + ex.Message; }
			return outp;
		}

		// WRITES LOCALLY, like everything else on this page. A synced variable belongs to whoever owns
		// the object: if that is not you, the owner's next sync tick overwrites what you wrote and the
		// value snaps back. That is VRChat working correctly rather than the write failing — and taking
		// ownership to force it through is a networked act this does not perform.
		public static bool SetVariable(Entry e, string name, string text)
		{
			try
			{
				if (e == null || e.B == null || string.IsNullOrEmpty(name)) return false;
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				if (ub == null) return false;
				ResolveSetter(ub);
				if (!CanWrite) { Status = "this build exposes no way to write variables"; return false; }

				string tn = "?";
				try
				{
					var gt = ub.GetMethod("GetProgramVariableType", new[] { typeof(string) });
					tn = TypeNameOf(gt?.Invoke(e.B, new object[] { name }));
				}
				catch { }

				object boxed = ParseValue(tn, text, out Type managed);
				if (boxed == null) { Status = "cannot turn that into a " + tn; return false; }

				if (_setGeneric != null)
					_setGeneric.MakeGenericMethod(managed).Invoke(e.B, new object[] { name, boxed });
				else if (_setPlain != null && _setPlainParam != null && _setPlainParam.IsInstanceOfType(boxed))
					_setPlain.Invoke(e.B, new object[] { name, boxed });
				else { Status = "this build will not take a " + tn + " from here"; return false; }

				Status = "set " + Trunc(name, 28) + " = " + Trunc(text, 24);
				VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] set {name} = {text} on {e.Path}");
				return true;
			}
			catch (Exception ex) { Status = "write failed: " + ex.Message; return false; }
		}

		// WRITING IS RESOLVED, NOT ASSUMED. VRChat has shipped more than one shape of
		// SetProgramVariable, and Il2CppInterop does not always generate the overload a strict
		// GetMethod(name, types) asks for — the video-URL code already carries a fallback for exactly
		// that. So the setter is found by scanning, once, and what is found decides whether a SET
		// button is offered at all. No button beats a button that silently does nothing.
		private static bool _setterResolved;
		private static MethodInfo _setGeneric;    // SetProgramVariable<T>(string, T)
		private static MethodInfo _setPlain;      // SetProgramVariable(string, <something>)
		private static Type _setPlainParam;

		public static bool CanWrite => _setGeneric != null || _setPlain != null;

		private static void ResolveSetter(Type ub)
		{
			if (_setterResolved) return;
			_setterResolved = true;
			try
			{
				foreach (var m in ub.GetMethods(BindingFlags.Public | BindingFlags.Instance))
				{
					if (m.Name != "SetProgramVariable") continue;
					var ps = m.GetParameters();
					if (ps.Length != 2 || ps[0].ParameterType != typeof(string)) continue;
					// The generic overload is preferred: it lets the interop layer do the marshalling
					// rather than us hand-boxing a value into an il2cpp object, which is the step that
					// takes the process down when it is wrong.
					if (m.IsGenericMethodDefinition) { if (_setGeneric == null) _setGeneric = m; }
					else if (_setPlain == null) { _setPlain = m; _setPlainParam = ps[1].ParameterType; }
				}
			}
			catch { }
		}

		// The types a text box can honestly produce. Everything else is shown and not offered for
		// editing — a world's variable can be a Transform, a material or a whole script, and none of
		// those come out of a line of text.
		private static bool Parsable(string typeName)
		{
			switch (typeName)
			{
				case "Boolean": case "String":
				case "Int32": case "UInt32": case "Int64": case "UInt64":
				case "Int16": case "UInt16": case "Byte": case "SByte":
				case "Single": case "Double":
					return true;
				default: return false;
			}
		}

		private static object ParseValue(string typeName, string text, out Type managed)
		{
			managed = null;
			text = text ?? "";
			var inv = CultureInfo.InvariantCulture;
			try
			{
				switch (typeName)
				{
					case "String":  managed = typeof(string); return text;
					case "Boolean": managed = typeof(bool);
						return text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Trim() == "1";
					case "Int32":   managed = typeof(int);    return int.Parse(text, inv);
					case "UInt32":  managed = typeof(uint);   return uint.Parse(text, inv);
					case "Int64":   managed = typeof(long);   return long.Parse(text, inv);
					case "UInt64":  managed = typeof(ulong);  return ulong.Parse(text, inv);
					case "Int16":   managed = typeof(short);  return short.Parse(text, inv);
					case "UInt16":  managed = typeof(ushort); return ushort.Parse(text, inv);
					case "Byte":    managed = typeof(byte);   return byte.Parse(text, inv);
					case "SByte":   managed = typeof(sbyte);  return sbyte.Parse(text, inv);
					case "Single":  managed = typeof(float);  return float.Parse(text, NumberStyles.Float, inv);
					case "Double":  managed = typeof(double); return double.Parse(text, NumberStyles.Float, inv);
				}
			}
			catch { }
			managed = null;
			return null;
		}

		// GetProgramVariableType hands back a managed Type on some builds and an il2cpp Type proxy on
		// others, and the difference is not visible from here. Both answer to Name.
		private static string TypeNameOf(object t)
		{
			if (t == null) return "?";
			if (t is Type mt) return mt.Name ?? "?";
			try
			{
				string s = t.GetType().GetProperty("Name")?.GetValue(t) as string;
				if (!string.IsNullOrEmpty(s)) return s;
			}
			catch { }
			return "?";
		}

		// THE VALUE, OR THE TYPE — never a gamble. A managed primitive came back as itself and is
		// safe to print. Anything else is an il2cpp object, and calling ToString() on one whose native
		// object is no longer live is an access violation that no try/catch here can survive. So for
		// those we print what it IS and stop, which is the useful half anyway.
		private static string Render(object val)
		{
			if (val == null) return "(null)";
			try
			{
				if (val is string s) return s.Length > 120 ? s.Substring(0, 119) + "…" : s;
				if (val is bool || val is int || val is uint || val is long || val is ulong
				 || val is short || val is ushort || val is byte || val is sbyte
				 || val is float || val is double)
					return Convert.ToString(val, CultureInfo.InvariantCulture) ?? "?";
			}
			catch { }
			try
			{
				var o = val as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				if (o != null && Core.NativeGuard.Alive(o))
				{
					string n = MenuCard.Il2CppNameOf(o);
					if (!string.IsNullOrEmpty(n)) return "<" + n + ">";
				}
				if (o != null) return "<dead reference>";
			}
			catch { }
			try { return "<" + val.GetType().Name + ">"; } catch { return "<?>"; }
		}

		// VariableSymbols is an ImmutableArray on some builds and a plain enumerable on others. Both
		// shapes are read here rather than assuming one — the same trap that made every early events
		// dump print a type name instead of the events.
		private static IEnumerable<string> Strings(object seq)
		{
			var outp = new List<string>();
			try
			{
				if (seq is System.Collections.IEnumerable en)
				{
					foreach (object o in en)
					{
						string s = o as string ?? o?.ToString();
						if (!string.IsNullOrEmpty(s)) outp.Add(s);
						if (outp.Count >= 400) break;
					}
					if (outp.Count > 0) return outp;
				}

				var t = seq.GetType();
				var lenP = t.GetProperty("Length") ?? t.GetProperty("Count");
				var item = t.GetProperty("Item");
				if (lenP?.GetValue(seq) is int len && item != null)
					for (int i = 0; i < len && i < 400; i++)
					{
						try
						{
							string s = item.GetValue(seq, new object[] { i }) as string;
							if (!string.IsNullOrEmpty(s)) outp.Add(s);
						}
						catch { }
					}
			}
			catch { }
			return outp;
		}

		// ------------------------------------------------------------------ helpers

		// True when this transform hangs under a player rather than the world — an avatar's own
		// Udon (FollowHead, breath systems, pickups) has nothing to do with the world script the
		// user is trying to manage.
		private static readonly string[] PlayerMarkers =
		{
			"VRCPlayer", "Player[Local]", "Player[Remote]", "AvatarRoot", "SelectRegion",
		};

		private static bool IsUnderPlayer(Transform t)
		{
			try
			{
				for (Transform p = t; p != null; p = p.parent)
				{
					string n = p.name ?? "";
					for (int i = 0; i < PlayerMarkers.Length; i++)
						if (n.IndexOf(PlayerMarkers[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
				}
			}
			catch { }
			return false;
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
			var sb = new StringBuilder(96);
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
