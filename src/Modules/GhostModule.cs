using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// GHOST — a TOGGLE that stops YOUR OWN player from being serialized to the network.
	//
	// It does exactly what the owner did by hand in UnityExplorer: on the local player object
	// ("VRCPlayer[Local] …") the component VRC.Networking.FlatBufferNetworkSerializer is the thing that
	// packs your position / rotation / IK into the outbound stream. With it disabled nothing about your
	// body leaves the client: everyone else sees you standing exactly where you were when you switched
	// it on, while you keep moving normally on your side. Turning it off re-enables the component and
	// the next serialization catches everybody up.
	//
	// Scope: your own object only. No other client, no world state, no extra traffic (less, in fact).
	// A world change rebuilds the player object, so the toggle drops to OFF there instead of chasing a
	// component that no longer exists.
	public class GhostModule : IModule
	{
		public override string Name => "Ghost";

		/// <summary>True while the local player's network serializer is held disabled (read by the sync for the client's button).</summary>
		public static bool Active { get; private set; }
		public static string Status = "";

		private const string SerializerName = "FlatBufferNetworkSerializer";
		private static Behaviour _serializer;     // the component we hold off; re-resolved when the player object changes
		private static float _nextCheck;

		public override void OnUpdate()
		{
			try
			{
				if (!Active) return;
				// Once a second, make sure it is still ours to hold: VRChat may re-enable the component
				// (avatar switch, respawn) and a re-created player object needs a fresh reference.
				float now = Time.realtimeSinceStartup;
				if (now < _nextCheck) return;
				_nextCheck = now + 1f;
				var s = Resolve();
				if (s == null) { Active = false; Status = "ghost off — your player object changed"; return; }
				if (s.enabled) s.enabled = false;
			}
			catch (Exception e) { Status = "ghost: " + e.Message; }
		}

		public static void Toggle()
		{
			try
			{
				var s = Resolve();
				if (s == null)
				{
					Active = false;
					Status = "ghost: your player's network serializer was not found (not in a world yet?)";
					VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] " + SerializerName + " not found on the local player.");
					return;
				}
				if (Active)
				{
					s.enabled = true;
					Active = false;
					Status = "ghost OFF — others see you move again";
				}
				else
				{
					s.enabled = false;
					Active = true;
					_nextCheck = Time.realtimeSinceStartup + 1f;
					Status = "ghost ON — you are frozen for everyone else, you still move for yourself";
				}
				VaTagsModule.LastStatus = Status;
				VRChatArchiveModPlugin.Logger.LogInfo("[Ghost] " + Status);
			}
			catch (Exception e) { Status = "ghost: " + e.Message; VaTagsModule.LastStatus = Status; }
		}

		// The serializer on the local player's root object ("VRCPlayer[Local] …"), by il2cpp type name —
		// the class is not referenced by the mod, so the name keeps this build-independent. Three
		// name sources are tried per component (native class name, interop type, managed type), the
		// root's own components first, then the whole player hierarchy; when nothing matches, the
		// names actually found are logged once so the next report says what the build calls it.
		private static float _nextDiag;
		private static Behaviour Resolve()
		{
			try
			{
				if (_serializer != null && Core.NativeGuard.Alive(_serializer)) return _serializer;
				_serializer = null;
				var root = PlayerRef.LocalPlayer();
				GameObject go = null;
				try { go = root != null && Core.NativeGuard.Alive(root) ? root.gameObject : null; } catch { go = null; }
				if (go == null)
				{
					var t = PlayerRef.LocalTransform();
					try { go = t != null && Core.NativeGuard.Alive(t) ? t.gameObject : null; } catch { go = null; }
				}
				if (go == null || !Core.NativeGuard.Alive(go)) return null;

				// FIRST BY TYPE. On this build the il2cpp class names on the player are obfuscated (the
				// diagnostic below printed garbage for 16 of 19 components), so a name comparison finds
				// nothing. The interop assemblies carry the de-obfuscated type — resolve it once and ask
				// Unity for that component directly.
				Behaviour found = null;
				var il2 = SerializerType();
				if (il2 != null)
				{
					Component comp = null;
					try { comp = go.GetComponent(il2); } catch { comp = null; }
					if (comp == null) { try { comp = go.GetComponentInChildren(il2, true); } catch { comp = null; } }
					if (comp != null && Core.NativeGuard.Alive(comp))
					{
						found = comp.TryCast<Behaviour>();
						if (found == null) { try { found = new Behaviour(comp.Pointer); } catch { found = null; } }
					}
				}
				if (found == null) found = Find(go.GetComponents<Component>());
				if (found == null) found = Find(go.GetComponentsInChildren<Component>(true));
				if (found == null)
				{
					float now = Time.realtimeSinceStartup;
					if (now >= _nextDiag)
					{
						_nextDiag = now + 10f;
						var names = new System.Text.StringBuilder();
						try
						{
							var comps = go.GetComponents<Component>();
							int k = 0;
							foreach (var c in comps) { if (c == null) continue; if (k++ > 0) names.Append(", "); names.Append(NameOf(c)); if (k > 40) break; }
						}
						catch { }
						VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] no component named *" + SerializerName + "* on '" + go.name + "'. Components there: " + names);
					}
					return null;
				}
				_serializer = found;
				return found;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] resolve: " + e.Message); }
			return null;
		}

		// The de-obfuscated managed type from the interop assemblies (VRC.Networking.FlatBufferNetworkSerializer),
		// turned into the il2cpp Type GetComponent understands. Looked up once per session; a miss is logged
		// with the reason so the next report names the assembly it lives in.
		private static Il2CppSystem.Type _il2Type;
		private static bool _typeTried;
		private static Il2CppSystem.Type SerializerType()
		{
			if (_typeTried) return _il2Type;
			_typeTried = true;
			try
			{
				System.Type t = null;
				var asms = AppDomain.CurrentDomain.GetAssemblies();
				// cheap first: the fully qualified name in every loaded assembly
				foreach (var asm in asms)
				{
					try { t = asm.GetType("VRC.Networking." + SerializerName, false); } catch { t = null; }
					if (t != null) break;
				}
				if (t == null)
				{
					try { t = System.Reflection.Assembly.Load("Assembly-CSharp").GetType("VRC.Networking." + SerializerName, false); } catch { t = null; }
				}
				if (t == null)
				{
					// slow path, once: any type of that simple name
					foreach (var asm in asms)
					{
						System.Type[] types;
						try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; } catch { continue; }
						if (types == null) continue;
						foreach (var ty in types) if (ty != null && ty.Name == SerializerName) { t = ty; break; }
						if (t != null) break;
					}
				}
				if (t == null)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] no managed type named " + SerializerName + " in the loaded interop assemblies.");
					return null;
				}
				_il2Type = Il2CppInterop.Runtime.Il2CppType.From(t);
				VRChatArchiveModPlugin.Logger.LogInfo("[Ghost] serializer type resolved: " + t.FullName + " (" + t.Assembly.GetName().Name + ")");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[Ghost] type lookup: " + e.Message); }
			return _il2Type;
		}

		private static Behaviour Find(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Component> comps)
		{
			if (comps == null) return null;
			foreach (var c in comps)
			{
				try
				{
					if (c == null || !Core.NativeGuard.Alive(c)) continue;
					if (NameOf(c).IndexOf(SerializerName, StringComparison.OrdinalIgnoreCase) < 0) continue;
					var b = c.TryCast<Behaviour>();
					// A MonoBehaviour the interop cannot cast (class not in the generated assemblies) still IS
					// a Behaviour natively: wrap the same pointer.
					if (b == null) { try { b = new Behaviour(c.Pointer); } catch { b = null; } }
					if (b != null) return b;
				}
				catch { }
			}
			return null;
		}

		private static string NameOf(Component c)
		{
			string n = "";
			try { n = Core.MenuCard.Il2CppNameOf(c) ?? ""; } catch { }
			if (n.Length == 0 || n == "?")
			{
				try { n = c.GetIl2CppType().FullName ?? ""; } catch { }
			}
			if (n.Length == 0) { try { n = c.GetType().Name; } catch { } }
			return n ?? "";
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			// The player object is rebuilt with the world: the old component is gone, and a new world
			// starts un-ghosted so nobody is surprised by a frozen you on arrival.
			_serializer = null;
			if (Active) { Active = false; Status = "ghost off (world changed)"; }
		}

		public override void OnShutdown()
		{
			try { var s = Resolve(); if (s != null) s.enabled = true; } catch { }
			Active = false;
		}
	}
}
