# Network Share Quick Search (NSQS)

A Windows tray app that indexes folder names on network shares (UNC paths only) and opens a
Spotlight-style search overlay to find and open them in Explorer.

Configure one or more `\\server\share` roots in Settings. NSQS walks the tree in the
background, stores folder names in a local SQLite FTS5 index, and lets you filter results by
share before opening a match.

## Requirements

The [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64). The installer
checks for it and points you at the download if it is missing.

Network shares must be reachable from your PC (mapped drives are converted to UNC when added).

## Install

Two options, both framework-dependent (~2 MB each — SQLite native libraries are bundled, .NET is not):

| Artifact | What it does |
|----------|----------------|
| **`NSQS-<version>-setup.exe`** | Per-user install (no admin prompt) to `%LocalAppData%\Programs\NSQS`, Start Menu entry, uninstaller. |
| **`NSQS-<version>-portable.exe`** | Single exe — run from anywhere. |

If the .NET runtime is missing, the installer offers to open the download page; the portable exe
shows the standard .NET "You must install .NET" dialog.

**Autostart** records the exe's absolute path. If you move the portable exe, the app repoints the
registry entry the next time you run it.

Uninstalling the setup build removes the program and autostart entry but **leaves your settings and
index** in `%AppData%\NSQS`.

## Run in development

```
dotnet run --project Nsqs
```

Open the solution with `NSQS.sln`.

## Build a release

```
.\build.ps1
```

Produces in `dist\`:

- `NSQS-<version>-portable.exe`
- `NSQS-<version>-setup.exe`

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php) at its default location. The version
comes from `<Version>` in `Nsqs/Nsqs.csproj`.

To bundle the .NET runtime so no prerequisite is needed:

```
dotnet publish Nsqs -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

That yields a single ~160 MB exe with no separate installer or .NET install required.

## Usage

- Runs from the tray by default. Press the launcher hotkey (default **Ctrl+Shift+Space**) or
  left-click the tray icon to open search.
- **Right-click** the tray icon → Open search, Settings, Rebuild index now, Start with Windows,
  Exit.

### Search overlay

| Action | Keys |
|--------|------|
| Move selection | ↑ ↓ |
| Open folder in Explorer | Enter |
| Close overlay | Esc |
| Cycle focus (search → filter → export) | Tab / Shift+Tab |
| Toggle share filter (in dropdown) | Space |
| Export current results to CSV | Ctrl+E, or Tab to **Export CSV** then Enter |

The share filter dropdown limits results to selected UNC roots. With nothing selected, all
configured shares are searched.

### Settings

| Setting | Purpose |
|---------|---------|
| **Network share roots** | UNC paths to index. Browse or paste; mapped drives become UNC. |
| **Launcher hotkey** | Global shortcut to show/hide the overlay. |
| **Scheduled index rebuild** | Daily or weekly full re-index at a local time. |
| **Run missed index on startup** | Catch up if the PC was off at the scheduled time. |
| **Launch to tray** | When on (default), only the tray icon is shown. When off, the overlay opens on startup and the app stays on the taskbar. |
| **Start with Windows** | Per-user autostart via `HKCU\...\Run` (`NSQS`). |
| **Export CSV…** (index status) | Export the full index to CSV. |

Settings and index data:

```
%AppData%\NSQS\settings.json
%AppData%\NSQS\index.db
%AppData%\NSQS\debug.log
```

Only one instance runs at a time. Launching again activates the existing overlay.

### Indexing

NSQS keeps a local search index on disk. It is updated in two ways:

1. **Full rebuild** — walks every folder under your configured share roots. Runs on first launch,
   on a schedule (Settings), via **Rebuild index now**, or when a missed scheduled run is caught on
   startup.
2. **Live folder watch** (v1.1+) — while the app is running, new folders created on a watched
   share are added to the index within a few seconds. Renames and deletes are reflected too.

The watcher supplements full rebuilds; it does not replace them. Scheduled rebuilds still run as
before and correct anything the network share notifications may have missed.

## Troubleshooting

| Symptom | Things to check |
|---------|-----------------|
| New folder not in search | The app must be running and the share reachable. Watch updates take a few seconds; use **Rebuild index now** if needed. |
| Index never updates | Scheduled rebuild requires the app to be running (or use **Run missed index on startup**). |
| Hotkey does nothing | Another app may own the same shortcut — pick a different hotkey in Settings and save. |
| Share unreachable during index | Check network/VPN access to the UNC path; errors are logged to `debug.log`. |
| Settings kept after uninstall | By design — only the program folder is removed. Delete `%AppData%\NSQS` manually to reset. |

## Project layout

```
Nsqs/           Main WPF tray application
tools/IconGen/  Generates app.ico for the exe and installer
installer/      Inno Setup script (NSQS.iss)
build.ps1       Publish and build both release artifacts
```
