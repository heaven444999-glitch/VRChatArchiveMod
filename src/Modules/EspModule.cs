using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using VRC.Core;
using VRC.SDKBase;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Player ESP: draws a box around remote players, colored by their VRChat trust rank
	// (Visitor / New User / User / Known / Trusted / Nuisance), with optional name and
	// distance. This is a VISUALIZATION of positions your client already receives — it is
	// not a targeting/aimbot/tracking tool and never follows or singles out an individual.
	//
	// Players come from VRCPlayerApi.AllPlayers (stable SDK API). Trust rank comes from the
	// player's APIUser (typed has*TrustLevel properties). Drawing happens on OnGui (Repaint).
	public class EspModule : IModule
	{
		public override string Name => "ESP";

		// Trust palette — official VRChat rank colours, plus a bright-red VRChat Team rank.
		private static readonly Color CVisitor  = new Color(0.84f, 0.84f, 0.88f); // #D6D6E0 light grey
		private static readonly Color CNewUser  = new Color(0.09f, 0.47f, 1.00f); // #1778FF blue
		private static readonly Color CUser     = new Color(0.17f, 0.81f, 0.36f); // #2BCF5C green
		private static readonly Color CKnown    = new Color(1.00f, 0.48f, 0.26f); // #FF7B42 orange
		private static readonly Color CTrusted  = new Color(0.51f, 0.26f, 0.90f); // #8143E6 purple
		private static readonly Color CNuisance = new Color(0.55f, 0.00f, 0.00f); // #8B0000 dark red
		private static readonly Color CVrcTeam  = new Color(1.00f, 0.12f, 0.12f); // #FF1F1F VRChat Team

		private Type _playerType;
		private PropertyInfo _mApiUser;
		private MethodInfo _tryCastPlayer;
		private bool _resolved;

		private GUIStyle _label;
		private static int _lastFrame = -1;

		// NEAREST-N CAP. ESP draws in OnGui every frame, so it cannot be time-throttled without the
		// boxes flickering. Instead only the EspCap closest players are drawn each frame: the per-
		// player projection + skeleton work is what froze the main thread (profiler: 467 ms/s at 30
		// players), and a freeze that deep drops you from the instance to your home. Anyone past the
		// cap is distant clutter; nearest-first means the readable ones are always the ones drawn.
		private const int EspCap = 24;
		private static int[] _order;
		private static float[] _distBuf;

		public override void OnGui()
		{
			try
			{
				if (!ModConfig.EspEnabled.Value) return;
				if (Event.current.type != EventType.Repaint) return;

				// ONCE PER FRAME. IMGUI issues a Repaint per GUI pass, and with the mod menu open
				// there is more than one per frame — so every projection, bone fetch and interop
				// call below was being paid several times over for a single visible result. The
				// profiler had this module at 209 ms/s, a fifth of every second.
				if (_lastFrame == Time.frameCount) return;
				_lastFrame = Time.frameCount;

				var cam = Camera.main;
				if (cam == null) return;

				var players = VRCPlayerApi.AllPlayers;
				if (players == null) return;

				ResolveReflection();
				EnsureStyle();

				float maxDist = ModConfig.EspMaxDistance.Value;
				Vector3 camPos = cam.transform.position;
				int count = players.Count;

				// Portals and pickups are no longer drawn here. Screen-space diamonds with a name
				// and a distance turned a busy world into a wall of overlapping labels that told you
				// less than nothing. HighlightEspModule lights the object's own geometry instead —
				// VRChat's own HighlightsFX, the effect it uses for "Hold to Grab".

				// FRUSTUM CULL, once. A player behind you or across the map cost exactly as much as
				// the one you are looking at — every projection, bone fetch and interop crossing was
				// paid for people who could not be seen. Six plane tests reject them before any of
				// that. Computed one time per frame, not per player.
				var frustum = GeometryUtility.CalculateFrustumPlanes(cam);

				// PRE-PASS: collect every non-local player inside the distance cap, then keep only the
				// EspCap nearest. GetPosition is cheap; the projection, bone fetches and line drawing
				// below are not, so bounding how many players reach them is what keeps a full instance
				// from freezing the frame. Sorting a few dozen floats per frame is nothing next to that.
				if (_order == null || _order.Length < count) { _order = new int[count]; _distBuf = new float[count]; }
				int m = 0;
				for (int i = 0; i < count; i++)
				{
					VRCPlayerApi a;
					try { a = players[i]; } catch { continue; }
					if (a == null || a.isLocal) continue;
					Vector3 fp;
					try { fp = a.GetPosition(); } catch { continue; }
					float d = Vector3.Distance(camPos, fp);
					if (maxDist > 0f && d > maxDist) continue;   // 0 = unlimited
					_order[m] = i; _distBuf[m] = d; m++;
				}
				if (m > 1) Array.Sort(_distBuf, _order, 0, m);   // nearest first; _order follows _distBuf
				int drawCount = Mathf.Min(m, EspCap);

				for (int k = 0; k < drawCount; k++)
				{
					try
					{
						VRCPlayerApi api = players[_order[k]];
						if (api == null || api.isLocal) continue;

						Vector3 feet;
						try { feet = api.GetPosition(); } catch { continue; }
						float dist = _distBuf[k];

						// Roughly a person-sized box at the feet; if none of it is in view, skip the
						// whole player before touching the avatar.
						var bb = new Bounds(feet + Vector3.up, new Vector3(1.2f, 2.4f, 1.2f));
						if (!GeometryUtility.TestPlanesAABB(frustum, bb)) continue;

						Vector3 head;
						try { head = api.GetBonePosition(HumanBodyBones.Head); } catch { head = Vector3.zero; }
						if (head == Vector3.zero) head = feet + Vector3.up * 1.7f;

						Vector3 fs = cam.WorldToScreenPoint(feet);
						Vector3 hs = cam.WorldToScreenPoint(head);
						if (fs.z <= 0f && hs.z <= 0f) continue; // fully behind camera

						float feetY = Screen.height - fs.y;
						float headY = Screen.height - hs.y;
						float top = Mathf.Min(feetY, headY);
						float h = Mathf.Abs(feetY - headY);
						if (h < 20f) h = 20f;
						float w = h * 0.5f;
						float cx = (fs.x + hs.x) * 0.5f;
						var box = new Rect(cx - w * 0.5f, top, w, h);

						Color col = TrustColor(Core.ApiUsers.Get(api));

						// "Box fits the avatar" (EspMeshBody) REMOVED 2026-08-26. It measured the
						// avatar's real renderer bounds to reshape the box, but next to the outline
						// glow and the 3D capsule it told you nothing either of those did not, and it
						// cost a GetComponentsInChildren<Renderer> per player per frame.

						// BOX = a RECTANGLE, the way an ESP box is meant to look. It drew a capsule
						// pill before (DrawMeshCapsule), which is why "Box" produced a 3D-looking
						// capsule instead of squares — that is what the 3D Capsule option is for.
						// The two are different shapes now, so both can be on at once.
						if (ModConfig.EspBox.Value)
							DrawBoxOutline(box, col, 2f);

						// Skeleton ("mesh") ESP: bone-to-bone lines that follow the avatar's real
						// pose, instead of a flat capsule. Humanoid rigs only — GetBonePosition
						// returns zero on non-humanoid avatars, and those segments are skipped.
						// UNLIMITED distance like every other type now — the frustum cull already
						// skips anyone off screen, so a far skeleton only costs when it is in view.
						if (ModConfig.EspSkeleton.Value)
							DrawSkeleton(cam, api, col);

						if (ModConfig.EspName.Value)
						{
							string nm = SafeName(api);
							_label.normal.textColor = col;
							_label.alignment = TextAnchor.LowerCenter;
							GUI.Label(new Rect(box.x - 40f, box.y - 18f, box.width + 80f, 16f), nm, _label);

							// Anti-Block found this player's avatar force-hidden for us after having seen
							// it, which is what a block looks like from this side. Drawn on its own line
							// above the name, in red, so it cannot be mistaken for part of the name.
							if (AntiBlockModule.HasBlockedYou(nm))
							{
								_label.normal.textColor = new Color(1f, 0.29f, 0.29f);
								GUI.Label(new Rect(box.x - 60f, box.y - 33f, box.width + 120f, 16f), "HAS BLOCKED YOU", _label);
							}
						}

						if (ModConfig.EspDistance.Value)
						{
							_label.normal.textColor = col;
							_label.alignment = TextAnchor.UpperCenter;
							GUI.Label(new Rect(box.x - 40f, box.yMax + 2f, box.width + 80f, 16f), Mathf.RoundToInt(dist) + "m", _label);
						}
					}
					catch { /* one bad player must not abort the overlay */ }
				}

				GUI.color = Color.white;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[ESP] draw threw: {e}");
			}
		}

		// --- trust rank ---

		// CACHED PER APIUser. Every has*TrustLevel, hasSuperPowers and developerType read below is
		// a native getter (il2cpp_runtime_invoke plus a boxed return) — three to ten crossings per
		// player per frame, plus the string marshal IsRainbow pays for u.id when the rainbow list
		// is set — for a rank that changes essentially never. Keyed by the il2cpp pointer of the
		// APIUser that ApiUsers already hands back for a second (Boehm GC does not move objects);
		// the pointer is only a key, never dereferenced. The rainbow VERDICT is cached, the colour
		// itself is not, so the spectrum still cycles every frame.
		private sealed class TrustEntry { public Color C; public bool Rainbow; public float At; }
		private static readonly Dictionary<IntPtr, TrustEntry> _trust = new Dictionary<IntPtr, TrustEntry>();
		private const float TrustTtl = 1f;

		private static Color TrustColor(APIUser u)
		{
			try
			{
				if (u == null) return CVisitor;
				IntPtr key;
				try { key = u.Pointer; } catch { return TrustColorUncached(u); }
				float now = Time.realtimeSinceStartup;
				if (_trust.TryGetValue(key, out var e) && e != null && now - e.At < TrustTtl)
					return e.Rainbow ? Core.TrustKit.Spectrum() : e.C;
				bool rainbow = Core.TrustKit.IsRainbow(u);
				Color c = rainbow ? Core.TrustKit.Spectrum() : TrustColorUncached(u);
				if (_trust.Count > 512) _trust.Clear();   // players come and go; never let it grow unbounded
				_trust[key] = new TrustEntry { C = c, Rainbow = rainbow, At = now };
				return c;
			}
			catch { return CVisitor; }
		}

		private static Color TrustColorUncached(APIUser u)
		{
			try
			{
				if (u == null) return CVisitor;
				// The signature rule comes first, so the box and skeleton carry it too.
				if (Core.TrustKit.IsRainbow(u)) return Core.TrustKit.Spectrum();
				if (IsVrcTeam(u)) return CVrcTeam;                                    // VRChat staff first
				if (u.hasVeryNegativeTrustLevel || u.hasNegativeTrustLevel) return CNuisance;
				if (u.hasLegendTrustLevel || u.hasVeteranTrustLevel) return CTrusted;
				if (u.hasTrustedTrustLevel) return CKnown;
				if (u.hasKnownTrustLevel) return CUser;
				if (u.hasBasicTrustLevel) return CNewUser;
				return CVisitor;
			}
			catch { return CVisitor; }
		}

		// VRChat Team = elevated staff powers, or a developer type of Internal/Moderator
		// (DeveloperType: None=0, Trusted=1, Internal=2, Moderator=3).
		private static bool IsVrcTeam(APIUser u)
		{
			try { if (u.hasSuperPowers) return true; } catch { }
			try { if ((int)u.developerType >= 2) return true; } catch { }
			return false;
		}

		private void ResolveReflection()
		{
			if (_resolved) return;
			_resolved = true;
			try
			{
				_playerType = Assembly.Load("Assembly-CSharp").GetType("VRC.Player");
				if (_playerType != null)
				{
					_mApiUser = _playerType.GetProperties().FirstOrDefault(p => p.PropertyType == typeof(APIUser));
					// TryCast<VRC.Player> re-wraps a plain Component as the concrete type so the
					// APIUser property can actually be read (otherwise GetValue throws → null → all
					// boxes fall back to the Visitor colour, which is why nothing was coloured).
					_tryCastPlayer = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
						.GetMethod("TryCast", BindingFlags.Instance | BindingFlags.Public)
						?.MakeGenericMethod(_playerType);
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[ESP] trust reflection unavailable: {e.Message}"); }
		}

		// Best-effort: map a VRCPlayerApi back to its VRC.Player, then to APIUser.
		// Kept for callers; the actual resolution + caching lives in Core.ApiUsers so ESP,
		// the radar and the watchlist share one cached lookup instead of three uncached ones.
		private APIUser GetApiUser(VRCPlayerApi api) => Core.ApiUsers.Get(api);

		// --- drawing ---

		// ---------------------------------------------------------------- mesh bounds

		// Renderers are cached per player: GetComponentsInChildren walks the whole avatar, and doing
		// that for every player every frame is the kind of thing that quietly costs you 20 fps in a
		// full instance. Two seconds is short enough to follow an avatar change.
		private sealed class MeshCache { public Renderer[] R; public float At; }
		private static readonly Dictionary<int, MeshCache> _mesh = new Dictionary<int, MeshCache>();
		private const float MeshCacheSec = 2f;

		// The union of the avatar's visible renderers, in world space. Shared by the mesh box and
		// the mesh capsule so both measure the same thing.
		private static bool MeshBounds(VRCPlayerApi api, out Bounds bounds)
		{
			bounds = default;
			try
			{
				int id = api.playerId;
				float now = Time.realtimeSinceStartup;
				if (!_mesh.TryGetValue(id, out var mc) || mc == null || now - mc.At > MeshCacheSec)
				{
					Renderer[] rs = null;
					try
					{
						var go = api.gameObject;
						if (go != null) rs = go.GetComponentsInChildren<Renderer>(false);
					}
					catch { }
					mc = new MeshCache { R = rs, At = now };
					_mesh[id] = mc;
				}
				if (mc.R == null || mc.R.Length == 0) return false;

				bool any = false;
				foreach (var r in mc.R)
				{
					if (r == null || !r.enabled) continue;
					if (!any) { bounds = r.bounds; any = true; }
					else bounds.Encapsulate(r.bounds);
				}
				return any;
			}
			catch { return false; }
		}

		// CAPSULE, NOT A RECTANGLE.
		//
		// A screen-aligned box has corners that belong to nothing — in a crowded room the corners of
		// twenty boxes cross each other and the world behind them, and the result reads as a wire
		// mesh rather than as "there is a person there". A capsule has the shape of the thing it is
		// drawn around, so overlapping ones still read as separate people.
		//
		// The glow is three passes of the same outline: wide and faint underneath, narrow and full
		// on top. IMGUI has no stroke width or blur, so a neon line has to be built out of the only
		// primitive there is — a filled quad — and stacking is what makes it look lit rather than
		// merely thick.
		private const int ArcSegments = 14;

		// MESH CAPSULE — the real thing.
		//
		// Built from the avatar's renderer bounds in WORLD space: the axis runs from the bottom of
		// the mesh to the top, and the radius is the widest horizontal half-extent. Both ends are
		// projected to the screen, and the radius is measured by projecting a point one radius to
		// the camera's right — so it shrinks with distance exactly like the avatar does.
		//
		// Billboarded on purpose. A true capsule silhouette would need the outline that faces the
		// camera, which changes as you move around it; a capsule that always presents its side is
		// what every reference of this looks like, and it costs two projections instead of dozens.
		private bool DrawMeshCapsule(Camera cam, VRCPlayerApi api, Vector3 feet, Vector3 head, Color col)
		{
			try
			{
				Bounds b;
				if (!MeshBounds(api, out b))
				{
					// No renderers (avatar still loading, or hidden): a capsule from feet to head
					// with a plausible width is still better than nothing.
					float hgt = Mathf.Max(0.4f, head.y - feet.y);
					b = new Bounds((feet + head) * 0.5f, new Vector3(hgt * 0.34f, hgt, hgt * 0.34f));
				}

				Vector3 c = b.center, e = b.extents;
				// Width from the mesh, but CAPPED against the height. Renderer bounds include the
				// arms, so a T-posed or gesturing avatar has a horizontal half-extent close to its
				// full height — taken literally that produces a balloon, not a capsule. The cap
				// keeps the proportions of a body whatever the pose is doing.
				float rad = Mathf.Max(e.x, e.z);
				rad = Mathf.Clamp(rad, 0.05f, Mathf.Max(0.12f, e.y * 0.42f));
				Vector3 top = new Vector3(c.x, c.y + Mathf.Max(0f, e.y - rad), c.z);
				Vector3 bot = new Vector3(c.x, c.y - Mathf.Max(0f, e.y - rad), c.z);

				Vector3 ts = cam.WorldToScreenPoint(top);
				Vector3 bs = cam.WorldToScreenPoint(bot);
				if (ts.z <= 0f || bs.z <= 0f) return false;      // straddling the camera plane

				Vector3 edge = cam.WorldToScreenPoint(c + cam.transform.right * rad);
				Vector3 cs = cam.WorldToScreenPoint(c);
				float rPix = Mathf.Abs(edge.x - cs.x);
				if (rPix < 3f || rPix > Screen.width) return false;

				float tY = Screen.height - ts.y;
				float bY = Screen.height - bs.y;
				if (bY < tY) { var s = tY; tY = bY; bY = s; }    // camera below the player
				float cxScreen = (ts.x + bs.x) * 0.5f;

				// A PILL is a rounded rect whose corner radius is half its width — so the whole
				// capsule is TWO GuiKit calls (a faint wide halo, then the bright rim) instead of
				// the ~90 rotated quads DrawCapsuleAxis emitted per player. That draw cost was the
				// single biggest thing in the mod's profiler; this is the same look for a fraction
				// of it.
				var pill = new Rect(cxScreen - rPix, tY - rPix, rPix * 2f, (bY - tY) + rPix * 2f);
				GuiKit.RoundedBorder(pill, new Color(0f, 0f, 0f, 0f),
					new Color(col.r, col.g, col.b, 0.18f), rPix, 5f);
				GuiKit.RoundedBorder(pill, new Color(0f, 0f, 0f, 0f), col, rPix, 1.8f);
				return true;
			}
			catch { return false; }
		}

		// Straight sides plus a cap at each end, around a vertical axis.
		private static void DrawCapsuleAxis(Vector2 top, Vector2 bot, float r, Color c, float t)
		{
			DrawLine(new Vector2(top.x - r, top.y), new Vector2(bot.x - r, bot.y), c, t);
			DrawLine(new Vector2(top.x + r, top.y), new Vector2(bot.x + r, bot.y), c, t);

			Vector2 pT = new Vector2(top.x - r, top.y), pB = new Vector2(bot.x - r, bot.y);
			for (int i = 1; i <= ArcSegments; i++)
			{
				float a = Mathf.PI * i / ArcSegments;
				float dx = -Mathf.Cos(a) * r, dy = Mathf.Sin(a) * r;
				var nT = new Vector2(top.x + dx, top.y - dy);
				DrawLine(pT, nT, c, t); pT = nT;
				var nB = new Vector2(bot.x + dx, bot.y + dy);
				DrawLine(pB, nB, c, t); pB = nB;
			}
		}

		private static void DrawCapsule(Rect r, Color c)
		{
			// Three passes, widest first, so the bright core lands on top of its own halo.
			DrawCapsuleOutline(r, new Color(c.r, c.g, c.b, 0.14f), 6f);
			DrawCapsuleOutline(r, new Color(c.r, c.g, c.b, 0.34f), 3f);
			DrawCapsuleOutline(r, new Color(c.r, c.g, c.b, 1.00f), 1.6f);
		}

		private static void DrawCapsuleOutline(Rect r, Color c, float t)
		{
			try
			{
				float rad = Mathf.Min(r.width * 0.5f, r.height * 0.5f);
				if (rad < 1f) return;
				float cx = r.x + r.width * 0.5f;
				float topY = r.y + rad;
				float botY = r.yMax - rad;

				// Straight sides, only where the caps do not already cover the height. A very short
				// box is all cap and no side, and drawing a negative-length side flips the quad.
				if (botY > topY)
				{
					DrawLine(new Vector2(r.x, topY), new Vector2(r.x, botY), c, t);
					DrawLine(new Vector2(r.xMax, topY), new Vector2(r.xMax, botY), c, t);
				}

				// Caps as short chords. Fourteen segments is smooth at the size a player occupies on
				// screen and stays cheap when twenty of them are on at once.
				Vector2 prevT = new Vector2(r.x, topY), prevB = new Vector2(r.x, botY);
				for (int i = 1; i <= ArcSegments; i++)
				{
					float a = Mathf.PI * i / ArcSegments;
					float dx = -Mathf.Cos(a) * rad;
					float dy = Mathf.Sin(a) * rad;

					var nowT = new Vector2(cx + dx, topY - dy);
					DrawLine(prevT, nowT, c, t);
					prevT = nowT;

					var nowB = new Vector2(cx + dx, botY + dy);
					DrawLine(prevB, nowB, c, t);
					prevB = nowB;
				}
			}
			catch { }
		}

		private static void DrawBoxOutline(Rect r, Color c, float t)
		{
			GUI.color = c;
			GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), GuiKit.Pixel);            // top
			GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), GuiKit.Pixel);     // bottom
			GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), GuiKit.Pixel);           // left
			GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), GuiKit.Pixel);    // right
			GUI.color = Color.white;
		}


		// The humanoid segments we connect. Fingers/toes are deliberately left out: they add a
		// lot of lines for no readability at ESP distances.
		private static readonly HumanBodyBones[,] Bones =
		{
			{ HumanBodyBones.Head,          HumanBodyBones.Neck },
			{ HumanBodyBones.Neck,          HumanBodyBones.Chest },
			{ HumanBodyBones.Chest,         HumanBodyBones.Spine },
			{ HumanBodyBones.Spine,         HumanBodyBones.Hips },
			{ HumanBodyBones.Neck,          HumanBodyBones.LeftUpperArm },
			{ HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm },
			{ HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand },
			{ HumanBodyBones.Neck,          HumanBodyBones.RightUpperArm },
			{ HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm },
			{ HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand },
			{ HumanBodyBones.Hips,          HumanBodyBones.LeftUpperLeg },
			{ HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg },
			{ HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot },
			{ HumanBodyBones.Hips,          HumanBodyBones.RightUpperLeg },
			{ HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg },
			{ HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot },
		};

		// Distinct bones referenced by Bones[,] — each is fetched and projected ONCE per player.
		// Doing it per segment meant Hips/Neck were resolved and projected three times each.
		private static readonly HumanBodyBones[] BoneList =
		{
			HumanBodyBones.Head, HumanBodyBones.Neck, HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips,
			HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
			HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
			HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
			HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
		};
		private static readonly Dictionary<HumanBodyBones, int> BoneIndex = BuildBoneIndex();
		private static readonly Vector2[] Proj = new Vector2[BoneList.Length];   // reused, no per-frame alloc
		private static readonly bool[] ProjOk = new bool[BoneList.Length];

		private static Dictionary<HumanBodyBones, int> BuildBoneIndex()
		{
			var d = new Dictionary<HumanBodyBones, int>(BoneList.Length);
			for (int i = 0; i < BoneList.Length; i++) d[BoneList[i]] = i;
			return d;
		}

		// Bone-to-bone wireframe of a player, projected to screen space.
		private static void DrawSkeleton(Camera cam, VRCPlayerApi api, Color col)
		{
			try
			{
				// The centre of the player, to sanity-check bones against. Broken or joke avatars
				// return bone positions metres away from the body — a hand at the world origin, a
				// head flung across the room — and a line drawn to one of those is the long streak
				// that tangles the ESP. Any bone more than 3m from the player's own position is not
				// a real bone on that body and is dropped.
				Vector3 centre;
				try { centre = api.GetPosition() + Vector3.up; } catch { centre = Vector3.zero; }
				const float maxBoneDist = 3f;

				// ONE Screen.height PER SKELETON. It is a native static getter (il2cpp_runtime_invoke
				// plus a boxed int) and it was read once per bone and twice per segment — ~49 crossings
				// per player per frame for a number that cannot change mid-frame.
				float screenH = Screen.height;
				float maxSegSq = (screenH * 0.33f) * (screenH * 0.33f);

				for (int i = 0; i < BoneList.Length; i++)
				{
					ProjOk[i] = false;
					Vector3 w;
					try { w = api.GetBonePosition(BoneList[i]); } catch { continue; }
					if (w == Vector3.zero) continue;                 // bone absent on this rig
					if (centre != Vector3.zero && (w - centre).sqrMagnitude > maxBoneDist * maxBoneDist)
						continue;                                    // glitched bone flung off the body
					Vector3 s = cam.WorldToScreenPoint(w);
					if (s.z <= 0f) continue;                         // behind the camera
					Proj[i] = new Vector2(s.x, screenH - s.y);
					ProjOk[i] = true;
				}

				// SHARED GUI STATE HOISTED OUT OF THE LINE LOOP. All 16 segments use one colour, one
				// base matrix and one texture, but DrawLine paid a GUI.matrix get, two GUI.color sets
				// and a GuiKit.Pixel (a UnityEngine.Object null-compare, itself a native call) per
				// segment — five of its seven crossings for state that does not change between lines.
				// Each segment is still the same 1×len quad rotated about the same pivot, drawn in the
				// same order, so the pixels are identical; the finally restores the matrix even if a
				// draw throws, which DrawLine did not.
				Matrix4x4 saved = GUI.matrix;
				Texture2D px = GuiKit.Pixel;
				GUI.color = col;
				try
				{
					int n = Bones.GetLength(0);
					for (int i = 0; i < n; i++)
					{
						if (!BoneIndex.TryGetValue(Bones[i, 0], out int ia)) continue;
						if (!BoneIndex.TryGetValue(Bones[i, 1], out int ib)) continue;
						if (!ProjOk[ia] || !ProjOk[ib]) continue;
						// A real bone segment on screen is short. If two projected bones are more than a
						// third of the screen apart, one of them is a bad projection — skip the segment
						// rather than draw a streak across the view.
						Vector2 a = Proj[ia], d = Proj[ib] - a;
						if (d.sqrMagnitude > maxSegSq) continue;
						// Same quad DrawLine emits (width 1.6, centred on the segment), same guards.
						float len = d.magnitude;
						if (len < 0.5f || len > 6000f) continue;
						GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
						GUI.DrawTexture(new Rect(a.x, a.y - 0.8f, len, 1.6f), px);
						GUI.matrix = saved;
					}
				}
				finally
				{
					GUI.matrix = saved;
					GUI.color = Color.white;
				}
			}
			catch { }
		}

		// IMGUI has no line primitive: draw a 1×len quad and rotate it about its start point.
		private static void DrawLine(Vector2 a, Vector2 b, Color c, float w)
		{
			Vector2 d = b - a;
			float len = d.magnitude;
			if (len < 0.5f || len > 6000f) return;                          // degenerate / absurd
			float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;

			Matrix4x4 saved = GUI.matrix;
			GUI.color = c;
			GUIUtility.RotateAroundPivot(ang, a);
			GUI.DrawTexture(new Rect(a.x, a.y - w * 0.5f, len, w), GuiKit.Pixel);
			GUI.matrix = saved;
			GUI.color = Color.white;
		}

		private void EnsureStyle()
		{
			if (_label != null) return;
			_label = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.LowerCenter };
		}

		// CACHED. displayName is an interop call across the managed/native boundary, and it was paid
		// once per player per repaint — 40 players in a busy instance is 40 crossings a frame for a
		// string that changes when somebody joins, i.e. essentially never.
		private sealed class NameEntry { public string N; public float At; }
		private static readonly Dictionary<int, NameEntry> _names = new Dictionary<int, NameEntry>();
		private const float NameTtl = 5f;

		private static string SafeName(VRCPlayerApi api)
		{
			try
			{
				int id = api.playerId;
				float now = Time.realtimeSinceStartup;
				if (_names.TryGetValue(id, out var e) && e != null && now - e.At < NameTtl) return e.N;
				string n;
				try { n = api.displayName ?? "?"; } catch { n = "?"; }
				_names[id] = new NameEntry { N = n, At = now };
				return n;
			}
			catch { return "?"; }
		}
	}
}
