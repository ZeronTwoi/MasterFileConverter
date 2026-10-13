# Opens the app's window inside the guest on a virtual X display and saves what it shows.
#   python build/linux-vm/vm.py script build/linux-vm/window.sh 1800
#   python build/linux-vm/vm.py fetch /root/screen.xwd.gz screen.xwd.gz
#   python build/linux-vm/xwd2png.py screen.xwd.gz screen.png
set -e
export DEBIAN_FRONTEND=noninteractive
app=$(ls -d /root/*-linux-x64 | head -1)
# the program is called what its menu entry is called
name=$(basename "$app"/app/share/applications/*.desktop .desktop)
# a process list shows only the first 15 letters of a name
short=$(printf %.15s "$name")

# what a desktop already has: an X server, fontconfig and a font
if ! command -v Xvfb >/dev/null 2>&1; then
    apt-get update -q >/dev/null
    apt-get install -y -q --no-install-recommends xvfb libfontconfig1 fonts-dejavu-core >/dev/null
fi

cd /root

# a few real files to show in the list (made by the app's own self-test)
if [ ! -f /root/samples/photo.png ]; then
    "$app/$name" --self-test --keep --filter "png → jpg" >/dev/null 2>&1 || true
    mkdir -p /root/samples
    for sample in photo.png clip.mp4 report.pdf bundle.zip; do
        cp /tmp/"$name"-selftest-*/samples/$sample /root/samples/ 2>/dev/null || true
    done
fi

# stop what an earlier run left behind (a minimal system has no pkill)
for p in /proc/[0-9]*; do
    case "$(cat "$p/comm" 2>/dev/null)" in
        Xvfb|"$short") kill "${p#/proc/}" 2>/dev/null || true ;;
    esac
done
sleep 2
rm -rf /root/fb && mkdir -p /root/fb
Xvfb :1 -screen 0 1180x760x24 -fbdir /root/fb >/dev/null 2>&1 &
sleep 3

DISPLAY=:1 "$app/$name" /root/samples/* > /root/window.log 2>&1 &
pid=$!
# under emulation the first frame takes a while; wait for the process to settle
for i in $(seq 1 ${FC_WINDOW_WAIT:-60}); do
    sleep 1
    kill -0 $pid 2>/dev/null || { echo "the app exited:"; cat /root/window.log; exit 1; }
done

cp /root/fb/Xvfb_screen0 /root/screen.xwd
gzip -f /root/screen.xwd
echo "window is up (pid $pid); screenshot saved: $(ls -la /root/screen.xwd.gz | awk '{print $5}') bytes"
echo "--- app log ---"
head -20 /root/window.log
