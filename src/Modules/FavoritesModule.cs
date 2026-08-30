using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// ARCHIVE FAVOURITES — the user's unlimited favourites, in game.
	//
	// Three layers, deliberately separate:
	//   1. the LIST (ids + the name the server stored)
	//   2. the METADATA (real name, author, thumbnail url) resolved in batches of 100
	//   3. the THUMBNAIL itself, fetched only for cards actually on screen and kept on disk
	//
	// A favourites list you cannot look at is a wall of avtr_ strings, which is what this was
	// before: layers 2 and 3 exist so a card can show the avatar instead of its id.
	//
	// Everything goes through the desktop client's bridge, so the account session never reaches
	// this process — including the thumbnails, which the server fetches and caches on our behalf.
	public class FavoritesModule : IModule
	{
		public override string Name => "Favorites";

		public sealed class Fav
		{
			public string Id;
			public string Name;          // server-stored name, replaced by the real one once resolved
			public string Author = "";
			public string Image = "";    // full-size image url from the avatar page
			public bool MetaDone;

			public Texture2D Thumb;
			// 0 not asked · 1 fetching · 2 ready · 3 unavailable
			public int ThumbState;
		}

		private static readonly List<Fav> Items = new List<Fav>();
		private static readonly List<string> Ids = new List<string>();
		private static readonly Dictionary<string, Fav> ById = new Dictionary<string, Fav>(StringComparer.OrdinalIgnoreCase);
		private static readonly object Gate = new object();

		public static string LastStatus = "";

		// Bumped on every change to the list. Anything DISPLAYING these favourites watches this and
		// redraws when it moves — without it, adding or removing left the native grid showing the
		// list as it was when the category was last selected.
		public static int Revision { get; private set; }
		private static void Bump() { unchecked { Revision++; } }
		public static bool Loaded { get; private set; }
		public static int Count { get { lock (Gate) return Ids.Count; } }

		private float _nextRefresh;
		private static bool _busy;

		public static List<string> Snapshot() { lock (Gate) return new List<string>(Ids); }
		public static List<Fav> Favourites() { lock (Gate) return new List<Fav>(Items); }
		public static bool Has(string id)
		{
			if (string.IsNullOrEmpty(id)) return false;
			lock (Gate) return ById.ContainsKey(id);
		}

		public override void OnUpdate()
		{
			try
			{
				// Only meaningful inside the desktop client, and only once it is signed in.
				if (!VaAuth.InsideClient) return;

				// Decoding happens here because creating a Texture2D is main-thread only. A couple
				// per frame: a full grid arriving at once would otherwise be a visible hitch.
				DrainThumbs(2);

				float now = Time.realtimeSinceStartup;
				if (now >= _nextMeta) { _nextMeta = now + 1f; PumpMeta(); }

				if (now < _nextRefresh) return;
				_nextRefresh = now + (Loaded ? 120f : 15f);
				_ = RefreshAsync();
			}
			catch { }
		}

		// ---------------------------------------------------------------- list

		public static async System.Threading.Tasks.Task RefreshAsync()
		{
			if (_busy) return;
			_busy = true;
			try
			{
				var (ok, raw, status) = await VaAuth.FavAsync("list", "");
				if (!ok) { LastStatus = Error(raw) ?? ("could not read favourites (" + status + ")"); return; }

				var found = new List<Fav>();
				try
				{
					using var doc = JsonDocument.Parse(raw);
					Collect(doc.RootElement, found);
				}
				catch (Exception e) { LastStatus = "favourites: bad response (" + e.Message + ")"; return; }

				lock (Gate)
				{
					// Merge rather than replace: a refresh every two minutes must not throw away the
					// resolved names and the already-decoded thumbnails.
					var kept = new Dictionary<string, Fav>(ById, StringComparer.OrdinalIgnoreCase);
					Items.Clear(); Ids.Clear(); ById.Clear();
					foreach (var f in found)
					{
						if (ById.ContainsKey(f.Id)) continue;
						Fav use = kept.TryGetValue(f.Id, out var old) ? old : f;
						if (use != f && !use.MetaDone && !string.IsNullOrEmpty(f.Name)) use.Name = f.Name;
						Ids.Add(use.Id); Items.Add(use); ById[use.Id] = use;
					}
				}
				Loaded = true;
				Bump();
				LastStatus = Count + " Archive favourite(s)";
				VRChatArchiveModPlugin.Logger.LogInfo($"[Favorites] {Count} avatar favourite(s) from the Archive.");
			}
			catch (Exception e) { LastStatus = "favourites failed: " + e.Message; }
			finally { _busy = false; }
		}

		// The list endpoint's exact shape is the server's business and has changed before, so rather
		// than bind to one layout this walks the JSON and takes every avtr_ string it finds. A
		// favourite IS an id; anything else in the payload is decoration.
		private static void Collect(JsonElement e, List<Fav> outp)
		{
			switch (e.ValueKind)
			{
				case JsonValueKind.Object:
					// An {id, name} pair is the shape the endpoint actually returns, and taking both
					// together is what lets the list show a name instead of a wall of avtr_ strings.
					if (e.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
					{
						string id = idEl.GetString();
						if (!string.IsNullOrEmpty(id) && id.StartsWith("avtr_", StringComparison.Ordinal))
						{
							string nm = e.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
								? nEl.GetString() : null;
							outp.Add(new Fav { Id = id, Name = string.IsNullOrWhiteSpace(nm) ? id : nm });
							return;
						}
					}
					foreach (var p in e.EnumerateObject()) Collect(p.Value, outp);
					break;
				case JsonValueKind.Array:
					foreach (var c in e.EnumerateArray()) Collect(c, outp);
					break;
				case JsonValueKind.String:
					// Bare id, for a response shape that carries no names.
					string v = e.GetString();
					if (!string.IsNullOrEmpty(v) && v.StartsWith("avtr_", StringComparison.Ordinal))
						outp.Add(new Fav { Id = v, Name = v });
					break;
			}
		}

		// ---------------------------------------------------------------- metadata

		private static float _nextMeta;
		private static bool _metaBusy;
		public static int MetaPending { get; private set; }

		// One batch per second, 100 at a time. The server resolves them in parallel and keeps a
		// persistent cache, so this is one round trip per hundred favourites and nearly free after
		// the first time.
		private static void PumpMeta()
		{
			if (_metaBusy || !Loaded) return;
			var batch = new List<Fav>(100);
			int pending = 0;
			lock (Gate)
			{
				foreach (var f in Items)
				{
					if (f.MetaDone) continue;
					pending++;
					if (batch.Count < 100) batch.Add(f);
				}
			}
			MetaPending = pending;
			if (batch.Count == 0) return;
			_metaBusy = true;
			_ = ResolveAsync(batch);
		}

		private static async System.Threading.Tasks.Task ResolveAsync(List<Fav> batch)
		{
			try
			{
				var sb = new StringBuilder("{\"action\":\"meta\",\"ids\":[");
				for (int i = 0; i < batch.Count; i++)
				{
					if (i > 0) sb.Append(',');
					sb.Append('"').Append(batch[i].Id).Append('"');
				}
				sb.Append("]}");

				var (ok, raw, status) = await VaAuth.FavRawAsync(sb.ToString());
				if (!ok)
				{
					// Marked done anyway: a failing batch retried every second forever would hammer
					// the bridge for as long as the menu is open. The next full refresh retries.
					foreach (var f in batch) f.MetaDone = true;
					VRChatArchiveModPlugin.Logger.LogWarning("[Favorites] metadata batch failed (" + status + ")");
					return;
				}

				using var doc = JsonDocument.Parse(raw);
				if (doc.RootElement.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Object)
				{
					foreach (var p in res.EnumerateObject())
					{
						string key = p.Name;                       // "avatar:avtr_xxx"
						int c = key.IndexOf(':');
						string id = c >= 0 ? key.Substring(c + 1) : key;
						Fav f;
						lock (Gate) { ById.TryGetValue(id, out f); }
						if (f == null) continue;

						string title = Str(p.Value, "title");
						string image = Str(p.Value, "image");
						string author = Str(p.Value, "authorName");
						SplitTitle(ref title, ref author);
						if (!string.IsNullOrWhiteSpace(title)) f.Name = title;
						if (!string.IsNullOrWhiteSpace(author)) f.Author = author;
						if (!string.IsNullOrWhiteSpace(image)) f.Image = image;
					}
				}
				foreach (var f in batch) f.MetaDone = true;
			}
			catch (Exception e)
			{
				foreach (var f in batch) f.MetaDone = true;
				VRChatArchiveModPlugin.Logger.LogWarning("[Favorites] metadata: " + e.Message);
			}
			finally { _metaBusy = false; }
		}

		private static string Str(JsonElement e, string prop)
			=> e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

		// The avatar page's og:title is "Name by Author", while the author is scraped separately out
		// of og:description — and that second one is missing whenever the description is not phrased
		// the way the scraper expects. So a card showed "Particle Kon! by BananasaurusRex" on its
		// name line and an empty "By:" underneath, while its neighbour showed both correctly.
		//
		// The author is right there in the title either way. Split on the LAST " by ", because
		// avatar names contain the word themselves often enough to make the first one wrong.
		// The author arrives with VRChat's page boilerplate glued to it. Its og:description reads
		// "<name> by <author> - an avatar on VRChat. Click to view this avatar.", and the scraper
		// takes everything after "by", so the author came through as
		// "Kaichi Sama - an avatar on VRChat. Click to view this avatar."
		// Fixed at the source too, but the server's OG cache is persistent — everything already
		// scraped still carries it, so it is cleaned here as well.
		private static readonly string[] Boilerplate =
		{
			" - an avatar on VRChat", " - an avatar on vrchat", " on VRChat. Click", ". Click to view",
		};

		private static string CleanAuthor(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return "";
			foreach (string b in Boilerplate)
			{
				int i = s.IndexOf(b, StringComparison.OrdinalIgnoreCase);
				if (i > 0) s = s.Substring(0, i);
			}
			return s.Trim().TrimEnd('-', '.', ',').Trim();
		}

		private static void SplitTitle(ref string title, ref string author)
		{
			try
			{
				author = CleanAuthor(author);
				if (string.IsNullOrWhiteSpace(title)) return;
				const string sep = " by ";

				// Author already known: just take the duplicate off the end of the name.
				if (!string.IsNullOrWhiteSpace(author))
				{
					string tail = sep + author;
					if (title.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
						title = title.Substring(0, title.Length - tail.Length).TrimEnd();
					return;
				}

				int i = title.LastIndexOf(sep, StringComparison.Ordinal);
				if (i <= 0) return;
				string left = title.Substring(0, i).TrimEnd();
				string right = title.Substring(i + sep.Length).Trim();
				if (left.Length == 0 || right.Length == 0) return;
				title = left;
				author = right;
			}
			catch { }
		}

		// ---------------------------------------------------------------- thumbnails

		private static readonly ConcurrentQueue<(Fav fav, byte[] data)> Decoded = new ConcurrentQueue<(Fav, byte[])>();
		private static int _inflight;
		private const int MaxInflight = 4;

		private static string ThumbDir
		{
			get
			{
				string d = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "thumbs");
				try { Directory.CreateDirectory(d); } catch { }
				return d;
			}
		}

		// Asks for one card's thumbnail. Called from the menu while drawing, so it only ever fires
		// for cards the user can actually see — 150 favourites do not mean 150 downloads.
		public static void RequestThumb(Fav f)
		{
			if (f == null || f.ThumbState != 0) return;
			if (!f.MetaDone || string.IsNullOrEmpty(f.Image)) return;   // nothing to fetch yet
			if (_inflight >= MaxInflight) return;
			f.ThumbState = 1;
			_inflight++;
			_ = FetchThumbAsync(f);
		}

		private static async System.Threading.Tasks.Task FetchThumbAsync(Fav f)
		{
			byte[] data = null;
			try
			{
				string cache = Path.Combine(ThumbDir, f.Id + ".img");
				try { if (File.Exists(cache)) data = File.ReadAllBytes(cache); } catch { }

				if (data == null || data.Length < 100)
				{
					// Small variant first. The page's og:image is the FULL-SIZE original (~1200x900):
					// a hundred of those decoded into textures is hundreds of megabytes of VRAM for a
					// grid of 200px cards, so the resized endpoint is what we ask for.
					data = await GetImageAsync(Small(f.Image));
					if (data == null) data = await GetImageAsync(f.Image);
					if (data != null && data.Length >= 100)
					{
						try { File.WriteAllBytes(cache, data); } catch { }
					}
				}
			}
			catch { }
			finally { Decoded.Enqueue((f, data)); }
		}

		private static async System.Threading.Tasks.Task<byte[]> GetImageAsync(string url)
		{
			try
			{
				if (string.IsNullOrEmpty(url)) return null;
				string body = "{\"action\":\"image\",\"url\":\"" + url.Replace("\"", "") + "\"}";
				var (ok, raw, _) = await VaAuth.FavRawAsync(body);
				if (!ok) return null;
				using var doc = JsonDocument.Parse(raw);
				if (!doc.RootElement.TryGetProperty("b64", out var b) || b.ValueKind != JsonValueKind.String) return null;
				return Convert.FromBase64String(b.GetString());
			}
			catch { return null; }
		}

		// Rewrite any VRChat asset url to its 256px variant:
		//   /api/1/file/file_xxx/1/file    -> /api/1/image/file_xxx/1/256
		//   /api/1/image/file_xxx/1/1024   -> /api/1/image/file_xxx/1/256
		// Both forms occur in the wild. Handling only the first is why a third of the cached
		// thumbnails came down at 1200x900: the rewrite silently returned the url unchanged and we
		// fetched the original.
		// Same host either way, so the client's and the server's url guards both still apply.
		private static string Small(string image)
		{
			try
			{
				foreach (string marker in new[] { "/api/1/file/", "/api/1/image/" })
				{
					int i = image.IndexOf(marker, StringComparison.Ordinal);
					if (i < 0) continue;
					string[] parts = image.Substring(i + marker.Length).Split('/');
					if (parts.Length < 2 || !parts[0].StartsWith("file_", StringComparison.Ordinal)) continue;
					return image.Substring(0, i) + "/api/1/image/" + parts[0] + "/" + parts[1] + "/256";
				}
			}
			catch { }
			return image;
		}

		// Whatever actually arrives, cap it here. The url rewrite is a REQUEST, not a guarantee —
		// the endpoint can ignore the size, the fallback path deliberately fetches the original, and
		// a card 214px wide has no use for a 1200x900 texture either way. 35 of those cost 150 MB of
		// VRAM on their own.
		private const int MaxThumbWidth = 320;
		private const int ThumbWidth = 256;

		private static Texture2D Downscale(Texture2D src, string cachePath)
		{
			try
			{
				if (src == null || src.width <= MaxThumbWidth) return src;
				int w = ThumbWidth;
				int h = Mathf.Max(1, Mathf.RoundToInt(src.height * (w / (float)src.width)));

				var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
				var prev = RenderTexture.active;
				Graphics.Blit(src, rt);
				RenderTexture.active = rt;

				var dst = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
				{ hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
				dst.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
				dst.Apply(false, false);

				RenderTexture.active = prev;
				RenderTexture.ReleaseTemporary(rt);
				UnityEngine.Object.Destroy(src);

				// Rewrite the cache with the small version so this only ever happens once per avatar.
				try
				{
					var png = ImageConversion.EncodeToPNG(dst);
					if (png != null && png.Length > 100)
					{
						var managed = new byte[png.Length];
						for (int i = 0; i < png.Length; i++) managed[i] = png[i];
						File.WriteAllBytes(cachePath, managed);
					}
				}
				catch { }
				return dst;
			}
			catch { return src; }
		}

		private static void DrainThumbs(int max)
		{
			for (int n = 0; n < max; n++)
			{
				if (!Decoded.TryDequeue(out var item)) return;
				_inflight--;
				if (_inflight < 0) _inflight = 0;
				try
				{
					if (item.data == null || item.data.Length < 100) { item.fav.ThumbState = 3; continue; }
					var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
					{ hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
					if (!ImageConversion.LoadImage(tex, new Il2CppStructArray<byte>(item.data)))
					{
						item.fav.ThumbState = 3;
						continue;
					}
					item.fav.Thumb = Downscale(tex, Path.Combine(ThumbDir, item.fav.Id + ".img"));
					item.fav.ThumbState = 2;
				}
				catch { item.fav.ThumbState = 3; }
			}
		}

		// ---------------------------------------------------------------- writes

		public static async System.Threading.Tasks.Task<bool> AddAsync(string avatarId)
		{
			if (string.IsNullOrEmpty(avatarId)) return false;
			var (ok, raw, status) = await VaAuth.FavAsync("add", avatarId);
			if (ok)
			{
				// Applied locally straight away: waiting for the next refresh to show it would make
				// the button feel broken for up to two minutes.
				lock (Gate)
				{
					if (!ById.ContainsKey(avatarId))
					{
						// FRONT of the list, not the end. The server returns favourites newest-first
						// (sort=newest), so appending made a freshly saved avatar appear last and then
						// jump to the top on the next refresh two minutes later. Inserting at 0 means
						// the optimistic local update already matches the order the server will send.
						var f = new Fav { Id = avatarId, Name = avatarId };
						Ids.Insert(0, avatarId); Items.Insert(0, f); ById[avatarId] = f;
					}
				}
				Bump();
				LastStatus = "added to your Archive favourites";
			}
			else LastStatus = Error(raw) ?? ("could not add (" + status + ")");
			return ok;
		}

		public static async System.Threading.Tasks.Task<bool> RemoveAsync(string avatarId)
		{
			if (string.IsNullOrEmpty(avatarId)) return false;
			var (ok, raw, status) = await VaAuth.FavAsync("remove", avatarId);
			if (ok)
			{
				lock (Gate)
				{
					if (ById.Remove(avatarId))
					{
						Ids.RemoveAll(x => string.Equals(x, avatarId, StringComparison.OrdinalIgnoreCase));
						Items.RemoveAll(x => string.Equals(x.Id, avatarId, StringComparison.OrdinalIgnoreCase));
					}
				}
				Bump();
				LastStatus = "removed from your Archive favourites";
			}
			else LastStatus = Error(raw) ?? ("could not remove (" + status + ")");
			return ok;
		}

		private static string Error(string raw)
		{
			try
			{
				using var d = JsonDocument.Parse(raw);
				if (d.RootElement.TryGetProperty("error", out var e)) return e.GetString();
			}
			catch { }
			return null;
		}
	}
}
