using System;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Full game-log capture. Subscribes to Unity's own log callback and mirrors EVERY line
	// the game (and every other plugin) emits — Debug.Log / LogWarning / LogError /
	// exceptions / asserts — to a file on disk.
	//
	// IMPORTANT — this is a PASSIVE listener, nothing more. It hooks NOTHING: no Photon, no
	// networking, no game methods. `Application.logMessageReceivedThreaded` is a callback Unity
	// already raises for messages the game chooses to log; we only receive them and write them
	// down. It cannot affect gameplay, networking, or anti-cheat, and it never reads or touches
	// Photon in any way. Toggle with ModConfig.LogCapture.Enabled.
	//
	// Output: BepInEx/VRChatArchiveMod/logs/game_full_<timestamp>.log, rotated into _partN files
	// so no single file grows unbounded.
	public class LogCaptureModule : IModule
	{
		public override string Name => "LogCapture";

		private const long MaxBytesPerFile = 256L * 1024 * 1024; // rotate at 256 MB
		private const int FlushEveryFrames = 120;                // ~2s at 60fps

		private readonly object _lock = new object();
		private Application.LogCallback _callback;   // kept alive so the delegate isn't collected
		private StreamWriter _writer;
		private string _dir;
		private string _baseName;
		private int _part;
		private long _bytes;
		private int _frame;
		private bool _active;

		// live stats for the menu / info
		public static long LinesWritten { get; private set; }
		public static long MenuLines { get; private set; }
		public static string MenuFile { get; private set; } = "(none)";
		private System.IO.StreamWriter _menuWriter;
		public static string CurrentFile { get; private set; } = "(off)";

		public override void OnInitialize()
		{
			if (!ModConfig.LogCaptureEnabled.Value)
			{
				VRChatArchiveModPlugin.Logger.LogInfo("[LogCapture] disabled by config.");
				return;
			}

			try
			{
				_dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "logs");
				Directory.CreateDirectory(_dir);
				_baseName = "game_full_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
				OpenNextFile();

				// Threaded variant catches messages logged off the main thread too, so the capture
				// is genuinely complete. This is a subscribe-only callback — it hooks nothing.
				_callback = Core.Il2CppDelegates.TryConvert<Application.LogCallback>(
					(Action<string, string, LogType>)OnLog, "LogCapture");
				if (_callback == null) return;   // the bridge is off; nothing to capture through
				Application.add_logMessageReceivedThreaded(_callback);
				_active = true;

				WriteRaw("==== VRCHAT ARCHIVE MOD — full game log capture ====");
				WriteRaw("started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · passive Unity log sink · Photon untouched");
				VRChatArchiveModPlugin.Logger.LogInfo($"[LogCapture] capturing all game logs to '{_dir}'.");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[LogCapture] could not start: {e}");
			}
		}

		public override void OnUpdate()
		{
			if (!_active) return;
			if (++_frame < FlushEveryFrames) return;
			_frame = 0;
			lock (_lock)
			{
				try { _writer?.Flush(); } catch { }
			}
		}

		public override void OnShutdown()
		{
			if (!_active) return;
			_active = false;
			try { if (_callback != null) Application.remove_logMessageReceivedThreaded(_callback); } catch { }
			lock (_lock)
			{
				try { WriteRaw("==== capture stopped " + DateTime.Now.ToString("HH:mm:ss") + " ===="); } catch { }
				try { _writer?.Flush(); _writer?.Dispose(); } catch { }
				_writer = null;
				// The companion stream too — an AutoFlush writer still holds the file handle.
				try { _menuWriter?.Flush(); _menuWriter?.Dispose(); } catch { }
				_menuWriter = null;
			}
		}

		// Unity log callback. May fire from any thread (threaded variant) — everything guarded.
		private void OnLog(string condition, string stackTrace, LogType type)
		{
			try
			{
				lock (_lock)
				{
					if (_writer == null) return;

					// Redacted BEFORE it is written: VRChat dumps its own command line at startup and
					// the loader is launched with the user's key on it, so this file otherwise carried
					// a live credential into every chat where somebody pasted it for help.
					string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  [" + type + "]  "
						+ Core.Redact.Line(condition);
					_writer.WriteLine(line);
					_bytes += line.Length + 2;

					bool wantTrace = ModConfig.LogCaptureStackTraces.Value
						&& (type == LogType.Error || type == LogType.Exception || type == LogType.Assert);
					if (wantTrace && !string.IsNullOrEmpty(stackTrace))
					{
						_writer.WriteLine(stackTrace);
						_bytes += stackTrace.Length + 2;
					}

					// A SECOND, FILTERED STREAM FOR THE MENU.
					//
					// The full log is the right thing to keep — but it reached nine megabytes in three
					// minutes, so anything about the menu is buried in it and unusable. This mirrors
					// only the lines that mention VRChat's menu, its favourites or its UI into a small
					// companion file, which is what actually gets read when a category fails to appear
					// or an injected list misbehaves.
					//
					// Same line, already redacted, written twice — no second formatting pass, and the
					// test below is a handful of substring checks on a string we already hold.
					if (MenuRelated(condition))
					{
						try
						{
							if (_menuWriter == null) OpenMenuFile();
							_menuWriter?.WriteLine(line);
							if (wantTrace && !string.IsNullOrEmpty(stackTrace)) _menuWriter?.WriteLine(stackTrace);
							MenuLines++;
						}
						catch { }
					}

					LinesWritten++;
					if (_bytes >= MaxBytesPerFile) OpenNextFile();
				}
			}
			catch { /* logging must never throw back into the game */ }
		}

		// What counts as "about the menu". Deliberately generous on the VRChat side and exact on
		// ours: a missing category shows up either as VRChat complaining, or as one of our own
		// modules reporting, and both belong in the same short file.
		private static readonly string[] MenuWords =
		{
			"favorite", "favourite", "FavoriteList", "FavoriteArea", "fvgrp_",
			"Menu_MM_", "QuickMenu", "MainMenu", "UIPage", "Voyager",
			"ArchiveHijack", "WorldFavList", "UserFavList", "AvatarFavList", "ArchiveFav",
			"QMTab", "MenuTheme", "MenuExclusive",
		};

		private static bool MenuRelated(string s)
		{
			if (string.IsNullOrEmpty(s)) return false;
			for (int i = 0; i < MenuWords.Length; i++)
				if (s.IndexOf(MenuWords[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
			return false;
		}

		private void OpenMenuFile()
		{
			try
			{
				string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "logs");
				System.IO.Directory.CreateDirectory(dir);
				MenuFile = System.IO.Path.Combine(dir, "menu_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
				_menuWriter = new System.IO.StreamWriter(MenuFile, false) { AutoFlush = true };
				_menuWriter.WriteLine("VRCHAT ARCHIVE — MENU LOG (filtered from the full game log)");
				_menuWriter.WriteLine(new string('=', 80));
				VRChatArchiveModPlugin.Logger.LogInfo("[LogCapture] menu log -> " + MenuFile);
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[LogCapture] menu log failed: " + e.Message);
				_menuWriter = null;
			}
		}

		// Opens the next rotation file (game_full_<ts>.log, then _part2, _part3, …).
		private void OpenNextFile()
		{
			try { _writer?.Flush(); _writer?.Dispose(); } catch { }
			_part++;
			string name = _part <= 1 ? _baseName + ".log" : _baseName + "_part" + _part + ".log";
			string path = Path.Combine(_dir, name);
			_writer = new StreamWriter(path, append: false) { AutoFlush = false };
			_bytes = 0;
			CurrentFile = path;
		}

		private void WriteRaw(string s)
		{
			try { _writer?.WriteLine(s); } catch { }
		}
	}
}
