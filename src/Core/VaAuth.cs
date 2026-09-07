using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VRChatArchiveMod.Core
{
	// Resolves HOW the mod authenticates VA tag WRITES, and performs them. Reads are always
	// public (GET /api/va-tags), so this only governs add/remove/lock.
	//
	//   ClientBridge : the mod runs inside the VRChat Archive desktop client, which is already
	//                  logged in. We POST the write to the client's LocalBridge
	//                  (127.0.0.1:8791/va-tag) and the CLIENT performs it with ITS session — the
	//                  account cookie NEVER reaches the mod (so a malicious web page that can hit
	//                  the CORS-* bridge cannot steal a token from us). Auto, no login.
	//   ModLogin     : the mod is NOT inside the client (a third-party dev's launcher). The user
	//                  logs into their VRChat Archive account inside the mod; we hold the session
	//                  cookie IN MEMORY ONLY (never written to disk) and send it as a Cookie header.
	//   None         : no auth available -> writes are refused ("login required"); reads still work.
	public static class VaAuth
	{
		public enum Mode { None, ClientBridge, ModLogin }

		public static Mode Current { get; private set; } = Mode.None;
		public static string AccountName = "";       // display name of the connected VA account
		/// <summary>Archive access level of the connected account (5+ Legendary, 4 VIP, 3 Premium,
		/// 2 Supporter, 1 Free, 0 none). -1 = not known: no bridge, or a client too old to send it —
		/// the rank badge leaves everything alone on -1 rather than guessing.</summary>
		public static int AccountLevel = -1;
		public static bool AccountAdmin;             // the account carries the admin flag
		public static string LastError = "";
		public static bool Busy { get; private set; } // a login is in flight

		private const string Bridge = "http://127.0.0.1:8791";
		private static string _cookie = "";          // ModLogin session cookie — IN MEMORY ONLY
		private static double _nextProbe;
		private static volatile bool _probing;        // guards against overlapping probes (timeout > interval)
		private static int _probeFailures;            // consecutive TRANSIENT /status failures

		private static readonly HttpClient Http = Make();
		private static HttpClient Make()
		{
			// UseCookies=false: we read Set-Cookie ourselves and send the Cookie header manually,
			// exactly like the desktop client's ArchiveApi (mixing auto-cookies with a manual
			// header is unreliable). Browser UA: Cloudflare 403s non-browser agents.
			var c = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(12) };
			c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
				"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36 VRChatArchiveMod/3.3");
			return c;
		}

		public static bool IsAuthed => Current != Mode.None;
		public static bool InsideClient => Current == Mode.ClientBridge;

		// Human-readable one-liner for the menu.
		public static string StatusLine => Current switch
		{
			Mode.ClientBridge => "connected via the VRChat Archive client — " + (string.IsNullOrEmpty(AccountName) ? "logged in" : AccountName),
			Mode.ModLogin     => "logged in as " + (string.IsNullOrEmpty(AccountName) ? "member" : AccountName),
			_                 => "not connected — tag writing is disabled",
		};

		// Cheap periodic probe of the client bridge; call from a module OnUpdate.
		public static void Poll(double now)
		{
			if (Busy || _probing || now < _nextProbe) return;   // never overlap probes
			_nextProbe = now + 5.0;
			_ = ProbeAsync();
		}

		private static async Task ProbeAsync()
		{
			_probing = true;
			try
			{
				try
				{
					using var resp = await Http.GetAsync(Bridge + "/status");
					if (resp.IsSuccessStatusCode)
					{
						using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
						var r = doc.RootElement;
						// Only trust the bridge when it can actually PROXY writes (vaTagProxy):
						// an older client is logged in but 404s /va-tag, which would strand the
						// user on a "connected" strip whose writes all fail.
						bool canProxy = r.TryGetProperty("vaTagProxy", out var vp) && vp.ValueKind == JsonValueKind.True;
						if (canProxy && r.TryGetProperty("archiveLoggedIn", out var li) && li.ValueKind == JsonValueKind.True)
						{
							AccountName = (r.TryGetProperty("archiveUser", out var au) && au.ValueKind == JsonValueKind.String)
								? (au.GetString() ?? "client") : "client";
							// The tier the account holds, straight from the client — the only side that
							// knows it. -1 means "an older client that does not send the field", which
							// the rank badge reads as "don't touch anything": updating the mod alone can
							// never strip somebody's badge.
							AccountLevel = (r.TryGetProperty("archiveLevel", out var al) && al.ValueKind == JsonValueKind.Number && al.TryGetInt32(out int lv)) ? lv : -1;
							AccountAdmin = r.TryGetProperty("archiveAdmin", out var ad) && ad.ValueKind == JsonValueKind.True;
							_probeFailures = 0;
							Current = Mode.ClientBridge;
							return;
						}
						// DEFINITIVE negative: the bridge answered but isn't logged in / can't proxy.
						// Demote immediately (this is real state, not a blip).
						// Level 0 only when nothing else is holding an account: signed out IS the
						// answer, and it is what takes the rank badge off. A mod-side login stays at
						// -1 because that path never learns the tier.
						AccountLevel = string.IsNullOrEmpty(_cookie) ? 0 : -1;
						AccountAdmin = false;
						_probeFailures = 0;
						Current = !string.IsNullOrEmpty(_cookie) ? Mode.ModLogin : Mode.None;
						return;
					}
					// non-2xx: treat as a transient failure (fall through to the counter)
				}
				catch { /* transient: bridge unreachable / timed out */ }

				// Transient failure. Don't strand a live bridge on ONE blip: keep the last-good
				// ClientBridge until several probes in a row fail, then fall back.
				_probeFailures++;
				if (Current == Mode.ClientBridge && _probeFailures < 3) return;
				Current = !string.IsNullOrEmpty(_cookie) ? Mode.ModLogin : Mode.None;
			}
			finally { _probing = false; }
		}

		// Fallback login (third-party clients). Session cookie kept in memory only.
		public static async Task<bool> LoginAsync(string username, string password)
		{
			username = (username ?? "").Trim();
			if (username.Length == 0 || string.IsNullOrEmpty(password)) { LastError = "enter your VRChat Archive username and password"; return false; }
			Busy = true; LastError = "";
			try
			{
				var payload = new { username, password, client = "vrchatarchive-mod", client_version = "3.3" };
				using var req = new HttpRequestMessage(HttpMethod.Post, ModConfig.VaTagsApiBase.Value.TrimEnd('/') + "/api/login");
				req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
				using var resp = await Http.SendAsync(req);
				string body = await resp.Content.ReadAsStringAsync();
				if (resp.IsSuccessStatusCode)
				{
					string cookie = ExtractCookies(resp);
					if (string.IsNullOrEmpty(cookie)) { LastError = "logged in but no session was returned"; return false; }
					_cookie = cookie; AccountName = username; Current = Mode.ClientBridge == Current ? Current : Mode.ModLogin;
					return true;
				}
				try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("error", out var e)) { LastError = e.GetString() ?? "login failed"; return false; } } catch { }
				LastError = "login failed (" + (int)resp.StatusCode + ")";
				return false;
			}
			catch (Exception ex) { LastError = "network error: " + ex.Message; return false; }
			finally { Busy = false; }
		}

		public static void Logout()
		{
			_cookie = "";
			if (Current == Mode.ModLogin) { Current = Mode.None; AccountName = ""; }
			_nextProbe = 0; // re-probe the bridge immediately
		}

		// Performs an add/remove/lock write through whichever mode is active. Returns the RAW
		// upstream JSON verbatim (so the caller parses tags/locked/error exactly as before).
		public static async Task<(bool ok, string raw, int status)> SendWriteAsync(string action, string bodyJson)
		{
			if (Current == Mode.ClientBridge)
			{
				string wrapper = "{\"action\":\"" + action + "\",\"payload\":" + bodyJson + "}";
				try
				{
					using var req = new HttpRequestMessage(HttpMethod.Post, Bridge + "/va-tag");
					req.Content = new StringContent(wrapper, Encoding.UTF8, "application/json");
					using var resp = await Http.SendAsync(req);
					string raw = await resp.Content.ReadAsStringAsync();
					// We only reach ClientBridge mode when /status advertised vaTagProxy, so the
					// route EXISTS: any HTTP status (including a semantic 404 like "user does not
					// have this tag" on remove) is authoritative. Only a NETWORK failure (catch)
					// falls through to a mod login — never a real HTTP response.
					return (resp.IsSuccessStatusCode, raw, (int)resp.StatusCode);
				}
				catch { /* bridge died mid-write -> fall through to mod login if present */ }
			}

			if (!string.IsNullOrEmpty(_cookie))
			{
				try
				{
					using var req = new HttpRequestMessage(HttpMethod.Post, ModConfig.VaTagsApiBase.Value.TrimEnd('/') + "/api/va-tags/" + action);
					req.Headers.TryAddWithoutValidation("Cookie", _cookie);
					req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
					using var resp = await Http.SendAsync(req);
					string raw = await resp.Content.ReadAsStringAsync();
					return (resp.IsSuccessStatusCode, raw, (int)resp.StatusCode);
				}
				catch (Exception ex) { return (false, "{\"error\":\"network error: " + Esc(ex.Message) + "\"}", 0); }
			}

			return (false, "{\"error\":\"login required — connect your VRChat Archive account\",\"login_required\":true}", 401);
		}


		// Authenticated POST to any archive endpoint, reusing whichever mode is active. Used by the
		// VRCA request button so the submission carries the member's real session instead of a
		// self-declared name.
		//
		// NOTE, deliberately recorded here: /api/assetbundle-request does not currently verify that
		// session server-side, so this is an INTERFACE gate — it stops the mod submitting on behalf
		// of someone who is not signed in, it cannot stop a direct POST. The session is sent anyway
		// so the server can start enforcing it without a client change.
		public static async Task<(bool ok, string raw, int status)> PostAsync(string path, string bodyJson)
		{
			if (Current == Mode.ClientBridge)
			{
				string wrapper = "{\"action\":\"post\",\"path\":\"" + path + "\",\"payload\":" + bodyJson + "}";
				try
				{
					using var req = new HttpRequestMessage(HttpMethod.Post, Bridge + "/va-post");
					req.Content = new StringContent(wrapper, Encoding.UTF8, "application/json");
					using var resp = await Http.SendAsync(req);
					string raw = await resp.Content.ReadAsStringAsync();
					if (resp.StatusCode != HttpStatusCode.NotFound)
						return (resp.IsSuccessStatusCode, raw, (int)resp.StatusCode);
					// older client without the generic proxy -> fall through to a direct call
				}
				catch { }
			}

			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Post, ModConfig.VaTagsApiBase.Value.TrimEnd('/') + path);
				if (!string.IsNullOrEmpty(_cookie)) req.Headers.TryAddWithoutValidation("Cookie", _cookie);
				req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
				using var resp = await Http.SendAsync(req);
				string raw = await resp.Content.ReadAsStringAsync();
				return (resp.IsSuccessStatusCode, raw, (int)resp.StatusCode);
			}
			catch (Exception ex) { return (false, "{\"error\":\"network error: " + Esc(ex.Message) + "\"}", 0); }
		}

		// Soundboard trigger. Anyone may fire one, so this never refuses for lack of an account:
		// the session cookie is attached when we happen to hold one (ModLogin), which just means the
		// server can name the sender instead of logging "guest".
		public static async Task<(bool ok, string raw, int status)> PostSoundAsync(string bodyJson)
		{
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Post,
					ModConfig.VaTagsApiBase.Value.TrimEnd('/') + "/api/mod-sound");
				if (!string.IsNullOrEmpty(_cookie)) req.Headers.TryAddWithoutValidation("Cookie", _cookie);
				req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
				using var resp = await Http.SendAsync(req);
				string raw = await resp.Content.ReadAsStringAsync();
				return (resp.IsSuccessStatusCode, raw, (int)resp.StatusCode);
			}
			catch (Exception ex) { return (false, "{\"error\":\"network error: " + Esc(ex.Message) + "\"}", 0); }
		}

		// Archive favourites, through the desktop client ONLY.
		//
		// Unlike tags and the soundboard there is no direct fallback: favourites are account data
		// and the mod holds no session of its own worth trusting with them. If the client is not
		// running, the honest answer is "not available", not a half-authenticated request.
		public static async Task<(bool ok, string raw, int status)> FavAsync(string action, string avatarId)
			=> await FavRawAsync("{\"action\":\"" + action + "\",\"id\":\"" + (avatarId ?? "").Replace("\"", "") + "\"}");

		// The same relay, for the calls whose payload is not a single id (a batch of ids to resolve,
		// a thumbnail url to fetch). The client decides what each action is allowed to reach.
		// POST to any bridge route. The favourites path has its own helper because it carries the
		// account session; this one is for routes that do not — the mod/client control channel,
		// where the payload is settings and roster rather than anything belonging to an account.
		public static async Task<(bool ok, string raw, int status)> PostBridgeAsync(string path, string bodyJson)
		{
			if (Current != Mode.ClientBridge)
				return (false, "{\"error\":\"the VRChat Archive client must be running\"}", 401);
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Post, Bridge + path);
				req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
				using var resp = await Http.SendAsync(req);
				return (resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync(), (int)resp.StatusCode);
			}
			catch (Exception ex) { return (false, "{\"error\":\"network error: " + Esc(ex.Message) + "\"}", 0); }
		}

		public static async Task<(bool ok, string raw, int status)> FavRawAsync(string bodyJson)
		{
			if (Current != Mode.ClientBridge)
				return (false, "{\"error\":\"the VRChat Archive client must be running and signed in\"}", 401);
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Post, Bridge + "/va-fav");
				req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
				using var resp = await Http.SendAsync(req);
				return (resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync(), (int)resp.StatusCode);
			}
			catch (Exception ex) { return (false, "{\"error\":\"network error: " + Esc(ex.Message) + "\"}", 0); }
		}

		private static string ExtractCookies(HttpResponseMessage resp)
		{
			if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return "";
			var parts = new System.Collections.Generic.List<string>();
			foreach (var v in values) { int i = v.IndexOf(';'); parts.Add(i >= 0 ? v.Substring(0, i) : v); }
			return string.Join("; ", parts);
		}

		private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "'");
	}
}
