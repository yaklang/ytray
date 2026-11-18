import base64
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import time


def stop(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


def verify(engine_path, proto_path, command, directory):
    root = Path(directory)
    endpoint = root / "grpc.sock"
    connected = root / "connected"
    password = secrets.token_hex(24)
    env = dict(os.environ, YAKIT_HOME=str(root / "yak"))
    env.pop("YAK_BROWSER_EXTENSION_BRIDGE_TRANSPORT", None)
    grpc = ["grpcurl", "-plaintext", "-unix", "-max-time", "3",
            "-H", f"authorization: Bearer {password}", "-import-path", str(proto_path.parent),
            "-proto", proto_path.name]

    def request(method, path, body=None):
        payload = {"Method": method, "Url": {"Schema": "browser-extension", "Path": path}}
        if body is not None:
            payload["Body"] = base64.b64encode(json.dumps(body).encode()).decode()
        response = subprocess.run(
            [*grpc, "-d", json.dumps(payload), str(endpoint), "ypb.Yak/RequestYakURL"],
            capture_output=True, text=True, timeout=5, check=True,
        )
        return json.loads(response.stdout)["Resources"]

    log = root / "yak.log"
    with log.open("wb") as output:
        engine = subprocess.Popen(
            [engine_path, "grpc", "--frontend", "yakit", "--transport", "unix",
             "--socket-path", str(endpoint), "--local-password", password],
            env=env, stdout=output, stderr=subprocess.STDOUT,
        )
    try:
        deadline = time.monotonic() + 60
        while True:
            if engine.poll() is not None or time.monotonic() >= deadline:
                raise RuntimeError("Yak IPC startup failed")
            try:
                request("POST", "/pairing-window", {"ttlSeconds": 120})
                break
            except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
                time.sleep(0.25)

        test = subprocess.Popen(command, env=dict(os.environ, YTRAY_TEST_NATIVE_CONNECTION_FILE=str(connected)))
        try:
            deadline = time.monotonic() + 180
            while not connected.exists() and test.poll() is None and time.monotonic() < deadline:
                resources = request("GET", "/snapshot")
                for resource in resources:
                    if resource["ResourceType"] == "pairing-request":
                        request("POST", f"/pairings/{resource['ResourceName']}/approve", {"name": "YTray CI"})
                status = next(item for item in resources if item["ResourceType"] == "status")
                state = json.loads(status["Extra"][0]["Value"])
                if not state.get("url", "").startswith("unix:"):
                    raise RuntimeError(f"Yak default bridge did not use a Unix socket: {state.get('url')}")
                if state["connected"]:
                    sockets = subprocess.run(
                        ["lsof", "-nP", "-a", "-p", str(engine.pid), "-iTCP", "-sTCP:LISTEN"],
                        capture_output=True, text=True, timeout=5,
                    )
                    if sockets.returncode != 1 or sockets.stdout or sockets.stderr:
                        raise RuntimeError(f"Yak TCP listener check failed: {sockets.stdout}{sockets.stderr}")
                    connected.touch()
                    print("Chrome authenticated through YTray Native Messaging and Yak Unix socket; Yak TCP listeners: 0", flush=True)
                else:
                    time.sleep(0.25)
            if not connected.exists() or test.wait(timeout=85) != 0:
                raise RuntimeError("Real Chrome Native Messaging integration failed")
        finally:
            stop(test)
    except (RuntimeError, subprocess.SubprocessError, KeyError):
        print(log.read_text(errors="replace")[-6000:].replace(password, "***"), file=sys.stderr)
        raise RuntimeError("Native engine smoke failed") from None
    finally:
        stop(engine)


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="ytray-native-", dir="/tmp") as scratch:
        verify(sys.argv[1], Path(sys.argv[2]).resolve(), sys.argv[3:], scratch)
