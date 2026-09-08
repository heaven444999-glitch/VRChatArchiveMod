<!--
No workflow in this repository compiles this project, and none ever will: the mod cannot be built on a
GitHub runner. It references the BepInEx 6 IL2CPP assemblies and the VRChat/Unity interop assemblies
under libs/, and embeds 18 files from ressources/ — neither directory is redistributable, so neither is
committed. Your local build and your in-game test are therefore the only evidence the maintainer has
that this change works. Be specific about both.
-->

## What this changes, and why

<!-- The behaviour before, the behaviour after, and the reason. If it fixes something, say what the
     failure looked like on screen, not only which line was wrong. -->

## Tested against

- **VRChat build:** <!-- the exact version string, e.g. 2026.2.1p3 — IL2CPP is renamed every update, so a fix is only ever verified against one build -->
- **BepInEx build:** <!-- e.g. 6.0.0-be.<build> (IL2CPP) -->
- **Mode:** <!-- Desktop, VR, or both -->

## Checklist

- [ ] `dotnet build -c Release` succeeds locally, against my own `libs/` and `ressources/`.
- [ ] I loaded the built DLL into VRChat and exercised the changed behaviour in a real instance — not only compiled it.
- [ ] It keeps the local-only invariant: it changes my client only, and anything that unavoidably touches networked state (pickup ownership, synced video, chatbox, voice) is opt-in, off by default, and labelled as visible to everyone.
- [ ] It degrades rather than crashes when the IL2CPP layer shifts: pointers guarded before dereference, delegates converted through the gate, feature state reported instead of an unhandled throw.
- [ ] No files added under `libs/`, `ressources/` or `captures/`. The first two are not ours to redistribute; the third holds runtime dumps that can contain instance and user data.
- [ ] Commit messages match the repository's existing style — sentence case, imperative, an optional `Scope: ` prefix, a second clause after an em dash or semicolon. For example: `Make LICENSE pure MIT; move third-party notes to NOTICE`. No `feat:` / `fix:` / `chore:` prefixes.
- [ ] `CHANGELOG.md` updated, if this is user-facing.
- [ ] Any screenshot attached here shows no other player's display name, profile or VRChat id. This repository is public; blur them or capture in a private instance.
