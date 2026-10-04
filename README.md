# Claude Usage HUD (Windows)

A small Windows tray app + floating always-on-top HUD that shows live Claude Code
context-window usage plus your account's weekly plan usage, inspired by
[claude-code-usage-bar](https://github.com/leeguooooo/claude-code-usage-bar)
(macOS-only, built on PyObjC/AppKit). This is a from-scratch Windows-native
reimplementation in C#/.NET WinForms - not a port of that codebase.

## Download

**[⬇ Download the latest release](https://github.com/HikeAndMap/claude-usage-hud/releases/latest)**
— grab `ClaudeUsageHUD.exe` and run it directly. It's a self-contained build, so
no .NET runtime install is required.

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

**Weekly and rolling 5-hour usage**: the Claude desktop app already tracks both
itself, locally, in a running log of periodic samples, each with a five-hour
(`fh`) and seven-day/weekly (`sd`) usage percentage. The HUD reads the last
sample's values and color-codes them the same way as the context line. Claude
desktop is installed as an MSIX-packaged app, so its usage-history file doesn't
live at the "traditional" `%AppData%\Claude\plan-usage-history.json` path -
Windows virtualizes that into a per-package location that isn't reliably visible
outside Claude's own process, so the HUD resolves and reads the real underlying
file directly. No reset time is stored anywhere in the file for the 5-hour figure,
so the HUD estimates it from the sample history instead (it resets exactly 5 hours
after the most recent sharp drop in `fh`).

**Live usage (recommended)**: since around October 2026 the desktop app no longer
keeps that file up to date (it only seems to write a sample at launch), so the
file-based figures go stale - the HUD then shows them in gray with their age, e.g.
`Weekly: 0% (5h old)`. For live numbers, right-click the tray icon → **Sign in for
live usage...** and approve the request in your browser once. The HUD then asks
Anthropic's usage endpoint for your weekly and 5-hour usage (with the real 5-hour
reset time) once a minute, and falls back to the file reader if that fails.

The sign-in is the HUD's own OAuth login (the same flow Claude Code uses, with the
`user:profile` scope the usage endpoint requires). Its tokens are stored only in
`%AppData%\ClaudeUsageHUD\hud-login.bin`, encrypted for your Windows account, and
renewed automatically; the HUD never reads or writes Claude Code's or the desktop
app's own credential stores. The endpoint and client id are undocumented, so this
could break if Anthropic changes them - failures are logged (never with tokens) to
`%AppData%\ClaudeUsageHUD\weekly-diagnostic.log`, and hovering the HUD shows the
current live-usage status. Without signing in, nothing is stored and the HUD works
from local files only.

## Why the context-window size is a setting, not auto-detected

The real per-model/per-plan context window size isn't present anywhere in the
transcript files - Claude Code computes it internally and only ever exposes it
through the `statusLine` hook's JSON payload, which doesn't fire for the desktop
app. Right-click the tray icon → "Set context window size..." to set it (defaults
to 200,000; the desktop app's own status popup can tell you the real number for
your current plan/model, e.g. 1,000,000 for a 1M-context Sonnet 5 session).

## Running from source

```
dotnet run --project ClaudeUsageHUD
```

Or build and run the .exe directly from `ClaudeUsageHUD\bin\Debug\net10.0-windows\`.

- Tray icon (small orange circle, "C") → right-click for the menu: Show/Hide HUD,
  Set context window size, Sign in for live usage, Set usage token (a manual
  fallback), Restart, Exit.
- The HUD itself is a small borderless panel showing three lines - context-window
  usage, weekly plan usage, and rolling 5-hour usage - draggable by clicking
  anywhere on it; its position is remembered (`%AppData%\ClaudeUsageHUD\settings.json`)
  between launches, including across multiple monitors.
- If weekly/5h usage ever gets stuck reading (rare, and generally self-heals
  within a few minutes on its own), the tray menu's "Restart" is the fastest fix.

## Auto-start with Windows

A shortcut in `shell:startup` (`%AppData%\Microsoft\Windows\Start Menu\Programs\Startup\Claude Usage HUD.lnk`)
launches the app on login - it points at the **Release** build
(`ClaudeUsageHUD\bin\Release\net10.0-windows\ClaudeUsageHUD.exe`), so rebuild
Release (not just Debug) for code changes to reach the auto-started copy.

## Opening in Visual Studio

Open `ClaudeUsageHUD.sln` at the repo root - it's a classic `.sln` (not the newer
`.slnx` format `dotnet new sln` defaults to now), for compatibility with older VS
versions.
