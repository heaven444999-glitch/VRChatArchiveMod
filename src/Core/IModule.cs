namespace VRChatArchiveMod.Core
{
	// Base contract for every VRCHAT ARCHIVE MOD feature module.
	// Mirrors the Munchen ModuleComponent lifecycle, adapted to the BepInEx runtime.
	public abstract class IModule
	{
		public abstract string Name { get; }

		// Called once, after the plugin has loaded and Harmony is ready.
		public virtual void OnInitialize() { }

		// Called once the VRChat UI/manager is available (first scene fully up).
		public virtual void OnUiReady() { }

		public virtual void OnUpdate() { }
		public virtual void OnLateUpdate() { }
		public virtual void OnFixedUpdate() { }

		// Called from OnGUI (IMGUI). For modules that draw their own overlay (e.g. ESP).
		public virtual void OnGui() { }

		// Called when a scene finishes loading (buildIndex from Unity).
		public virtual void OnSceneLoaded(int buildIndex) { }

		public virtual void OnShutdown() { }
	}
}
