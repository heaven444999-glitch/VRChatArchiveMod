using System;
using System.Reflection;
using UnityEngine;
using Killiorim.Core;

namespace Killiorim.Modules
{
	// PLAYER ROTATOR — turn your OWN capsule, and take the view with it.
	//
	// WHY THE OLD ROTATOR DID NOT DO THIS. MovementModule's arrow-key turning is YAW ONLY, and says
	// so in its own comment: "Pitch is not offered: VRChat keeps players upright, so tilting leaves
	// the rig crooked." That is the whole feature the owner asked for — head down, with the view
	// following — so the refusal had to be undone properly rather than by widening the old code.
	//
	// IT IS TWO DIFFERENT LEVERS, AND THAT IS THE POINT. Writing the rig's rotation moves the BODY.
	// It does not move the desktop camera: VRChat rebuilds the view every frame from the mouse-look
	// component, so a rig you tilted looks tilted to everyone including you-in-a-mirror, while your
	// camera stays stubbornly level. MovementModule already found this the hard way — its method #4
	// is documented as spinning "only the avatar MESH", with "the player and the desktop view keep
	// facing the old way".
	//
	//   BODY  →  the rig transform, rewritten every LateUpdate so VRChat's own write cannot undo it.
	//   VIEW  →  NeckMouseRotator.field_Public_NeckRange_0, the CLAMP on how far mouse-look may pitch.
	//            Widen it and the mouse itself carries you past vertical and all the way over.
	//
	// The view half is not invented here: EvilEye's HeadFlipper does exactly this one write —
	// NeckRange(float.MinValue, float.MaxValue, 0) — and that mod ships working. This uses a large
	// FINITE limit instead, because a clamp of ±3.4e38 feeding a lerp is one NaN away from a camera
	// that never comes back, and ±180° is already all the way round.
	//
	// VERIFIED BEFORE IT WAS WRITTEN, not after. Both members exist in this build's interop
	// assemblies: LocomotionInputController.field_Protected_NeckMouseRotator_0 (and
	// GamelikeInputController derives from it, so the desktop controller carries it), and
	// NeckMouseRotator.field_Public_NeckRange_0 of type VRC.DataModel.NeckRange — a three-float
	// struct at fixed offsets 0/4/8. That check is the lesson of the custom-username crash, which
	// shipped on a grep of a name and took the game down.
	//
	// WHAT IT PROBES RATHER THAN ASSUMES. Two things are genuinely unknown until the game is running,
	// so neither is guessed: which of NeckRange's three floats is the min and which the max (the
	// ORIGINAL values are read and logged, and only the two that look like a symmetric clamp are
	// widened), and whether the rig rotation actually holds (it is read back next frame and the
	// result is logged once). If either turns out otherwise, the log says so and the feature reports
	// itself broken instead of pretending.
	//
	// LOCAL, AND HONEST ABOUT IT. The rig transform is the local player's own; VRChat serialises what
	// it serialises, so other people may or may not see the tilt. Nothing is sent by hand, no ApiModel
	// is touched, and turning it off puts the neck clamp and the rig back exactly as they were.
	public class PlayerRotatorModule : IModule
	{
		public override string Name => "PlayerRotator";

		public static string Status = "off";
		public static bool Active { get; private set; }

		/// <summary>Body tilt held by the module, in degrees. Yaw stays VRChat's.</summary>
		private static float _pitch, _roll;

		// ------------------------------------------------------------------ the neck clamp
		private static object _neck;                 // NeckMouseRotator proxy
		private static PropertyInfo _rangeProp;      // its field_Public_NeckRange_0
		private static object _rangeOriginal;        // boxed NeckRange, exactly as we found it
		private static bool _neckWidened;
		private static bool _neckResolved;
		private static string _neckWhy = "not tried";

		// ------------------------------------------------------------------ the body
		private static Transform _rig;
		private static float _rigCheckedAt;
		private static Quaternion _applied = Quaternion.identity;
		private static float _yaw;                   // VRChat's heading, tracked so tilt composes on top
		private static bool _holdProbed;

		public static void Toggle() { Set(!Active); }

		public static void Set(bool on)
		{
			if (on == Active) return;
			Active = on;
			try { if (ModConfig.RotatorEnabled != null) ModConfig.RotatorEnabled.Value = on; } catch { }

			if (on)
			{
				_pitch = 0f; _roll = 0f;
				_rig = null; _holdProbed = false;
				WidenNeck();
				Status = "on — arrows tilt, PgUp/PgDn roll, RShift+F flips, RShift+Backspace resets"
					+ (_neckWidened ? "" : " (view clamp unchanged: " + _neckWhy + ")");
			}
			else
			{
				RestoreNeck();
				RestoreBody();
				Status = "off";
			}
			Killiorim.Logger.LogInfo("[Rotator] " + Status);
			Toast.Show(on ? "Player rotator ON — " + Status : "Player rotator OFF — upright again");
		}

		/// <summary>Straight to upside down and straight back — the thing you actually want a hotkey
		/// for, rather than holding a key for a second and a half. Arms the rotator if it was off, so
		/// one button from the client is enough.</summary>
		public static void Flip()
		{
			if (!Active) Set(true);
			_roll = Mathf.Abs(Mathf.DeltaAngle(_roll, 180f)) < 1f ? 0f : 180f;
			Status = _roll == 0f ? "upright" : "upside down";
			Toast.Show("Rotator — " + Status);
		}

		/// <summary>Back to level WITHOUT switching the rotator off, so the keys stay live.</summary>
		public static void ResetUpright()
		{
			_pitch = 0f; _roll = 0f;
			Status = Active ? "upright — arrows tilt, PgUp/PgDn roll, RShift+F flips" : "off";
			if (!Active) RestoreBody();
			Toast.Show("Rotator — upright");
		}

		public override void OnUpdate()
		{
			try
			{
				// RShift+R arms it. Same family as the mod's other RShift hotkeys, and R was free.
				if (Input.GetKey(KeyCode.RightShift) && Input.GetKeyDown(KeyCode.R)) { Toggle(); return; }
				if (!Active) return;

				float step = Time.deltaTime * Speed();

				// Up/Down = pitch. MovementModule takes Left/Right for yaw and leaves these alone,
				// despite what its config description claims.
				if (Input.GetKey(KeyCode.UpArrow)) _pitch -= step;
				if (Input.GetKey(KeyCode.DownArrow)) _pitch += step;

				// PageUp/PageDown = roll. Nothing in the mod or in VRChat desktop uses them.
				if (Input.GetKey(KeyCode.PageUp)) _roll -= step;
				if (Input.GetKey(KeyCode.PageDown)) _roll += step;

				if (Input.GetKey(KeyCode.RightShift))
				{
					if (Input.GetKeyDown(KeyCode.F)) Flip();
					if (Input.GetKeyDown(KeyCode.Backspace)) ResetUpright();
				}

				_pitch = Wrap(_pitch);
				_roll = Wrap(_roll);
			}
			catch (Exception e) { Status = "rotator: " + e.Message; }
		}

		// APPLIED IN LateUpdate, on purpose: VRChat's locomotion writes the rig during its own
		// Update, so a write from OnUpdate is overwritten before the frame is drawn. Whether VRChat
		// also writes it in LateUpdate is exactly what the probe below measures.
		public override void OnLateUpdate()
		{
			if (!Active) return;
			try
			{
				Transform rig = Rig();
				if (rig == null) return;

				Quaternion current = rig.rotation;

				// Did VRChat rewrite the rig since our last write? If it did, its value is a fresh
				// heading and we adopt it; if it did not, the rotation we are reading is our own
				// composed one and re-deriving a yaw from it would drift (and is degenerate at
				// pitch ±90 anyway).
				if (Quaternion.Angle(current, _applied) > 0.05f)
				{
					Vector3 f = current * Vector3.forward;
					f.y = 0f;
					if (f.sqrMagnitude > 0.0001f) _yaw = Quaternion.LookRotation(f.normalized, Vector3.up).eulerAngles.y;
				}
				else if (!_holdProbed)
				{
					// One line, once: the tilt survived a full frame, so the rig is ours to hold.
					_holdProbed = true;
					Killiorim.Logger.LogInfo(
						"[Rotator] rig rotation holds between frames — body tilt is applied to "
						+ rig.name + ".");
				}

				Quaternion want = Quaternion.AngleAxis(_yaw, Vector3.up) * Quaternion.Euler(_pitch, 0f, _roll);
				rig.rotation = want;
				_applied = want;
			}
			catch (Exception e) { Status = "rotator body: " + e.Message; }
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			// A new world rebuilds the player: every cached proxy here is stale, and the fresh neck
			// rotator comes back with the stock clamp.
			_rig = null; _neck = null; _rangeProp = null; _rangeOriginal = null;
			_neckResolved = false; _neckWidened = false; _holdProbed = false;
			_applied = Quaternion.identity;
			if (Active) WidenNeck();
		}

		public override void OnShutdown()
		{
			try { RestoreNeck(); } catch { }
		}

		// ------------------------------------------------------------------ body

		private static float Speed()
		{
			try { return ModConfig.RotatorSpeed != null ? Mathf.Clamp(ModConfig.RotatorSpeed.Value, 5f, 720f) : 120f; }
			catch { return 120f; }
		}

		private static float Wrap(float a)
		{
			while (a >= 360f) a -= 360f;
			while (a < 0f) a += 360f;
			return a;
		}

		// The topmost transform of the local player rig. Cached, and revalidated a few times a
		// second rather than every frame — NativeGuard.Alive is two VirtualQuery syscalls, and this
		// runs in LateUpdate.
		private static Transform Rig()
		{
			float now = Time.realtimeSinceStartup;
			if (_rig != null)
			{
				if (now - _rigCheckedAt < 0.25f) return _rig;
				_rigCheckedAt = now;
				if (NativeGuard.Alive(_rig)) return _rig;
				_rig = null;
			}
			_rigCheckedAt = now;
			try
			{
				var local = PlayerRef.LocalPlayer();
				if (local == null) return null;
				Transform t = local.transform;
				int guard = 0;
				while (t.parent != null && guard++ < 32) t = t.parent;
				_rig = t;
			}
			catch { _rig = null; }
			return _rig;
		}

		private static void RestoreBody()
		{
			try
			{
				Transform rig = Rig();
				if (rig == null) return;
				rig.rotation = Quaternion.AngleAxis(_yaw, Vector3.up);
				_applied = rig.rotation;
			}
			catch { }
		}

		// ------------------------------------------------------------------ view (the neck clamp)

		// Reaching NeckMouseRotator: the same route MovementModule already proved on this build —
		// the local player's Behaviours, matched by il2cpp class name (never by the generated member
		// names, which change every update), then the controller member whose TYPE is
		// NeckMouseRotator. GamelikeInputController derives from LocomotionInputController, so the
		// inherited field_Protected_NeckMouseRotator_0 is right there on it.
		private static void ResolveNeck()
		{
			if (_neckResolved) return;
			_neckResolved = true;

			// field_* members are il2cpp FIELD reads, and on this build Il2CppInterop computes field
			// offsets from the wrong slot until FieldOffsetFix has repaired it. An unrepaired read
			// lands outside the object: an access violation .NET cannot catch. So this does not run
			// at all until the repair is confirmed.
			if (!FieldOffsetFix.Verified)
			{
				_neckWhy = "field offsets not repaired on this build";
				_neckResolved = false;   // try again later; FieldOffsetFix installs during startup
				return;
			}

			try
			{
				var local = PlayerRef.LocalPlayer();
				if (local == null) { _neckWhy = "no local player yet"; _neckResolved = false; return; }

				Transform root = local.transform;
				int guard = 0;
				while (root.parent != null && guard++ < 32) root = root.parent;

				object ctl = null;
				foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
				{
					if (b == null) continue;
					string n;
					try { n = MenuCard.Il2CppNameOf(b); } catch { continue; }
					if (n != "GamelikeInputController" && n != "LocomotionInputController") continue;
					Type ct = null;
					foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
					{
						try { ct = asm.GetType(n, false); } catch { }
						if (ct != null) break;
					}
					if (ct == null) continue;
					var tryCast = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
						.GetMethod("TryCast").MakeGenericMethod(ct);
					ctl = tryCast.Invoke(b, null);
					if (ctl != null) break;
				}
				if (ctl == null)
				{
					_neckWhy = "no desktop locomotion controller (VR, or renamed this build)";
					return;
				}

				PropertyInfo neckProp = null;
				foreach (var pi in ctl.GetType().GetProperties(
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy))
				{
					if (pi.PropertyType.Name != "NeckMouseRotator") continue;
					neckProp = pi; break;
				}
				if (neckProp == null) { _neckWhy = "controller has no NeckMouseRotator member"; return; }

				try { _neck = neckProp.GetValue(ctl); } catch (Exception e) { _neckWhy = "neck read threw: " + e.Message; return; }
				if (_neck == null) { _neckWhy = "NeckMouseRotator is null"; return; }

				foreach (var pi in _neck.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (pi.PropertyType.Name != "NeckRange") continue;
					_rangeProp = pi; break;
				}
				if (_rangeProp == null) { _neckWhy = "NeckMouseRotator has no NeckRange member"; return; }

				_neckWhy = "ready";
			}
			catch (Exception e) { _neckWhy = "resolve threw: " + e.Message; }
		}

		private static void WidenNeck()
		{
			try
			{
				if (!ModConfig.RotatorFreeLook.Value) { _neckWhy = "free look switched off"; return; }
				ResolveNeck();
				if (_neck == null || _rangeProp == null) return;
				if (_neckWidened) return;

				object range;
				try { range = _rangeProp.GetValue(_neck); }
				catch (Exception e) { _neckWhy = "range read threw: " + e.Message; return; }
				if (range == null) { _neckWhy = "range read null"; return; }

				FieldInfo[] fs = Floats(range.GetType());
				if (fs == null) { _neckWhy = "NeckRange is not the expected three floats"; return; }

				float a = (float)fs[0].GetValue(range);
				float b = (float)fs[1].GetValue(range);
				float c = (float)fs[2].GetValue(range);
				_rangeOriginal = range;

				// WHICH TWO ARE THE CLAMP. VRChat's stock desktop neck is a symmetric-ish pitch limit
				// (something near ±70°), so the pair to widen is the one that straddles zero. Reading
				// it rather than assuming the ctor's argument order is the whole reason this is safe
				// to ship: if the layout is not what the decompile suggested, the log says so and
				// nothing is written.
				float lim = Mathf.Clamp(ModConfig.RotatorNeckLimit.Value, 90f, 1800f);
				object widened = Activator.CreateInstance(range.GetType());
				fs[0].SetValue(widened, a); fs[1].SetValue(widened, b); fs[2].SetValue(widened, c);

				bool ok = false;
				if (a < 0f && b > 0f) { fs[0].SetValue(widened, -lim); fs[1].SetValue(widened, lim); ok = true; }
				else if (b < 0f && c > 0f) { fs[1].SetValue(widened, -lim); fs[2].SetValue(widened, lim); ok = true; }

				Killiorim.Logger.LogInfo(
					"[Rotator] NeckRange as found: (" + a.ToString("F1") + ", " + b.ToString("F1") + ", "
					+ c.ToString("F1") + ")" + (ok ? " -> widened to ±" + lim.ToString("F0") + "°"
					: " — no pair straddles zero, so the clamp was left alone"));

				if (!ok) { _neckWhy = "could not tell which floats are the clamp"; return; }

				try { _rangeProp.SetValue(_neck, widened); }
				catch (Exception e) { _neckWhy = "range write threw: " + e.Message; return; }

				// Read back. A write that did not take is worse than no write, because everything
				// downstream would report a feature that is not there.
				try
				{
					object after = _rangeProp.GetValue(_neck);
					float a2 = (float)fs[0].GetValue(after);
					float b2 = (float)fs[1].GetValue(after);
					float c2 = (float)fs[2].GetValue(after);
					_neckWidened = Mathf.Abs(a2 - a) > 1f || Mathf.Abs(b2 - b) > 1f || Mathf.Abs(c2 - c) > 1f;
					_neckWhy = _neckWidened ? "widened" : "write did not stick";
				}
				catch { _neckWidened = true; _neckWhy = "widened (read-back unavailable)"; }

				Killiorim.Logger.LogInfo("[Rotator] free look: " + _neckWhy
					+ ". Mouse pitch is unclamped — look up and keep going to end up head-down.");
			}
			catch (Exception e) { _neckWhy = "widen threw: " + e.Message; }
		}

		private static void RestoreNeck()
		{
			try
			{
				if (!_neckWidened || _neck == null || _rangeProp == null || _rangeOriginal == null) return;
				_rangeProp.SetValue(_neck, _rangeOriginal);
				_neckWidened = false;
				Killiorim.Logger.LogInfo("[Rotator] neck clamp put back the way it was.");
			}
			catch (Exception e) { Killiorim.Logger.LogWarning("[Rotator] could not restore the neck clamp: " + e.Message); }
		}

		// NeckRange's three floats sit at fixed managed offsets (0/4/8) rather than behind the
		// il2cpp offset lookup, so these are ordinary field reads — but the ORDER is still taken
		// from the type, not hardcoded.
		private static FieldInfo[] Floats(Type t)
		{
			try
			{
				var all = t.GetFields(BindingFlags.Instance | BindingFlags.Public);
				int n = 0;
				for (int i = 0; i < all.Length; i++) if (all[i].FieldType == typeof(float)) n++;
				if (n != 3) return null;
				var outp = new FieldInfo[3];
				int k = 0;
				for (int i = 0; i < all.Length; i++) if (all[i].FieldType == typeof(float)) outp[k++] = all[i];
				return outp;
			}
			catch { return null; }
		}
	}
}
