using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Killiorim.Core;

namespace Killiorim.Modules
{
	// RIGHT-WING PANEL: avatar card preview instead of a raw event log. The goal is to keep the
	// wing feel but make the panel feel like part of the avatar preview / wardrobe area rather than
	// a debug feed. The original event data still exists elsewhere; this panel focuses on the avatar
	// you are currently wearing and a small summary readout.
	public class WingLogModule : IModule
	{
		public override string Name => "WingLog";

		private PanelSkin.Panel _panel;
		private float _nextTry;
		private int _fails;
		private Camera _previewCamera;
		private RenderTexture _previewTexture;
		private RawImage _previewImage;
		private GameObject _previewAvatar;
		private GameObject _previewSource;
		private string _previewAvatarId;
		private int _previewLayer;
		private float _nextPreviewRefresh;

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.WingPlayersEnabled.Value) { Drop(); return; }

				if (_panel == null || !_panel.Alive)
				{
					_panel = null;
					float now = Time.realtimeSinceStartup;
					if (now < _nextTry) return;
					_nextTry = now + 3f;
					if (_fails > 20) return;
					if (!TryBuild()) { _fails++; return; }
					_fails = 0;
				}

				_panel.Follow();
				if (Time.realtimeSinceStartup >= _nextPreviewRefresh)
				{
					_nextPreviewRefresh = Time.realtimeSinceStartup + 0.25f;
					Refresh();
				}
			}
			catch { }
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			if (_panel == null || !_panel.Alive) { _panel = null; _fails = 0; _nextTry = 0f; }
		}

		public override void OnShutdown() { Drop(); }

		private void Drop()
		{
			try { if (_panel != null && _panel.Root != null) UnityEngine.Object.Destroy(_panel.Root.gameObject); } catch { }
			try { if (_previewCamera != null) UnityEngine.Object.Destroy(_previewCamera.gameObject); } catch { }
			try { if (_previewTexture != null) _previewTexture.Release(); } catch { }
			_previewCamera = null;
			_previewTexture = null;
			_previewImage = null;
			if (_previewAvatar != null) UnityEngine.Object.Destroy(_previewAvatar);
			_previewAvatar = null;
			_previewSource = null;
			_previewAvatarId = null;
			_panel = null;
		}

		private bool TryBuild()
		{
			var p = Core.SidePanel.Build(false, "VA_AvatarPanel", "AVATAR", "va_panel_log.jpg");
			if (p == null) return false;
			PanelSkin.SetHeadings(p, "INFO", "DETAIL", null, "STATUS");
			BuildPreview(p);
			_panel = p;
			Killiorim.Logger.LogInfo("[WingLog] avatar preview panel built beside the right wing.");
			return true;
		}

		private void BuildPreview(PanelSkin.Panel p)
		{
			if (p == null || p.Root == null) return;

			var previewHolder = new GameObject("VA_AvatarPreviewHolder");
			previewHolder.AddComponent<RectTransform>();
			previewHolder.transform.SetParent(p.Root, false);
			var rt = previewHolder.GetComponent<RectTransform>();
			rt.anchorMin = new Vector2(0.5f, 1f);
			rt.anchorMax = new Vector2(0.5f, 1f);
			rt.pivot = new Vector2(0.5f, 1f);
			rt.anchoredPosition = new Vector2(0f, -88f);
			rt.sizeDelta = new Vector2(300f, 250f);

			var bg = previewHolder.AddComponent<Image>();
			bg.color = new Color(0.06f, 0.06f, 0.08f, 0.96f);
			bg.raycastTarget = false;
			var border = previewHolder.AddComponent<Outline>();
			border.effectColor = new Color(0.96f, 0.82f, 0.88f, 1f);
			border.effectDistance = new Vector2(2f, 2f);

			var raw = new GameObject("VA_AvatarRaw");
			raw.AddComponent<RectTransform>();
			raw.transform.SetParent(previewHolder.transform, false);
			var rawRt = raw.GetComponent<RectTransform>();
			rawRt.anchorMin = new Vector2(0f, 0f);
			rawRt.anchorMax = new Vector2(1f, 1f);
			rawRt.offsetMin = new Vector2(8f, 8f);
			rawRt.offsetMax = new Vector2(-8f, -8f);
			_previewImage = raw.AddComponent<RawImage>();
			_previewImage.raycastTarget = false;
			_previewImage.color = new Color(1f, 1f, 1f, 1f);

			_previewTexture = new RenderTexture(512, 512, 24, RenderTextureFormat.ARGB32);
			_previewTexture.filterMode = FilterMode.Bilinear;
			_previewTexture.Create();
			_previewImage.texture = _previewTexture;
			_previewImage.enabled = true;

			_previewLayer = LayerMask.NameToLayer("Ignore Raycast");
			if (_previewLayer < 0) _previewLayer = 31;

			var camObj = new GameObject("VA_AvatarPreviewCamera");
			camObj.transform.position = new Vector3(0f, 1.15f, 2.05f);
			camObj.transform.rotation = Quaternion.Euler(18f, 180f, 0f);
			_previewCamera = camObj.AddComponent<Camera>();
			_previewCamera.enabled = false;
			_previewCamera.clearFlags = CameraClearFlags.SolidColor;
			_previewCamera.backgroundColor = new Color(0.06f, 0.06f, 0.08f, 1f);
			_previewCamera.fieldOfView = 24f;
			_previewCamera.nearClipPlane = 0.1f;
			_previewCamera.farClipPlane = 16f;
			_previewCamera.targetTexture = _previewTexture;
			_previewCamera.cullingMask = 1 << _previewLayer;
			_previewCamera.allowHDR = false;
			_previewCamera.allowMSAA = false;
			_previewCamera.depth = 10f;
		}

		private void Refresh()
		{
			if (_panel == null) return;

			string model = "No avatar loaded";
			string state = "Waiting";
			string avatarId = VaTagsModule.LocalAvatarId() ?? "";
			var api = PlayerRef.LocalApi();
			if (api != null)
				state = "Ready";

			var avatar = ResolveLocalAvatarRoot();
			string avatarName = VaTagsModule.LocalAvatarName();
			if (!string.IsNullOrEmpty(avatarName)) model = avatarName;
			else if (avatar != null) model = avatar.name;
			if (model.Length > 22) model = model.Substring(0, 22) + "…";
			string idText = string.IsNullOrEmpty(avatarId) ? "Waiting for avatar ID" : avatarId;
			if (idText.Length > 26) idText = idText.Substring(0, 12) + "…" + idText.Substring(idText.Length - 10);
			long bytes = EstimateLoadedBytes(avatar);
			string size = bytes > 0 ? "~" + (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB est." : "Not available";

			var rows = new List<PanelSkin.Row>
			{
				new PanelSkin.Row { Id = PanelSkin.Tag(PanelSkin.HexDim, "MODEL"), Name = PanelSkin.Tag(PanelSkin.HexText, model), Pos = "", Badge = PanelSkin.Tag("FF7B7B", "<b>LIVE</b>") },
				new PanelSkin.Row { Id = PanelSkin.Tag(PanelSkin.HexDim, "AVATAR ID"), Name = PanelSkin.Tag("FF8E8E", idText), Pos = "", Badge = PanelSkin.Tag("D7A7AA", "<b>ID</b>") },
				new PanelSkin.Row { Id = PanelSkin.Tag(PanelSkin.HexDim, "LOADED SIZE"), Name = PanelSkin.Tag("72F7C1", size), Pos = "", Badge = PanelSkin.Tag("D7A7AA", "<b>EST</b>") },
				new PanelSkin.Row { Id = PanelSkin.Tag(PanelSkin.HexDim, "STATE"), Name = PanelSkin.Tag(PanelSkin.HexText, state), Pos = "", Badge = PanelSkin.Tag("72F7C1", "<b>READY</b>") },
			};
			_panel.SetRows(rows);
			_panel.SetCount("4");
			TickPreview(avatarId);
		}

		private void TickPreview(string avatarId)
		{
			if (_previewCamera == null || _previewImage == null) return;
			var avatar = ResolveLocalAvatarRoot();
			if (avatar == null)
			{
				_previewImage.enabled = false;
				return;
			}

			if (_previewAvatar == null || _previewAvatarId != avatarId || _previewSource != avatar)
			{
				CreatePreviewAvatar(avatar);
				_previewSource = avatar;
				_previewAvatarId = avatarId;
			}
			if (_previewAvatar == null) return;

			Bounds bounds = new Bounds(_previewAvatar.transform.position, Vector3.zero);
			var renderers = _previewAvatar.GetComponentsInChildren<Renderer>(true);
			for (int i = 0; i < renderers.Length; i++)
			{
				if (renderers[i] != null && renderers[i].enabled) bounds.Encapsulate(renderers[i].bounds);
			}
			if (bounds.size == Vector3.zero)
			{
				_previewImage.enabled = false;
				return;
			}

			float radius = Mathf.Max(0.45f, bounds.extents.magnitude);
			float distance = Mathf.Max(1.4f, radius * 2.6f);
			Vector3 center = bounds.center;
			_previewCamera.transform.position = center + new Vector3(0f, Mathf.Max(0.5f, radius * 0.85f), distance);
			_previewCamera.transform.LookAt(center + new Vector3(0f, Mathf.Max(0.4f, radius * 0.7f), 0f));
			_previewCamera.Render();
			_previewImage.enabled = true;
		}

		private GameObject ResolveLocalAvatarRoot()
		{
			try
			{
				var local = PlayerRef.LocalPlayer();
				if (local == null) return null;
				Renderer first = null;
				foreach (var r in local.gameObject.GetComponentsInChildren<Renderer>(true))
				{
					if (r == null) continue;
					if (r is SkinnedMeshRenderer || r is MeshRenderer)
					{
						first = r;
						break;
					}
				}
				return first != null ? first.transform.root.gameObject : null;
			}
			catch { return null; }
		}

		private static long EstimateLoadedBytes(GameObject root)
		{
			if (root == null) return 0;
			long bytes = 0;
			var seen = new HashSet<int>();
			try
			{
				var renderers = root.GetComponentsInChildren<Renderer>(true);
				for (int i = 0; i < renderers.Length; i++)
				{
					var renderer = renderers[i];
					if (renderer == null) continue;
					Mesh mesh = null;
					var skinned = renderer as SkinnedMeshRenderer;
					var filter = renderer.GetComponent<MeshFilter>();
					if (skinned != null) mesh = skinned.sharedMesh;
					else if (filter != null) mesh = filter.sharedMesh;
					if (mesh != null && seen.Add(mesh.GetInstanceID()))
					{
						long meshBytes = (long)mesh.vertexCount * 32L;
						for (int sub = 0; sub < mesh.subMeshCount; sub++) meshBytes += (long)mesh.GetIndexCount(sub) * 4L;
						bytes += meshBytes;
					}

					var materials = renderer.sharedMaterials;
					for (int m = 0; materials != null && m < materials.Length; m++)
					{
						var material = materials[m];
						if (material == null) continue;
						var properties = material.GetTexturePropertyNames();
						for (int t = 0; properties != null && t < properties.Length; t++)
						{
							var texture = material.GetTexture(properties[t]);
							if (texture == null || !seen.Add(texture.GetInstanceID())) continue;
							bytes += (long)texture.width * texture.height * 4L * 4L / 3L;
						}
					}
				}
			}
			catch { }
			return bytes;
		}

		private void CreatePreviewAvatar(GameObject source)
		{
			if (source == null) return;
			if (_previewAvatar != null) UnityEngine.Object.Destroy(_previewAvatar);
			_previewAvatar = UnityEngine.Object.Instantiate(source);
			_previewAvatar.name = source.name + "_KilliorimPreview";
			_previewAvatar.transform.SetParent(null, false);
			_previewAvatar.transform.localScale = Vector3.one * 1.1f;
			_previewAvatar.transform.position = Vector3.zero;
			_previewAvatar.transform.rotation = Quaternion.identity;
			foreach (var r in _previewAvatar.GetComponentsInChildren<Renderer>(true))
			{
				if (r == null) continue;
				r.gameObject.layer = _previewLayer;
			}
			foreach (var c in _previewAvatar.GetComponentsInChildren<Collider>(true))
			{
				if (c != null) c.enabled = false;
			}
			foreach (var a in _previewAvatar.GetComponentsInChildren<Animator>(true))
			{
				if (a != null) a.enabled = false;
			}
		}
	}
}
