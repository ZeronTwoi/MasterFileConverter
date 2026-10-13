#!/bin/sh
# Builds the downloads: one self-contained folder and archive per system (see publish.ps1 for the
# same thing on Windows). They are named <name>-<version>-<runtime>, after Directory.Build.props.
#   build/publish.sh              both systems
#   build/publish.sh linux-x64    just Linux
#   build/publish.sh win-x64
set -e

root=$(cd "$(dirname "$0")/.." && pwd)
said() { sed -n "s:.*<$1>\(.*\)</$1>.*:\1:p" "$root/Directory.Build.props" | head -1; }
program=$(said FcProgramName)
version=$(said Version)
runtimes=${1:-"win-x64 linux-x64"}

for rid in $runtimes; do
    name="$program-$version-$rid"
    folder="$root/artifacts/$name"
    rm -rf "$folder"

    echo "Publishing $name ..."
    dotnet publish "$root/FileConverter/FileConverter.csproj" -c Release -r "$rid" -o "$folder" --nologo -v q

    case "$rid" in
        win-*) archive="$root/artifacts/$name.zip" ;;
        *)     archive="$root/artifacts/$name.tar.gz" ;;
    esac
    dotnet run "$root/build/Pack.cs" -- "$folder" "$archive"
    echo "  $name: $(du -sm "$folder" | cut -f1) MB unpacked"
done
