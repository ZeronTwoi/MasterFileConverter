# What changed in each version

The part for a version is also what its page under
[Releases](https://github.com/ZeronTwoi/MasterFileConverter/releases) says.

## 2.4.0

- **Updates install themselves.** When a new version is out, one click on the warning sign
  downloads it, checks it against its checksum and installs it; the app comes back by itself.
  With files in the list it waits for *Restart and update* instead of closing on you — or
  installs when you close the app. Nothing is ever downloaded or installed without that click.
  From a terminal: `--update`, or `--update --from <file>` with a download that is already
  there, for a machine that is not online.
- **A look for a new version on request** (··· menu → *Look for a new version now*), for those
  who do not want the app to look by itself.
- **Uninstalling removes everything:** the app, its shortcuts and registry entries, the engines
  it downloaded, a taskbar pin, leftover work files — and your settings, unless you choose to
  keep them. On Linux: `masterfileconverter --uninstall`. The uninstaller asks you to close the
  app first if it is still open, instead of leaving files behind.
- **The installer** makes the Start menu shortcut a choice like the others, says on its first
  page what it does and does not put on the computer, closes a running copy of the app before
  replacing it, and shows the app's logo in Windows' dark mode too.
- **Closing the window ends everything.** A conversion that is still running is stopped (after
  asking) and its half-written file removed; no encoder keeps running after the window is gone.
- Work files lie in the temp folder under the app's name, and ones a crash left behind are
  cleared away at a later start.
- ··· menu → *Website and source code*.

## 2.3.0

- The app can tell you when a new version is out — only if you say yes when it asks, once.
- The Windows installer has proper pages: the licence, where to install, what to add, a summary.

## 2.2.0

- First public release: MIT licence, a Windows installer, and downloaded engines checked against
  their publishers' checksums.
