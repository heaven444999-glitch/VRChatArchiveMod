using Killiorim.Core;

namespace Killiorim.Modules
{
    // Placeholder for the optional asset-bundle hook. The public source tree
    // references its diagnostics, while the runtime patch is supplied separately.
    public sealed class AssetBundlePatchModule : IModule
    {
        public override string Name => "AssetBundlePatch";

        public static string HookInfo { get; } = "not installed";
        public static long Cleared { get; private set; }
        public static long Redirected { get; private set; }
        public static int Blocked { get; private set; }
        public static string LastBlocked { get; private set; } = "";
    }
}
