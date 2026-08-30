using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// RADAR MAP — an optional top-down view of the world, drawn underneath the radar blips.
	//
	// A second Camera is parked above the local player looking straight down and rendered into a
	// RenderTexture. Three things keep that from costing what a second camera usually costs:
	//
	//   * it is DISABLED and rendered by hand (camera.Render()) on our own cadence, so Unity never
	//     draws it as part of the normal frame
	//   * it renders at a small square resolution, because the result is shown inside a radar a few
	//     hundred pixels wide
	//   * its culling mask keeps world geometry and drops players, UI and mirrors — avatars are
	//     already drawn as blips, and a mirror in view would render the world twice
	//
	// OFF by default. It is the one feature here that genuinely costs frames, so it is the user's
	// choice to spend them, and the menu says so.
	public static class RadarMapCamera
	{
		private static Camera _cam;
		private static RenderTexture _rt;
		private static GameObject _go;
		private static int _frame;
		private static int _res;
		private static bool _failed;

		public static bool Ready => _cam != null && _rt != null;
		public static Texture Texture => _rt;

		// Everything except players, UI and mirrors. Layer numbers are VRChat's own and stable
		// across builds; naming them here beats a magic number.
		private const int LayerDefault = 0, LayerWater = 4, LayerUI = 5, LayerInteractive = 8;
		private const int LayerPlayer = 9, LayerPlayerLocal = 10, LayerEnvironment = 11;
		private const int LayerUiMenu = 12, LayerPickup = 13, LayerPickupNoEnv = 14;
		private const int LayerWalkthrough = 17, LayerMirrorReflection = 18;

		private static int Mask()
		{
			int m = 0;
			foreach (int l in new[] { LayerDefault, LayerWater, LayerInteractive, LayerEnvironment,
									  LayerPickup, LayerPickupNoEnv, LayerWalkthrough })
				m |= 1 << l;
			// Explicitly OFF, so a future edit to the include list cannot let them back in.
			m &= ~(1 << LayerUI);
			m &= ~(1 << LayerUiMenu);
			m &= ~(1 << LayerPlayer);
			m &= ~(1 << LayerPlayerLocal);
			m &= ~(1 << LayerMirrorReflection);
			return m;
		}

		// Called from the radar's OnGui. Returns false when there is nothing to draw.
		public static bool Tick(float rangeMeters, Camera playerCam)
		{
			try
			{
				if (!ModConfig.RadarMap.Value || _failed) { Release(); return false; }
				if (playerCam == null) return false;

				var local = PlayerRef.LocalTransform();
				if (local == null) return false;

				int want = Mathf.Clamp(ModConfig.RadarMapResolution.Value, 96, 1024);
				if (_rt != null && _res != want) Release();
				if (!Ensure(want)) return false;

				// Follow the player, and share the player camera's HEADING so the map turns with the
				// blips. The radar is camera-relative: a map that stayed world-aligned would have
				// every dot sliding across a picture that never moved with them.
				float yaw = 0f;
				try { yaw = playerCam.transform.eulerAngles.y; } catch { }
				float height = Mathf.Max(10f, ModConfig.RadarMapHeight.Value);
				_go.transform.position = local.position + Vector3.up * height;
				_go.transform.rotation = Quaternion.Euler(90f, yaw, 0f);
				_cam.orthographicSize = Mathf.Max(2f, rangeMeters);
				_cam.farClipPlane = height + Mathf.Max(50f, rangeMeters);

				// Rendered on OUR cadence, not the frame's.
				int every = Mathf.Clamp(ModConfig.RadarMapEveryFrames.Value, 1, 30);
				if (_frame++ % every == 0)
				{
					try { _cam.Render(); }
					catch (Exception e)
					{
						// One failure is enough: a camera that throws will throw every frame, and a
						// radar is not worth an exception per repaint.
						_failed = true;
						VRChatArchiveModPlugin.Logger.LogWarning($"[RadarMap] render failed, map disabled: {e.Message}");
						Release();
						return false;
					}
				}
				return true;
			}
			catch { return false; }
		}

		private static bool Ensure(int res)
		{
			if (_cam != null && _rt != null) return true;
			try
			{
				_res = res;
				_rt = new RenderTexture(res, res, 16, RenderTextureFormat.Default)
				{
					name = "ArchiveRadarMapRT",
					hideFlags = HideFlags.HideAndDontSave,
					antiAliasing = 1,
					filterMode = FilterMode.Bilinear,
					useMipMap = false,
				};
				_rt.Create();

				_go = new GameObject("ArchiveRadarMapCam");
				_go.hideFlags = HideFlags.HideAndDontSave;
				UnityEngine.Object.DontDestroyOnLoad(_go);

				_cam = _go.AddComponent<Camera>();
				_cam.orthographic = true;
				_cam.clearFlags = CameraClearFlags.SolidColor;
				_cam.backgroundColor = new Color(0.03f, 0.03f, 0.05f, 1f);
				_cam.cullingMask = Mask();
				_cam.targetTexture = _rt;      // never draws to the screen
				_cam.depth = -100;             // and never competes with the game's cameras
				_cam.allowHDR = false;
				_cam.allowMSAA = false;
				_cam.useOcclusionCulling = false;
				_cam.nearClipPlane = 0.3f;
				_cam.enabled = false;          // we call Render() ourselves

				VRChatArchiveModPlugin.Logger.LogInfo($"[RadarMap] camera ready ({res}x{res}).");
				return true;
			}
			catch (Exception e)
			{
				_failed = true;
				VRChatArchiveModPlugin.Logger.LogWarning($"[RadarMap] could not create the camera: {e.Message}");
				Release();
				return false;
			}
		}

		public static void Release()
		{
			try { if (_cam != null) _cam.targetTexture = null; } catch { }
			try { if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); } } catch { }
			try { if (_go != null) UnityEngine.Object.Destroy(_go); } catch { }
			_cam = null; _rt = null; _go = null; _res = 0;
		}
	}
}
