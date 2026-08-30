using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// FORCE JUMP — a one-shot upward launch of the local player.
	//
	// It is ordinary movement, the same nature as fly, teleport and the speed sliders already in the
	// mod: it moves YOU, over VRChat's own sanctioned per-player API, and other players see it exactly
	// as they would see any jump. Nothing is sent that is not sent for a normal move, nobody else's
	// client is touched, and there is no world state involved. It is in the "fun" family, not the
	// self-defence one.
	//
	// The launch preserves your horizontal momentum and only rewrites the vertical component, so a
	// jump taken while walking carries you forward rather than killing your run. VRChat's own
	// VRCPlayerApi.SetVelocity is the sanctioned setter — the same call the SDK gives worlds to move a
	// player — so this is not an exploit, it is the movement API used deliberately.
	public class ForceJumpModule : IModule
	{
		public override string Name => "ForceJump";

		public static string Status = "";

		// The impulse, in metres/second of upward velocity. A normal VRChat jump is around 3; the
		// default here is a strong-but-sane hop. Read live from config so the client's slider takes
		// effect without a restart, and clamped so a stray value cannot fling you out of the world.
		private static float ConfiguredForce
		{
			get
			{
				try { return Mathf.Clamp(ModConfig.ForceJumpForce.Value, 1f, 50f); }
				catch { return 8f; }
			}
		}

		// A keybind for using it in-game without the desktop client in focus. RightShift is the mod's
		// modifier throughout (RightShift+G force-grabs, RightShift+M opens the menu), so RightShift+J
		// keeps the family and avoids VRChat's bare Space = jump.
		public override void OnUpdate()
		{
			try
			{
				if (Input.GetKey(KeyCode.RightShift) && Input.GetKeyDown(KeyCode.J))
					Launch(ConfiguredForce);
			}
			catch { }
		}

		// force <= 0 means "use the configured value" — the client passes an explicit number, the
		// keybind passes the config, and a one-place clamp keeps both honest.
		public static bool Launch(float force = 0f)
		{
			try
			{
				var api = PlayerRef.LocalApi();
				if (api == null) { Status = "no local player yet"; return false; }

				float up = force > 0f ? Mathf.Clamp(force, 1f, 50f) : ConfiguredForce;

				Vector3 v;
				try { v = api.GetVelocity(); }
				catch { v = Vector3.zero; }

				// Keep the horizontal run, replace the vertical. Setting Y absolutely (rather than
				// adding) makes a second press a re-launch instead of an ever-growing rocket, which is
				// the predictable behaviour for a button you might mash.
				try { api.SetVelocity(new Vector3(v.x, up, v.z)); }
				catch (Exception ex) { Status = "could not apply velocity: " + ex.Message; return false; }

				Status = "jumped (" + up.ToString("0.#") + " m/s up)";
				VRChatArchiveModPlugin.Logger.LogInfo($"[ForceJump] launched at {up:0.#} m/s.");
				return true;
			}
			catch (Exception e) { Status = "failed: " + e.Message; return false; }
		}
	}
}
