using System;
using System.Collections.Generic;

namespace Killiorim.Core
{
	// Central registry + dispatcher for feature modules.
	// Every dispatch is guarded so one misbehaving module can never take down the others
	// (this is the same fail-safe philosophy as the Munchen client's anti-crash design).
	public static class ModuleManager
	{
		private static readonly List<IModule> Modules = new List<IModule>();
		private static bool _uiReady;

		// ---- per-module profiler -------------------------------------------------------
		// Guessing at what costs frame time did not settle it, so measure instead. Each
		// dispatch is timed with raw Stopwatch timestamps (no allocation), accumulated per
		// module, and turned into a "ms per second of wall clock" figure once a second. That
		// number is directly comparable between modules: 16.6 ms/s means the module eats one
		// frame's worth of time every second at 60 fps.
		private static readonly Dictionary<string, long> Ticks = new Dictionary<string, long>(StringComparer.Ordinal);
		private static readonly Dictionary<string, float> LastMs = new Dictionary<string, float>(StringComparer.Ordinal);
		private static float _windowStart;
		private static readonly double TickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

		public static bool Profiling { get; set; }

		private static void Account(IModule m, long startTs)
		{
			if (!Profiling) return;
			long d = System.Diagnostics.Stopwatch.GetTimestamp() - startTs;
			string k = m?.Name ?? "?";
			Ticks.TryGetValue(k, out long cur);
			Ticks[k] = cur + d;
		}

		// Called once per frame from the runner; rolls the 1-second window.
		public static void ProfileTick()
		{
			if (!Profiling) return;
			float now = UnityEngine.Time.realtimeSinceStartup;
			if (_windowStart <= 0f) { _windowStart = now; return; }
			float span = now - _windowStart;
			if (span < 1f) return;
			_windowStart = now;
			LastMs.Clear();
			foreach (var kv in Ticks) LastMs[kv.Key] = (float)(kv.Value * TickMs / span);
			Ticks.Clear();
		}

		// THE COSTLIEST MODULE OF THE SECOND CURRENTLY IN PROGRESS, in milliseconds.
		//
		// ProfileReport() answers a different question: it is the LAST COMPLETED one-second window,
		// so at the moment a spike happens it describes the second BEFORE it — which is precisely
		// the second the spike is not in. For attributing a stutter we want the time accumulated so
		// far in the window the stutter just landed in, and that is the live Ticks table.
		public static KeyValuePair<string, float> CurrentHot()
		{
			string best = null;
			long most = 0;
			foreach (var kv in Ticks)
				if (kv.Value > most) { most = kv.Value; best = kv.Key; }
			return new KeyValuePair<string, float>(best ?? "?", (float)(most * TickMs));
		}

		// Modules sorted by cost, worst first: (name, ms per second).
		public static List<KeyValuePair<string, float>> ProfileReport()
		{
			var list = new List<KeyValuePair<string, float>>(LastMs);
			list.Sort((a, b) => b.Value.CompareTo(a.Value));
			return list;
		}

		public static float ProfileTotalMs()
		{
			float t = 0f;
			foreach (var kv in LastMs) t += kv.Value;
			return t;
		}

		public static void Register(IModule module)
		{
			if (module == null) return;
			Modules.Add(module);
		}

		public static void InitializeAll()
		{
			foreach (var m in Modules)
			{
				try { m.OnInitialize(); }
				catch (Exception e) { LogModuleError(m, "OnInitialize", e); }
			}
		}

		public static void NotifyUiReady()
		{
			if (_uiReady) return;
			_uiReady = true;
			foreach (var m in Modules)
			{
				try { m.OnUiReady(); }
				catch (Exception e) { LogModuleError(m, "OnUiReady", e); }
			}
		}

		public static void Update()
		{
			for (int i = 0; i < Modules.Count; i++)
			{
				long _ts = Profiling ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
				try { Modules[i].OnUpdate(); }
				catch (Exception e) { LogModuleError(Modules[i], "OnUpdate", e); }
				Account(Modules[i], _ts);
			}
		}

		public static void LateUpdate()
		{
			for (int i = 0; i < Modules.Count; i++)
			{
				long _ts = Profiling ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
				try { Modules[i].OnLateUpdate(); }
				catch (Exception e) { LogModuleError(Modules[i], "OnLateUpdate", e); }
				Account(Modules[i], _ts);
			}
		}

		public static void FixedUpdate()
		{
			for (int i = 0; i < Modules.Count; i++)
			{
				long _ts = Profiling ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
				try { Modules[i].OnFixedUpdate(); }
				catch (Exception e) { LogModuleError(Modules[i], "OnFixedUpdate", e); }
				Account(Modules[i], _ts);
			}
		}

		public static void OnGui()
		{
			for (int i = 0; i < Modules.Count; i++)
			{
				long _ts = Profiling ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
				try { Modules[i].OnGui(); }
				catch (Exception e) { LogModuleError(Modules[i], "OnGui", e); }
				Account(Modules[i], _ts);
			}
		}

		public static void SceneLoaded(int buildIndex)
		{
			ApiUsers.Clear();   // player objects are gone; their cached APIUser handles are stale
			PlayerRef.Invalidate();   // and so is the local player PlayerRef now holds across frames
			foreach (var m in Modules)
			{
				try { m.OnSceneLoaded(buildIndex); }
				catch (Exception e) { LogModuleError(m, "OnSceneLoaded", e); }
			}
		}

		public static void ShutdownAll()
		{
			foreach (var m in Modules)
			{
				try { m.OnShutdown(); }
				catch (Exception e) { LogModuleError(m, "OnShutdown", e); }
			}
		}

		// Rate-limited so a module that throws EVERY frame cannot bury the game. Without this, one
		// broken module produced a full exception + stack string per frame, each of which the
		// diagnostics writer then wrote to disk — the logging itself became the performance
		// problem, exactly when something was already wrong. Same message = at most one line per
		// 10 s, with a count of what was suppressed.
		private sealed class ErrRate { public float At; public int Suppressed; }
		private static readonly System.Collections.Generic.Dictionary<string, ErrRate> Rates =
			new System.Collections.Generic.Dictionary<string, ErrRate>(StringComparer.Ordinal);

		private static void LogModuleError(IModule module, string phase, Exception e)
		{
			try
			{
				string key = (module?.Name ?? "?") + "/" + phase;
				float now = UnityEngine.Time.realtimeSinceStartup;
				if (!Rates.TryGetValue(key, out ErrRate r))
				{
					Rates[key] = new ErrRate { At = now };
				}
				else if (now - r.At < 10f)
				{
					r.Suppressed++;
					return;
				}
				else
				{
					if (r.Suppressed > 0)
						Killiorim.Logger?.LogError($"[{module?.Name ?? "?"}] {phase}: {r.Suppressed} identical error(s) suppressed in the last {now - r.At:F0}s.");
					r.At = now; r.Suppressed = 0;
				}
			}
			catch { }
			Killiorim.Logger?.LogError($"[{module?.Name ?? "?"}] {phase} threw: {e}");
		}
	}
}
