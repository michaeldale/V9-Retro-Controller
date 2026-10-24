# Changelog

All notable changes to V9 Retro Controller. Dates are in YYYY-MM-DD. The top
section must match `<Version>` in `Directory.Build.props`; the release scripts
refuse to build or publish otherwise, and the top section becomes the release
notes (on GitHub and in the in-app updater).

## 0.1.0 (2026-10-24)

First public release. Windows only for now: a self-contained single-file exe
(`V9RetroController-0.1.0-win-x64.exe`, no install, no .NET needed). The macOS
app builds from the same source (`scripts/build-mac.sh`) and will be added to
a later release.

### Added
- **Fleet.** Dashboard with online / offline / rebooting counts, a card per
  machine with its latest screenshot, and recent activity; a sortable,
  filterable agent table with bulk poll, screenshot and pause; add and edit
  with a Test connection button.
- **Discovery.** Finds agents on the LAN over mDNS (`_v9xremote._tcp`, agent
  0.8.0+). Add them, or control one directly without saving. Saved agents are
  tracked by the agent's install UUID, so a DHCP address change is followed
  automatically and a different machine at a saved address is flagged.
- **Quick connect and command palette.** Type an address to control a machine
  without saving it; Ctrl+K jumps to any agent or action.
- **Status polling** in the background (also while the window is closed to the
  tray), with the same rules as the Bluetrait plugin: reboot proof by boot
  counter and token, shutdown is not an outage, offline after N failures.
- **Per agent:** live screenshots and history, inventory (CPU, memory, drives,
  display, network, Windows, BIOS, devices with problem codes), a command
  console with streamed output and cancel, a file browser (upload, download,
  drag and drop, delete, hash, view text), processes, a read-only registry
  browser, the window list, click and type through the agent's input when VNC
  is off, and reboot / shut down / hard reset.
- **Built-in VNC viewer** for the agent's V9XVNC.EXE: fit or actual size,
  view only, Ctrl+Alt+Del and special keys, clipboard both ways, pop-out
  window, saved password (encrypted for your Windows account). VNC can be
  checked, started, stopped, enabled and disabled from the app.
- **Agent updates.** From michaeldale.com.au directly (agent 0.13.1+ downloads
  and stages its own update, then it is hot-swapped in), through this PC (any
  agent from 0.6.0), or from a package folder. Every server release is checked
  against the V9x release signing key first.
- **App updates.** Settings → Updates checks michaeldale.com.au, optionally at
  startup, and installs a signed release in place (Windows exe now, macOS app
  later).
- **Remote access.** One Windows PC can host its agents, history and polling
  for other copies of the app (a Mac, say) over HTTPS with per-client tokens
  and a pinned self-signed certificate; agent and VNC connections are
  tunnelled through the host, to private LAN addresses only.
- Selectable text everywhere it matters, and copy from every table.
