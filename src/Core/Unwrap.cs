using System;

namespace Killiorim.Core
{
	// "Exception has been thrown by the target of an invocation." names the messenger, never the
	// message. Anything reached by reflection — and in an IL2CPP mod that is most of it — arrives
	// wrapped in TargetInvocationException, and a type initializer that failed adds another wrapper
	// on top. Logging e.Message on one of those tells you only that something, somewhere, went wrong.
	//
	// Three module start-up failures were unreadable for exactly this reason. The real cause of one
	// of them turned out to be a missing GameAssembly export, which the outer message never hinted at.
	internal static class Unwrap
	{
		// The innermost exception, which is the one that actually happened.
		internal static Exception Root(Exception e)
		{
			int guard = 0;   // a cyclic InnerException chain is possible and must not hang the game
			while (e != null && e.InnerException != null && guard++ < 16) e = e.InnerException;
			return e;
		}

		// Type and message of the real cause, plus the frame it came from when there is one.
		internal static string Describe(Exception e)
		{
			if (e == null) return "<none>";
			Exception r = Root(e);
			string s = r.GetType().Name + ": " + r.Message;
			if (!ReferenceEquals(r, e)) s = e.GetType().Name + " -> " + s;
			string frame = FirstFrame(r);
			return frame == null ? s : s + "  [" + frame + "]";
		}

		private static string FirstFrame(Exception e)
		{
			try
			{
				string st = e.StackTrace;
				if (string.IsNullOrEmpty(st)) return null;
				string[] lines = st.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
				if (lines.Length == 0) return null;
				string line = lines[0].Trim();
				return line.Length > 160 ? line.Substring(0, 160) : line;
			}
			catch { return null; }
		}
	}
}
