using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace VRChatArchiveMod.Core
{
	// A BREADCRUMB TRAIL THAT SURVIVES THE PROCESS DYING.
	//
	// An access violation inside a native il2cpp call does not throw, does not unwind, and does not
	// run a finally block: the process is simply gone. Anything still sitting in a log buffer dies
	// with it — which is exactly what happened on 2026-09-01, when the crash log ended on "applied
	// action videoUrl" and said nothing about what the injector was touching at the time.
	//
	// So this does not go through the logger. Every step is written and FLUSHED TO DISK before the
	// risky call it describes is made (FileStream.Flush(true) pushes the OS buffer out, not just the
	// managed one). The cost is real — a forced disk write per step — which is why a trail is opened
	// around one dangerous operation and closed immediately after, never left running.
	//
	// HOW TO READ ONE: the trail is written to
	//     BepInEx\VRChatArchiveMod\crash\<name>.trail
	// and its LAST LINE is the last thing that completed. The step after it is what killed the game.
	// End() appends "== COMPLETED ==" and renames nothing; if the file exists on the next run WITHOUT
	// that marker, the previous attempt crashed — Check() reports that, once, with the last step in
	// it, so the next session tells you what the last one died on without anyone reading a file.
	public static class CrashTrail
	{
		private static FileStream _fs;
		private static string _path;
		private static string _name;
		private static readonly object Lock = new object();

		private static string Dir
		{
			get
			{
				string d = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "crash");
				Directory.CreateDirectory(d);
				return d;
			}
		}

		/// <summary>What the previous run of this trail died on, or "" when it completed (or never
		/// ran). Reading it also CONSUMES it, so it is reported once and not every session.</summary>
		public static string Check(string name)
		{
			try
			{
				string p = Path.Combine(Dir, name + ".trail");
				if (!File.Exists(p)) return "";
				string text = File.ReadAllText(p);
				File.Delete(p);
				if (text.IndexOf("== COMPLETED ==", StringComparison.Ordinal) >= 0) return "";

				string last = "";
				var lines = text.Split('\n');
				for (int i = lines.Length - 1; i >= 0; i--)
				{
					string l = lines[i].Trim();
					if (l.Length > 0) { last = l; break; }
				}
				return last;
			}
			catch { return ""; }
		}

		public static void Begin(string name, string header)
		{
			lock (Lock)
			{
				try
				{
					End();   // never two at once
					_name = name;
					_path = Path.Combine(Dir, name + ".trail");
					_fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
					Write("== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
						+ " " + name + " ==");
					if (!string.IsNullOrEmpty(header)) Write(header);
				}
				catch { _fs = null; }
			}
		}

		/// <summary>One breadcrumb, on disk before this call returns. Say what you are ABOUT to do and
		/// name the thing you are about to touch — the value of a trail is entirely in that name.</summary>
		public static void Step(string step)
		{
			lock (Lock)
			{
				if (_fs == null) return;
				try { Write(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + step); }
				catch { }
			}
		}

		public static void End()
		{
			lock (Lock)
			{
				if (_fs == null) return;
				try { Write("== COMPLETED =="); } catch { }
				try { _fs.Dispose(); } catch { }
				_fs = null; _path = null; _name = null;
			}
		}

		private static void Write(string line)
		{
			byte[] b = Encoding.UTF8.GetBytes(line + "\r\n");
			_fs.Write(b, 0, b.Length);
			_fs.Flush(true);   // to DISK: a managed flush alone still dies with the process
		}
	}
}
