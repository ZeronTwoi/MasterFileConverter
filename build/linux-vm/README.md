# Testing the Linux download without a Linux machine

For a Windows computer with no WSL and no Docker: a Debian system in the QEMU emulator, driven
through its serial console. No administrator rights and no installation are needed — QEMU is
only unpacked, and nothing outside the working folder is touched. Without hardware acceleration
the guest is roughly ten times slower than a real machine (the full self-test takes a while), but
it is a real Linux: a real kernel, real glibc, a real X server.

This is how the Linux build was checked while it was being written. A GitHub Actions run on
`ubuntu-latest` (see `.github/workflows/build.yml`) is the quicker check when one is available.

## Once: get the emulator and a system image

```powershell
$vm = "$env:TEMP\fc-linux-vm"; mkdir $vm
# QEMU for Windows (https://qemu.weilnetz.de/w64/): unpack the installer instead of running it
curl.exe -L -o $vm\qemu-setup.exe https://qemu.weilnetz.de/w64/qemu-w64-setup-20260811.exe
& "C:\Program Files\7-Zip\7z.exe" x $vm\qemu-setup.exe "-o$vm\qemu" -y

# Debian 12 "nocloud" image: root logs in on the console without a password
curl.exe -L -o $vm\debian-base.qcow2 https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-nocloud-amd64.qcow2
# compare with the debian-12-nocloud-amd64.qcow2 line of
# https://cloud.debian.org/images/cloud/bookworm/latest/SHA512SUMS
(Get-FileHash $vm\debian-base.qcow2 -Algorithm SHA512).Hash
```

## Each time: boot it with the build on a second disk

```powershell
build\publish.ps1 -Runtime linux-x64

# the tar.gz itself becomes the second disk; a raw disk must be a multiple of 512 bytes
Copy-Item artifacts\*-linux-x64.tar.gz $vm\payload.img
$pad = (512 - ((Get-Item $vm\payload.img).Length % 512)) % 512
if ($pad) { $f = [IO.File]::Open("$vm\payload.img", 'Append'); $f.Write((New-Object byte[] $pad), 0, $pad); $f.Close() }

# a throw-away disk on top of the untouched image
& $vm\qemu\qemu-img.exe create -f qcow2 -b $vm\debian-base.qcow2 -F qcow2 $vm\work.qcow2 12G

Start-Process -WindowStyle Hidden $vm\qemu\qemu-system-x86_64.exe -ArgumentList @(
    '-machine', 'q35', '-cpu', 'max', '-smp', '8', '-m', '6144', '-accel', 'tcg,thread=multi',
    '-drive', "file=$vm\work.qcow2,if=virtio,format=qcow2",
    '-drive', "file=$vm\payload.img,if=virtio,format=raw,readonly=on",
    '-nic', 'user,model=virtio-net-pci', '-display', 'none', '-monitor', 'none',
    '-serial', 'tcp:127.0.0.1:4555,server=on,wait=off')
```

## Drive it

`vm.py` (Python 3, nothing to install) talks to the guest's console on port 4555:

```powershell
python build\linux-vm\vm.py login                       # wait for the boot, log in as root
python build\linux-vm\vm.py run "cd /root && tar -xzf /dev/vdb 2>/dev/null; ls"
python build\linux-vm\vm.py run "cd /root/*-linux-x64 && ./masterfileconverter --version"
python build\linux-vm\vm.py run "cd /root/*-linux-x64 && ./masterfileconverter --setup-tools" 3000
python build\linux-vm\vm.py run "cd /root/*-linux-x64 && ./masterfileconverter --self-test" 5400

python build\linux-vm\vm.py script build\linux-vm\window.sh 1800   # open the window under Xvfb
python build\linux-vm\vm.py fetch /root/screen.xwd.gz screen.xwd.gz  # and bring a screenshot back
```

`run` takes a command and an optional timeout in seconds and prints the output and exit code;
`script` sends a shell script file and runs it (no quoting to get wrong); `fetch` copies a file out;
`wait <guest-file>` blocks until a file exists, for jobs started in the background with `nohup`.
`xwd2png.py` turns the fetched screen dump into a PNG.
The guest reaches the internet through the host, so `apt-get` and the app's own engine download
work. When finished, stop `qemu-system-x86_64.exe` and delete `work.qcow2`.
