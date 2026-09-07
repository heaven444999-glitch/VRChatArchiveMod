using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// FLOAT OBJECTS — a TOGGLE that removes gravity from every pickup in the world.
	//
	// Per object, the way the owner showed it done elsewhere: Rigidbody.useGravity = false on each
	// VRC_Pickup's body — NOT Physics.gravity = 0, which also stops lifts, doors and physics puzzles for
	// you. Objects you own float for everyone (their positions are synced), the rest only on your
	// screen. Re-swept every 2 s so pickups that appear later float too; OFF puts every body's own
	// useGravity back and lets them fall.
	public class ObjectGravityModule : IModule
	{
		public override string Name => "ObjectGravity";

		public static bool Active { get; private set; }
		public static int Count => _bodies.Count;
		public static string Status = "";

		// Id kept with the body: dropping a body from the list has to drop its id too, or a pickup that
		// only LOOKED dead for one step (a proxy the runtime recycled, a body re-enabled by the world)
		// is barred from _ids for ever and silently stops floating with no way back but a re-toggle.
		private sealed class Held { public Rigidbody Body; public bool Was; public int Id; }
		private static readonly List<Held> _bodies = new List<Held>();
		private static readonly HashSet<int> _ids = new HashSet<int>();
		private static float _nextSweep;
		private static Il2CppSystem.Type _pickupIl2;

		// RE-ASSERT EVERY PHYSICS STEP, NOT EVERY TWO SECONDS.
		//
		// VRC_Pickup turns gravity back ON at the moment you DROP an object — it restores the body's
		// original useGravity as part of releasing it. With the re-assert living on the 2 s discovery
		// timer, an object you threw kept its gravity for up to two seconds: it fell, hit the floor,
		// and only then started floating. The visible bug was "it only floats after it lands".
		//
		// This loop is a list walk with a bool compare, nothing like the object query it used to be
		// bundled with, so it costs nothing per step. FixedUpdate and not Update: useGravity is read
		// by the physics step, and FixedUpdate runs immediately before it, so the very next step
		// already honours it and the throw keeps its velocity.
		public override void OnFixedUpdate()
		{
			if (!Active) return;
			try { Reassert(); }
			catch (Exception e) { Status = "float objects: " + e.Message; }
		}

		public override void OnUpdate()
		{
			try
			{
				if (!Active) return;
				float now = Time.realtimeSinceStartup;
				if (now < _nextSweep) return;
				// Only DISCOVERY is rate-limited: FindObjectsOfType walks the whole object table.
				_nextSweep = now + 2f;
				Discover();
			}
			catch (Exception e) { Status = "float objects: " + e.Message; }
		}

		public static void Toggle()
		{
			if (Active) { RestoreAll(); Active = false; Status = "object gravity back"; }
			else { Active = true; _nextSweep = 0f; Discover(); Status = "object gravity removed — " + _bodies.Count + " pickup(s) floating"; }
			VaTagsModule.LastStatus = Status;
			VRChatArchiveModPlugin.Logger.LogInfo("[ObjectGravity] " + Status);
		}

		// Drop dead bodies and re-assert the ones we hold: VRC_Pickup restores gravity on release, and
		// world scripts switch it back on too.
		private static void Reassert()
		{
			for (int i = _bodies.Count - 1; i >= 0; i--)
			{
				var h = _bodies[i];
				try
				{
					if (h.Body == null || !NativeGuard.Alive(h.Body)) { Forget(i); continue; }
					if (h.Body.useGravity) h.Body.useGravity = false;
				}
				catch { Forget(i); }
			}
		}

		// Removes entry i from both the list and the id set, so the next discovery may take it again.
		private static void Forget(int i)
		{
			try { _ids.Remove(_bodies[i].Id); } catch { }
			_bodies.RemoveAt(i);
		}

		private static void Discover()
		{
			Reassert();
			try
			{
				// NON-GENERIC: FindObjectsOfType<VRC_Pickup>() returns nothing on this build.
				if (_pickupIl2 == null) _pickupIl2 = Il2CppInterop.Runtime.Il2CppType.Of<VRC.SDKBase.VRC_Pickup>();
				var found = UnityEngine.Object.FindObjectsOfType(_pickupIl2);
				if (found == null) return;
				int added = 0;
				for (int i = 0; i < found.Length; i++)
				{
					var p = found[i] != null ? found[i].TryCast<VRC.SDKBase.VRC_Pickup>() : null;
					if (p == null || !NativeGuard.Alive(p)) continue;
					Rigidbody rb = null;
					try { rb = p.GetComponent<Rigidbody>() ?? p.GetComponentInChildren<Rigidbody>(); } catch { }
					if (rb == null || !NativeGuard.Alive(rb)) continue;
					int id; try { id = rb.GetInstanceID(); } catch { continue; }
					if (!_ids.Add(id)) continue;
					var h = new Held { Body = rb, Was = rb.useGravity, Id = id };
					if (rb.useGravity) rb.useGravity = false;
					_bodies.Add(h); added++;
				}
				if (added > 0) Status = "object gravity removed — " + _bodies.Count + " pickup(s) floating";
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ObjectGravity] sweep: " + e.Message); }
		}

		private static void RestoreAll()
		{
			for (int i = 0; i < _bodies.Count; i++)
			{
				var h = _bodies[i];
				try { if (h.Body != null && NativeGuard.Alive(h.Body)) h.Body.useGravity = h.Was; } catch { }
			}
			_bodies.Clear(); _ids.Clear();
		}

		public override void OnSceneLoaded(int buildIndex) { _bodies.Clear(); _ids.Clear(); Active = false; }   // the objects left with the world
		public override void OnShutdown() { RestoreAll(); Active = false; }
	}
}
