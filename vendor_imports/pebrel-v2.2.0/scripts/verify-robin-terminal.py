"""Exercise a packaged serial client through raw pipes and Pebrel's real ConPTY.

Uses only a new QA data directory and ephemeral loopback sockets, never COM ports.
"""
import argparse
import json
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app-dir", type=Path, required=True)
    parser.add_argument("--qa-dir", type=Path, required=True)
    args = parser.parse_args()
    app = args.app_dir.resolve()
    args.qa_dir.mkdir(parents=True, exist_ok=True)
    qa = Path(tempfile.mkdtemp(prefix="native-pty-", dir=args.qa_dir.resolve()))
    gui = app / "pebrel.exe"
    helper = app / "runtime" / "pebrel-serial.exe"
    for executable in (gui, helper):
        if not executable.is_file():
            raise FileNotFoundError(executable)
    flags = subprocess.CREATE_NO_WINDOW

    # Redirected mode must preserve every byte, including Ctrl+C, NUL and ANSI.
    incoming = "中文".encode() + b"\x1b[A\x1b[B\r\n\0\x03" + bytes(range(256)) * 3
    outgoing = "中文 ●▂▄▆█ UTF-8\r\n".encode() + b"\x1b[31mred\x1b[0m\0" + incoming
    received = bytearray()
    errors = []
    server = socket.socket()
    server.bind(("127.0.0.1", 0))
    server.listen(1)
    server.settimeout(15)
    port = server.getsockname()[1]

    def raw_server():
        try:
            conn, _ = server.accept()
            with conn:
                conn.settimeout(15)
                while packet := conn.recv(4096):
                    received.extend(packet)
                for at in range(0, len(outgoing), 7):
                    conn.sendall(outgoing[at:at + 7])
        except Exception as error:
            errors.append(repr(error))
        finally:
            server.close()

    worker = threading.Thread(target=raw_server)
    worker.start()
    raw = subprocess.run(
        [str(helper), "--host", "127.0.0.1", "--port", str(port)],
        input=incoming, capture_output=True, timeout=20, creationflags=flags,
    )
    worker.join(16)
    assert not errors, errors
    assert raw.returncode == 0, raw.stderr
    assert bytes(received) == incoming
    assert raw.stdout == outgoing

    # A private runtime identity prevents writes to any existing user pane.
    data = qa / "data"
    data.mkdir()
    (data / ".pebrel-migration-v1").write_text("Isolated terminal QA.\n", encoding="utf-8")
    (data / "pebrel_settings.txt").write_text(
        "language=zh-CN\nai_hooks=0\nghost=0\nauto_check_updates=0\nshell=cmd\n",
        encoding="utf-8",
    )

    def cli(*arguments):
        result = subprocess.run(
            [str(gui), "--data-dir", str(data), *map(str, arguments)],
            capture_output=True, timeout=25, creationflags=flags,
        )
        assert result.returncode == 0, (result.stderr, result.stdout)
        response = json.loads(result.stdout)
        assert response["ok"], response
        return response["result"]

    server = socket.socket()
    server.bind(("127.0.0.1", 0))
    server.listen(1)
    server.settimeout(20)
    port = server.getsockname()[1]
    ready = threading.Event()
    release = threading.Event()
    received.clear()
    remote = "原生终端中文 ●▂▄▆█ UTF-8 正常"

    def pty_server():
        try:
            conn, _ = server.accept()
            with conn:
                conn.settimeout(0.2)
                for byte in ("\x1b[32m" + remote + "\x1b[0m\r\nRemote> ").encode():
                    conn.sendall(bytes([byte]))
                    time.sleep(0.002)
                ready.set()
                while not release.is_set():
                    try:
                        packet = conn.recv(128)
                        if not packet:
                            break
                        received.extend(packet)
                    except socket.timeout:
                        pass
        except Exception as error:
            errors.append(repr(error))
            ready.set()
        finally:
            server.close()

    worker = threading.Thread(target=pty_server)
    worker.start()
    process = subprocess.Popen(
        [str(gui), "--data-dir", str(data), "-e", str(helper),
         "--host", "127.0.0.1", "--port", str(port)],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags,
    )
    try:
        assert ready.wait(22) and not errors, errors
        assert cli("env")["runtime"]["reachable"]
        snapshot = cli("ctl", "snapshot")
        assert snapshot["process_id"] == process.pid, snapshot
        targets = [(window["id"], pane["id"]) for window in snapshot["windows"]
                   for tab in window["tabs"] for pane in tab["panes"]]
        assert len(targets) == 1, targets
        window, pane = targets[0]
        deadline = time.monotonic() + 12
        while True:
            # Include the whole viewport, not just blank rows at its bottom.
            screen = cli("pane", "read", pane, "--lines", 200)
            if remote in screen["text"]:
                break
            assert time.monotonic() < deadline, screen
            time.sleep(0.25)
        for key in ("up", "down", "enter"):
            cli("ctl", "send-key", "--window", window, "--pane", pane, "--key", key)
        deadline = time.monotonic() + 3
        while len(received) < 7 and time.monotonic() < deadline:
            time.sleep(0.02)
        assert bytes(received) == b"\x1b[A\x1b[B\r", bytes(received)
        result = {"raw_pipe_bytes": len(outgoing), "pty_utf8": "passed",
                  "key_bytes": bytes(received).hex(" "), "screen": screen["text"]}
    finally:
        release.set()
        worker.join(5)
        assert process.wait(timeout=20) == 0
    assert not errors, errors
    (qa / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"ok": True, "evidence": str(qa / "result.json"), **result}, ensure_ascii=False))


if __name__ == "__main__":
    main()
