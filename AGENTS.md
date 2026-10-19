# Agentwachter

A personal toolkit for running Claude Code and Codex side by side on Windows: the bridge (the
`agent-messaging` skill), the watcher (reads the agents' own session files and serves JSON and a
page on 127.0.0.1:17321) and the tray app. [README.md](README.md) covers setup and how each part
works.

## Run and verify

- Watcher: `node watcher/agentwachter.mjs` (Node 24, no dependencies). Tray: `dotnet build tray`,
  then `tray\bin\Debug\net10.0-windows\AgentwachterTray.exe`, which starts and stops the watcher
  itself.
- Check the tray without clicking: `AgentwachterTray.exe --snapshot out.png`. README images come
  from the demo data on a spare port ("Redrawing the screenshots"), never from real sessions.
- `bridge/` is live for every agent on this machine (`~/.agents/skills/agent-messaging` is a
  junction to it), and hooks call it on every prompt and tool call. A broken `agent_msg.py` breaks
  both agents, so run `python bridge/agent_msg.py list` before committing a change to it.

## Ship

- The repository is public on GitHub (MIT). Ask the user before pushing, and keep personal details,
  real session content and local paths out of commits.

## Notes

- The watcher only reads files that Claude Code and Codex already write, so watching costs no turns.
  Keep it read-only.
- `tray/Theme.cs` and `tray/Flyout.cs` are copies of UsageTray's, so a later merge stays easy.
  Generic features go to UsageTray; personal ones stay here.
