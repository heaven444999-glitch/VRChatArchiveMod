using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Local, self-only movement: desktop fly + noclip with arrow-key rotation. This only
	// moves the player's OWN avatar client-side — it does not touch, target, or affect
	// anyone else.
	//
	// Fly: Ctrl+F toggles it. Noclip: while flying, HOLD right-mouse to pass through walls
	// (release = solid). Float freely with:
	//   WASD + E/Q move relative to the camera, Shift = faster,
	//   arrow keys rotate (Left/Right = yaw, Up/Down = pitch). While flying the player holds
	//   position when no key is pressed instead of falling.
	//
	// Rather than editing global Physics.gravity (which would disturb the whole world's
	// physics and gets fought by VRChat), fly simply pins the transform each frame: velocity
	// is zeroed and, with no movement input, the position is re-asserted. Reflection-based so
	// it isn't bound to obfuscated signatures that rotate every VRChat build.
	public class MovementModule : IModule
	{
		public override string Name => "Movement";

		private bool _flying;
		private bool _noclip;
		private bool _flyHotkeyWasDown;
		private bool _flyWas;            // edge-detect fly so noclip can follow it on/off
		private readonly List<Collider> _disabled = new List<Collider>();
		private Vector3 _holdPos;
		private bool _hasHold;
		private Transform _vrcPlayerT;   // the real player root (capsule + camera live here)
		private Transform _capsuleT;     // the CharacterController's transform
		private bool _loggedHierarchy;

		public override void OnUpdate()
		{
			try
			{
				bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftCommand);
				bool menuOpen = Menu.Visible;   // the mod menu owns input while it's open

				// Ctrl+F edge-toggles FLY on/off (ignored while the menu owns input).
				bool flyHotkey = !menuOpen && ctrl && Input.GetKey(KeyCode.F);
				if (flyHotkey && !_flyHotkeyWasDown)
					ModConfig.FlyEnabled.Value = !ModConfig.FlyEnabled.Value;
				_flyHotkeyWasDown = flyHotkey;

				// FLY and NOCLIP travel together: turning fly on turns noclip on automatically, and
				// turning fly off drops it. The menu still exposes a noclip switch so you can fly
				// with collision back on, but the moment fly is (re)enabled noclip follows it.
				if (ModConfig.FlyEnabled.Value != _flyWas)
				{
					_flyWas = ModConfig.FlyEnabled.Value;
					ModConfig.NoclipEnabled.Value = _flyWas;
				}
				// Noclip without fly would just drop you through the floor.
				if (ModConfig.NoclipEnabled.Value && !ModConfig.FlyEnabled.Value)
					ModConfig.FlyEnabled.Value = _flyWas = true;

				// Click-teleport: hold right mouse + press left = teleport to the aimed surface.
				// Skipped while the mod menu is open so clicking buttons doesn't warp you.
				if (ModConfig.ClickTpEnabled.Value && !Menu.Visible
					&& Input.GetMouseButton(1) && Input.GetMouseButtonDown(0))
					ClickTeleport();

				// Apply state changes (edges).
				if (ModConfig.FlyEnabled.Value != _flying)
					SetFlying(ModConfig.FlyEnabled.Value);
				if (ModConfig.NoclipEnabled.Value != _noclip)
					SetNoclip(ModConfig.NoclipEnabled.Value);

				// Re-assert noclip every frame: VRChat re-enables the player collider on avatar
				// loads and locomotion resets, which would silently restore collision. If our
				// captured list has gone stale (avatar swap), re-scan.
				if (_noclip)
				{
					bool stale = _disabled.Count == 0;
					for (int i = 0; i < _disabled.Count; i++)
					{
						try { if (_disabled[i] == null) { stale = true; break; } if (_disabled[i].enabled) _disabled[i].enabled = false; }
						catch { stale = true; break; }
					}
					if (stale) { _disabled.Clear(); CollectPlayerColliders(_disabled); DisableAll(_disabled); }
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[Movement] update threw: {e}");
			}
		}

		// Fly movement + rotation run in LateUpdate, AFTER VRChat's own locomotion has set the
		// player transform this frame — otherwise the game overwrites our rotation/position the
		// same frame and the arrow-key turning "doesn't take". Running last means we win.
		public override void OnLateUpdate()
		{
			try
			{
				if (_flying) UpdateFly();
				UpdateRotate();   // arrow-key turning works on the ground too
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[Movement] late update threw: {e}");
			}
		}

		public override void OnShutdown()
		{
			try { if (_noclip) SetNoclip(false); } catch { }
			try { if (_flying) SetFlying(false); } catch { }
		}

		// Raycasts from the camera and drops the player on the first solid surface hit. Desktop
		// VRChat locks the cursor to look, so we aim straight down the camera's forward (the
		// crosshair) rather than the mouse position. One-shot: sets position, zeroes velocity.
		private void ClickTeleport()
		{
			try
			{
				var cam = Camera.main;
				var player = PlayerRef.LocalPlayer();
				if (cam == null || player == null) return;

				Vector3 origin = cam.transform.position;
				Vector3 dir = cam.transform.forward;
				if (Physics.Raycast(origin, dir, out RaycastHit hit, ModConfig.ClickTpMaxDistance.Value))
				{
					// Stand slightly above the surface so we don't clip into it.
					Vector3 dest = hit.point + Vector3.up * 0.15f;
					player.transform.position = dest;
					if (_flying) { _holdPos = dest; _hasHold = true; }
					PlayerRef.ZeroVelocity(player);
					VRChatArchiveModPlugin.Logger.LogInfo($"[Movement] click-teleport to {dest} ({hit.distance:F1}m).");
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[Movement] click-teleport failed: {e.Message}");
			}
		}

		// Arrow-key turning. Three ways to rotate a player, tried in order, because
		// transform.rotation alone only spins the avatar MESH — the player and the desktop
		// view keep facing the old way, which is exactly why turning did nothing:
		//   1. VRCPlayerApi.TeleportTo(pos, rot)  — the SDK's sanctioned orientation setter
		//   2. VRCPlayer's own (Vector3, Quaternion) teleport
		//   3. transform rotation on the whole rig (last resort)
		// Whichever moves GetRotation() is remembered and used from then on. Pitch is not
		// offered: VRChat keeps players upright, so tilting leaves the rig crooked.
		private int _rotMethod = -1;      // -1 = not chosen yet
		private bool _rotLogged;

		private void UpdateRotate()
		{
			if (!ModConfig.ArrowRotateEnabled.Value) return;

			float step = Time.deltaTime * ModConfig.FlyRotateSpeed.Value;
			float yaw = 0f;
			if (Input.GetKey(KeyCode.LeftArrow)) yaw -= step;
			if (Input.GetKey(KeyCode.RightArrow)) yaw += step;
			if (yaw == 0f) return;

			var api = PlayerRef.LocalApi();
			if (api == null)
			{
				if (!_rotLogged) { _rotLogged = true; VRChatArchiveModPlugin.Logger.LogWarning("[Movement] rotate: no local VRCPlayerApi — turning unavailable."); }
				return;
			}

			Quaternion before;
			Vector3 pos;
			try { before = api.GetRotation(); pos = api.GetPosition(); }
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[Movement] rotate: read failed: {e.Message}"); return; }

			// pure heading change: drop any pitch/roll the rig carries
			Vector3 fwd = before * Vector3.forward;
			fwd.y = 0f;
			if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
			Quaternion target = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.LookRotation(fwd.normalized, Vector3.up);

			int first = _rotMethod >= 0 ? _rotMethod : 0;
			int last = _rotMethod >= 0 ? _rotMethod : 2;
			for (int m = first; m <= last; m++)
			{
				if (!TryRotate(m, api, pos, target)) continue;

				// Did it take? Compare headings, not raw quaternions.
				Quaternion after;
				try { after = api.GetRotation(); } catch { after = before; }
				bool moved = Quaternion.Angle(after, before) > 0.01f;
				if (moved || _rotMethod >= 0)
				{
					if (_rotMethod < 0)
					{
						_rotMethod = m;
						VRChatArchiveModPlugin.Logger.LogInfo($"[Movement] rotate: using method #{m}.");
					}
					_holdPos = pos; _hasHold = true;
					return;
				}
			}

			if (_rotMethod < 0 && !_rotLogged)
			{
				_rotLogged = true;
				VRChatArchiveModPlugin.Logger.LogWarning("[Movement] rotate: none of the 3 methods changed GetRotation() — turning unavailable on this build.");
			}
		}

		private bool TryRotate(int method, VRC.SDKBase.VRCPlayerApi api, Vector3 pos, Quaternion target)
		{
			try
			{
				switch (method)
				{
					case 0:
						api.TeleportTo(pos, target);
						return true;
					case 1:
					{
						var local = PlayerRef.LocalPlayer();
						object vp = local == null ? null : FewTagsModule.GetMemberByTypeName(local, "VRCPlayer", "_vrcplayer", "prop_VRCPlayer_0", "field_Private_VRCPlayer_0");
						if (vp == null) return false;
						foreach (var mi in vp.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
						{
							var ps = mi.GetParameters();
							if (ps.Length != 2 || ps[0].ParameterType != typeof(Vector3) || ps[1].ParameterType != typeof(Quaternion)) continue;
							mi.Invoke(vp, new object[] { pos, target });
							return true;
						}
						return false;
					}
					default:
					{
						var local = PlayerRef.LocalPlayer();
						if (local == null) return false;
						Transform root = local.transform;
						int guard = 0;
						while (root.parent != null && guard++ < 32) root = root.parent;
						root.rotation = target;
						local.transform.rotation = target;
						return true;
					}
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[Movement] rotate method #{method} threw: {e.Message}");
				return false;
			}
		}

		private void UpdateFly()
		{
			var player = PlayerRef.LocalPlayer();
			var cam = Camera.main;
			if (player == null || cam == null) return;

			PlayerRef.ZeroVelocity(player);

			Transform t = player.transform;
			Transform ct = cam.transform;

			// Translation relative to the camera.
			float speed = Time.deltaTime * (Input.GetKey(KeyCode.LeftShift) ? ModConfig.FlyBoostSpeed.Value : ModConfig.FlySpeed.Value);
			Vector3 move = Vector3.zero;
			if (Input.GetKey(KeyCode.W)) move += ct.forward;
			if (Input.GetKey(KeyCode.S)) move -= ct.forward;
			if (Input.GetKey(KeyCode.D)) move += ct.right;
			if (Input.GetKey(KeyCode.A)) move -= ct.right;
			if (Input.GetKey(KeyCode.E)) move += Vector3.up;
			if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;

			if (move != Vector3.zero)
			{
				t.position += move * speed;
				_holdPos = t.position;   // moved this frame → new anchor
				_hasHold = true;
			}
			else if (_hasHold)
			{
				// No input: re-assert last position so gravity/velocity can't drift us down.
				t.position = _holdPos;
			}
			else
			{
				_holdPos = t.position;
				_hasHold = true;
			}

		}

		// Fly on/off. Enabling captures the current position as the hold anchor so we float
		// in place instead of falling. Collider handling lives entirely in SetNoclip.
		private void SetFlying(bool value)
		{
			_flying = value;
			if (value)
			{
				var p = PlayerRef.LocalPlayer();
				if (p != null) { _holdPos = p.transform.position; _hasHold = true; }
			}
			else
			{
				_hasHold = false;
				// Leaving fly must also drop noclip so collision comes back.
				if (_noclip) SetNoclip(false);
			}
			VRChatArchiveModPlugin.Logger.LogInfo($"[Movement] fly {(value ? "ON" : "OFF")}.");
		}

		// Noclip on/off: disables the local player's solid colliders so you pass through world
		// geometry (CharacterController derives from Collider, so one scan covers the capsule
		// and the controller). They live on the locomotion rig, not on the VRC.Player object,
		// so we scan the whole local-player hierarchy.
		private void SetNoclip(bool value)
		{
			try
			{
				if (value)
				{
					_disabled.Clear();
					CollectPlayerColliders(_disabled);
					DisableAll(_disabled);
					VRChatArchiveModPlugin.Logger.LogInfo($"[Movement] noclip ON — disabled {_disabled.Count} collider(s)"
						+ (_disabled.Count == 0 ? " (NONE FOUND — noclip won't work; tell me)" : "") + ".");
				}
				else
				{
					int restored = 0;
					for (int i = 0; i < _disabled.Count; i++)
					{
						try { if (_disabled[i] != null) { _disabled[i].enabled = true; restored++; } } catch { }
					}
					_disabled.Clear();
					VRChatArchiveModPlugin.Logger.LogInfo($"[Movement] noclip OFF — restored {restored} collider(s).");
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[Movement] noclip toggle failed: {e.Message}");
			}

			_noclip = value;
		}

		// The avatar mesh and the player's collision capsule/camera are DIFFERENT transforms —
		// rotating VRC.Player's transform only spun the avatar. Apply the rotation to every
		// relevant transform (avatar root, the VRCPlayer root, and the CharacterController's
		// object) so the actual player — capsule and view — turns, not just the mesh.
		private void ApplyRotationToPlayer(Component player, Quaternion rot)
		{
			try { player.transform.rotation = rot; } catch { }

			if (_vrcPlayerT == null) _vrcPlayerT = ResolveVrcPlayerTransform(player);
			try { if (_vrcPlayerT != null) _vrcPlayerT.rotation = rot; } catch { _vrcPlayerT = null; }

			if (_capsuleT == null) { var cc = FindLocalCharacterController(); if (cc != null) _capsuleT = cc.transform; }
			try { if (_capsuleT != null) _capsuleT.rotation = rot; } catch { _capsuleT = null; }

			if (!_loggedHierarchy) { _loggedHierarchy = true; LogHierarchy(player); }
		}

		// VRC.Player._vrcplayer → the VRCPlayer MonoBehaviour, whose transform is the player root.
		private static Transform ResolveVrcPlayerTransform(Component player)
		{
			try
			{
				var t = player.GetType();
				var vp = (t.GetProperty("_vrcplayer") ?? t.GetProperty("prop_VRCPlayer_0") ?? t.GetProperty("prop_VRCPlayer_1"))?.GetValue(player);
				if (vp is Component c) return c.transform;
			}
			catch { }
			return null;
		}

		private static CharacterController FindLocalCharacterController()
		{
			try
			{
				var player = PlayerRef.LocalPlayer();
				if (player == null) return null;
				Transform root = player.transform;
				int guard = 0;
				while (root.parent != null && guard++ < 32) root = root.parent;
				return root.GetComponentInChildren<CharacterController>(true);
			}
			catch { return null; }
		}

		// One-time diagnostic so we can see the real player rig if rotation still looks wrong.
		private static void LogHierarchy(Component player)
		{
			try
			{
				Transform root = player.transform;
				int guard = 0;
				while (root.parent != null && guard++ < 32) root = root.parent;
				var sb = new System.Text.StringBuilder("[Movement] local player rig from root:\n");
				Walk(root, 0, sb);
				VRChatArchiveModPlugin.Logger.LogInfo(sb.ToString());
			}
			catch { }
		}

		private static void Walk(Transform t, int depth, System.Text.StringBuilder sb)
		{
			if (t == null || depth > 6) return;
			try
			{
				string flags = "";
				if (t.GetComponent<CharacterController>() != null) flags += " [CC]";
				if (t.GetComponent<Camera>() != null) flags += " [CAM]";
				sb.Append(' ', depth * 2).Append(t.name).Append(flags).Append('\n');
				for (int i = 0; i < t.childCount && i < 12; i++) Walk(t.GetChild(i), depth + 1, sb);
			}
			catch { }
		}

		private static void DisableAll(List<Collider> cols)
		{
			for (int i = 0; i < cols.Count; i++)
			{
				try { if (cols[i] != null) cols[i].enabled = false; } catch { }
			}
		}

		// Every solid (non-trigger) collider on the local player's rig, scanned from the
		// top-most parent so nothing on the locomotion hierarchy is missed.
		private static void CollectPlayerColliders(List<Collider> into)
		{
			try
			{
				var player = PlayerRef.LocalPlayer();
				if (player == null) return;
				Transform root = player.transform;
				int guard = 0;
				while (root.parent != null && guard++ < 32) root = root.parent;

				var cols = root.GetComponentsInChildren<Collider>(true);
				if (cols == null) return;
				for (int i = 0; i < cols.Length; i++)
				{
					var c = cols[i];
					if (c == null) continue;
					try { if (c.isTrigger) continue; } catch { }   // keep trigger zones intact
					into.Add(c);
				}
			}
			catch { }
		}
	}
}
