using System;
using System.Collections.Generic;
using System.Text;

namespace Killiorim.Core
{
	// WHAT A SETTING IS ACTUALLY DOING, as opposed to what it is set to.
	//
	// Every control problem this mod has had came back to the same gap. A switch is flipped, the
	// value crosses to the other side correctly, the log even says "applied set ESP/Items = true" —
	// and nothing happens on screen. Three separate causes so far, all invisible:
	//
	//   * the value was gated behind a second setting nobody mentioned;
	//   * the effect it drives had been destroyed by a world change and never re-resolved;
	//   * the feature was off in a config file, and every module that reads it returns silently.
	//
	// In each case the honest report would have been one line, and in each case it took an hour of
	// reading to reach it. The transport was never wrong; there was simply nothing anywhere that
	// could say "on, but doing nothing, because X".
	//
	// So a module states its own truth here, keyed by the setting id the client already knows
	// ("ESP/Items"), and it travels back on the same sync as the values. A switch can then read
	// "on — 3 pickups glowing" or "on — HighlightsFX unavailable, nothing will glow", which turns
	// a silent failure into a sentence.
	//
	// Deliberately dumb: a string, replaced whenever the module has something newer to say. No
	// history, no severity enum, nothing to keep in sync. If nobody reports for an id, the client
	// simply shows nothing extra, which is the same as today.
	public static class FeatureHealth
	{
		public enum State { Ok, Idle, Broken }

		private sealed class Entry
		{
			public State State;
			public string Text;
		}

		private static readonly object Gate = new object();
		private static readonly Dictionary<string, Entry> Map =
			new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

		/// <summary>The feature is on and demonstrably working. Text says what it is doing.</summary>
		public static void Ok(string id, string text) => Set(id, State.Ok, text);

		/// <summary>Switched off, or nothing to act on yet. Not a fault.</summary>
		public static void Idle(string id, string text) => Set(id, State.Idle, text);

		/// <summary>On, but it cannot work — and this is the sentence that says why.</summary>
		public static void Broken(string id, string text) => Set(id, State.Broken, text);

		private static void Set(string id, State state, string text)
		{
			if (string.IsNullOrEmpty(id)) return;
			lock (Gate)
			{
				// Written every frame by some callers, so it must not allocate when nothing changed.
				if (Map.TryGetValue(id, out Entry e))
				{
					if (e.State == state && string.Equals(e.Text, text, StringComparison.Ordinal)) return;
					e.State = state; e.Text = text;
					return;
				}
				Map[id] = new Entry { State = state, Text = text };
			}
		}

		public static void Clear(string id)
		{
			if (string.IsNullOrEmpty(id)) return;
			lock (Gate) Map.Remove(id);
		}

		/// <summary>{"ESP/Items":{"s":"broken","t":"..."}} — folded into the sync payload.</summary>
		public static void AppendJson(StringBuilder sb)
		{
			lock (Gate)
			{
				sb.Append('{');
				bool first = true;
				foreach (var kv in Map)
				{
					if (!first) sb.Append(',');
					first = false;
					Quote(sb, kv.Key);
					sb.Append(":{\"s\":");
					Quote(sb, kv.Value.State == State.Ok ? "ok" : kv.Value.State == State.Broken ? "broken" : "idle");
					sb.Append(",\"t\":");
					Quote(sb, kv.Value.Text ?? "");
					sb.Append('}');
				}
				sb.Append('}');
			}
		}

		private static void Quote(StringBuilder sb, string s)
		{
			sb.Append('"');
			foreach (char c in s ?? "")
			{
				switch (c)
				{
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					default:
						if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
						else sb.Append(c);
						break;
				}
			}
			sb.Append('"');
		}
	}
}
