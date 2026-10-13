#!/bin/sh
# Installs @DisplayName@ for the current user: no root needed, nothing outside your home folder.
#   ./install.sh                            install (or update)
#   ./install.sh --remove                   uninstall: the app, the engines it downloaded, its settings
#   ./install.sh --remove --keep-settings   uninstall, but leave the settings for a later installation
# Once installed, "@UnixName@ --uninstall" does the same from anywhere.
#@ This is the script as the repository keeps it: the names between @ signs are filled in, and
#@ lines like these two left out, when a download is built (FcArrangeDownload in FileConverter.csproj).
set -e

here=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
config=${XDG_CONFIG_HOME:-$HOME/.config}
home="$HOME/.local/lib/@UnixName@"
bin="$HOME/.local/bin"
entry="$data/applications/@UnixName@.desktop"

# what an installation puts outside its own folder
remove_links() {
    rm -f "$bin/$1" "$data/applications/$1.desktop" "$data/icons/hicolor/scalable/apps/$1.svg"
    rm -f "$data"/icons/hicolor/*/apps/"$1".png
}

refresh_menu() {
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" >/dev/null 2>&1 || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$data/icons/hicolor" >/dev/null 2>&1 || true
}

if [ "$1" = "--remove" ]; then
    rm -rf "$home"
    remove_links "@UnixName@"
    refresh_menu
    # what the app made itself: the engines it downloaded and what it measured about this machine,
    # and work files of a conversion that ended without tidying up (they carry the app's name)
    rm -rf "$data/@UnixName@"
    rm -rf "${TMPDIR:-/tmp}"/@ProgramName@-*
    if [ "$2" = "--keep-settings" ] && [ -f "$config/@UnixName@/settings.json" ]; then
        for entry in "$config/@UnixName@"/* "$config/@UnixName@"/.[!.]*; do
            [ "$(basename "$entry")" = "settings.json" ] || rm -rf "$entry"
        done
        echo "@DisplayName@ was removed, with the engines it had downloaded. Your settings were kept: $config/@UnixName@/settings.json"
    else
        rm -rf "$config/@UnixName@"
        echo "@DisplayName@ was removed, with the engines it had downloaded and its settings."
    fi
    exit 0
fi

# Up to version 2.0 the app was called FileConverter. Its program is replaced by this one, so
# that the menu does not show two; its settings and engines are taken over at the first start.
if [ -e "$HOME/.local/lib/fileconverter" ] || [ -e "$data/applications/fileconverter.desktop" ]; then
    rm -rf "$HOME/.local/lib/fileconverter"
    remove_links "fileconverter"
    echo "The earlier version (FileConverter) was replaced."
fi

mkdir -p "$bin" "$data/applications" "$data/icons"
if [ "$here" != "$home" ]; then
    rm -rf "$home"
    mkdir -p "$home"
    cp -R "$here"/. "$home"/
fi
chmod +x "$home/@UnixName@" "$home/install.sh"

ln -sf "$home/@UnixName@" "$bin/@UnixName@"
cp -R "$home/app/share/icons/." "$data/icons/"
sed "s|^Exec=@UnixName@|Exec=$bin/@UnixName@|" "$home/app/share/applications/@UnixName@.desktop" > "$entry"
refresh_menu

echo "@DisplayName@ is installed. Start it from your applications menu, or run: @UnixName@"
echo "To remove it again: $home/install.sh --remove"
case ":$PATH:" in
    *":$bin:"*) ;;
    *) echo "Note: $bin is not on your PATH, so in a terminal use the full path: $bin/@UnixName@" ;;
esac
if ! command -v ffmpeg >/dev/null 2>&1 && [ ! -x "$data/@UnixName@/tools/ffmpeg" ] && [ ! -x "$data/fileconverter/tools/ffmpeg" ]; then
    echo "FFmpeg was not found. The app offers to download it (\"Set up engines\"); an installed FFmpeg 9 or newer is used too."
fi
