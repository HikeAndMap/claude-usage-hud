# Claude Usage HUD (Windows)

A small Windows tray app + floating always-on-top HUD that shows live Claude Code
context-window usage plus your account's weekly plan usage, inspired by
[claude-code-usage-bar](https://github.com/leeguooooo/claude-code-usage-bar)
(macOS-only, built on PyObjC/AppKit). This is a from-scratch Windows-native
reimplementation in C#/.NET WinForms - not a port of that codebase.

## How it works

**Context window usage**: Claude Code writes one JSON object per line to
`%USERPROFILE%\.claude\projects\<project>\<session-id>.jsonl` for every message in
every session, across every project. Each assistant message line carries a
`message.usage` object with `input_tokens` / `cache_creation_input_tokens` /
`cache_read_input_tokens` - summing those three gives the exact prompt size for
that turn, which is what "context window used" actually means.

The HUD:
1. Finds whichever `.jsonl` file (across all projects) was most recently modified -
   that's reliably the currently-active session, since only the session actually
   receiving messages gets appended to.
2. Reads just the tail of that file (not the whole multi-megabyte transcript) and
   parses the last line that has a `usage` object.
3. Divides by a configurable context-window-size (see below) and shows the
   percentage, color-coded green/yellow/red at the same 70%/90% thresholds as a
   typical statusline script.

**Weekly plan usage**: the Claude desktop app already tracks this itself, locally,
in `%AppData%\Claude\plan-usage-history.json` - a running log of periodic samples,
each with a five-hour (`fh`) and seven-day/weekly (`sd`) usage percentage. The HUD
just reads the last sample's `sd` value and color-codes it the same way as the
context line. The rolling five-hour figure is deliberately not shown here since the
desktop app already surfaces that one in its own UI.

No private API, no stored credentials, no reverse-engineering required for either
figure - just files Claude (CLI and desktop) already writes on its own.

## Why the context-window size is a setting, not auto-detected

The real per-model/per-plan context window size isn't present anywhere in the
transcript files - Claude Code computes it internally and only ever exposes it
through the `statusLine` hook's JSON payload, which doesn't fire for the desktop
app. Right-click the tray icon → "Set context window size..." to set it (defaults
to 200,000; the desktop app's own status popup can tell you the real number for
your current plan/model, e.g. 1,000,000 for a 1M-context Sonnet 5 session).

## Running it

```
dotnet run --project ClaudeUsageHUD
```

Or build and run the .exe directly from `ClaudeUsageHUD\bin\Debug\net10.0-windows\`.

- Tray icon (small orange circle, "C") → right-click for the menu: Show/Hide HUD,
  Set context window size, Exit.
- The HUD itself is a small borderless panel showing two lines - context-window
  usage on top, weekly plan usage below - draggable by clicking anywhere on it;
  its position is remembered (`%AppData%\ClaudeUsageHUD\settings.json`) between
  launches.

## Auto-start with Windows

A shortcut in `shell:startup` (`%AppData%\Microsoft\Windows\Start Menu\Programs\Startup\Claude Usage HUD.lnk`)
launches the app on login - it points at the **Release** build
(`ClaudeUsageHUD\bin\Release\net10.0-windows\ClaudeUsageHUD.exe`), so rebuild
Release (not just Debug) for code changes to reach the auto-started copy.

## Opening in Visual Studio

Open `ClaudeUsageHUD.sln` at the repo root - it's a classic `.sln` (not the newer
`.slnx` format `dotnet new sln` defaults to now), for compatibility with older VS
versions.
