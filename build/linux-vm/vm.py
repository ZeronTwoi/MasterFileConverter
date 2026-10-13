"""Drives a Linux guest through its serial console (QEMU -serial tcp:127.0.0.1:4555,server).

    python vm.py login                     wait for the login prompt and log in as root
    python vm.py run "<command>" [timeout] run a shell command, print its output and exit code
    python vm.py fetch <guest-file> <host-file>   copy a file out of the guest (gzip + base64 over the console)
"""
import base64
import gzip
import re
import socket
import sys
import time

PORT = 4555


class Console:
    def __init__(self):
        self.sock = socket.create_connection(("127.0.0.1", PORT), timeout=10)
        self.sock.settimeout(1.0)
        self.buffer = b""

    def send(self, text):
        self.sock.sendall(text.encode("utf-8"))

    def read_until(self, pattern, timeout):
        """Reads until the regex matches; returns everything read (as text) or None on timeout."""
        deadline = time.time() + timeout
        regex = re.compile(pattern)
        while time.time() < deadline:
            text = self.buffer.decode("utf-8", errors="replace")
            if regex.search(text):
                self.buffer = b""
                return text
            try:
                chunk = self.sock.recv(65536)
                if chunk:
                    self.buffer += chunk
            except socket.timeout:
                pass
        return None

    def run(self, command, timeout=600):
        marker = "FCDONE%d" % int(time.time() * 1000)
        # the marker is assembled by the shell, so the echoed command line cannot match it
        self.send("%s; echo \"%s\"\"_$?_\"\n" % (command, marker))
        text = self.read_until(re.escape(marker) + r"_(\d+)_", timeout)
        if text is None:
            tail = self.buffer.decode("utf-8", errors="replace")[-3000:]
            self.buffer = b""
            return None, tail
        code = int(re.search(re.escape(marker) + r"_(\d+)_", text).group(1))
        body = re.sub(re.escape(marker) + r"_\d+_.*", "", text, flags=re.S)
        body = re.sub(r"\x1b\[\?2004[hl]", "", body)
        return code, body.replace("\r", "")


def main():
    action = sys.argv[1]
    console = Console()

    if action == "login":
        console.send("\n")
        deadline = time.time() + float(sys.argv[2]) if len(sys.argv) > 2 else time.time() + 900
        while time.time() < deadline:
            text = console.read_until(r"login: |[#$] $", 20)
            if text is None:
                console.send("\n")
                continue
            if "login: " in text:
                console.send("root\n")
                time.sleep(3)
                console.send("\n")
                continue
            break
        else:
            print("no login prompt")
            return 1
        # a plain, predictable shell
        console.send("export TERM=dumb PS1='# '; stty -echo cols 250 2>/dev/null; bind 'set enable-bracketed-paste off' 2>/dev/null\n")
        time.sleep(1)
        code, out = console.run("uname -sr; cat /etc/debian_version", 60)
        print("logged in:", code, out.strip())
        return 0

    if action == "run":
        timeout = float(sys.argv[3]) if len(sys.argv) > 3 else 600
        code, out = console.run(sys.argv[2], timeout)
        sys.stdout.buffer.write(out.encode("utf-8", errors="replace"))
        print("\n[exit %s]" % ("TIMEOUT" if code is None else code))
        return 0 if code == 0 else 1

    if action == "script":
        # a shell script kept on the host: no quoting to get wrong on the way in
        timeout = float(sys.argv[3]) if len(sys.argv) > 3 else 600
        with open(sys.argv[2], "rb") as source:
            encoded = base64.b64encode(source.read().replace(b"\r\n", b"\n")).decode("ascii")
        code, out = console.run("echo %s | base64 -d > /tmp/fc-script.sh" % encoded, 60)
        if code != 0:
            print("could not send the script:", out[-300:])
            return 1
        code, out = console.run("bash /tmp/fc-script.sh 2>&1", timeout)
        sys.stdout.buffer.write(out.encode("utf-8", errors="replace"))
        print("\n[exit %s]" % ("TIMEOUT" if code is None else code))
        return 0 if code == 0 else 1

    if action == "wait":
        # wait until a file exists in the guest (a background job's "done" marker)
        deadline = time.time() + (float(sys.argv[3]) if len(sys.argv) > 3 else 3600)
        while time.time() < deadline:
            code, _ = console.run("test -f '%s'" % sys.argv[2], 60)
            if code == 0:
                print("done")
                return 0
            time.sleep(20)
        print("timed out waiting for", sys.argv[2])
        return 1

    if action == "fetch":
        code, out = console.run("echo BEGIN64; gzip -c '%s' | base64 -w0; echo; echo END64" % sys.argv[2], 300)
        match = re.search(r"BEGIN64\n(.*?)\nEND64", out, flags=re.S)
        if code != 0 or not match:
            print("fetch failed:", code, out[-500:])
            return 1
        data = gzip.decompress(base64.b64decode(match.group(1).strip()))
        with open(sys.argv[3], "wb") as target:
            target.write(data)
        print("fetched %d bytes" % len(data))
        return 0

    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main())
