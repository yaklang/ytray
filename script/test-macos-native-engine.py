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


def verify(engine_path, proto_path, command, directory, websocket=False):
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
    args = [engine_path, "grpc", "--frontend", "yakit", "--transport", "unix",
            "--socket-path", str(endpoint), "--local-password", password]
    if websocket:
        args += ["--browser-extension-bridge-port", "64334"]
    with log.open("wb") as output:
        engine = subprocess.Popen(args, env=env, stdout=output, stderr=subprocess.STDOUT)
    try:
        def snapshot():
            resources = request("GET", "/snapshot")
            status = next(item for item in resources if item["ResourceType"] == "status")
            return resources, json.loads(status["Extra"][0]["Value"])

        deadline = time.monotonic() + 60
        while True:
            if engine.poll() is not None or time.monotonic() >= deadline:
                raise RuntimeError("Yak IPC startup failed")
            try:
                request("POST", "/pairing-window", {"ttlSeconds": 120})
                break
            except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
                time.sleep(0.25)

        test_env = dict(os.environ)
        _, state = snapshot()
        url = state.get("url", "")
        if websocket:
            if not url.startswith("ws://127.0.0.1:"):
                raise RuntimeError("Legacy Yak did not start its local WebSocket bridge")
            test_env["YTRAY_TEST_LEGACY_ENGINE_URL"] = url
        else:
            if not url.startswith("unix:"):
                raise RuntimeError(f"Yak default bridge did not use a Unix socket: {url}")
            test_env["YTRAY_TEST_NATIVE_CONNECTION_FILE"] = str(connected)
        test = subprocess.Popen(command, env=test_env)
        try:
            deadline = time.monotonic() + (300 if websocket else 180)
            approved = set()
            while not connected.exists() and test.poll() is None and time.monotonic() < deadline:
                resources, state = snapshot()
                for resource in resources:
                    if resource["ResourceType"] == "pairing-request":
                        pairing = resource["ResourceName"]
                        if pairing in approved:
                            continue
                        if websocket and approved:
                            raise RuntimeError("Upgraded profile unexpectedly requested another approval")
                        request("POST", f"/pairings/{pairing}/approve", {"name": "YTray CI"})
                        approved.add(pairing)
                if state["connected"] and not websocket:
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
            if websocket:
                if test.poll() != 0 or len(approved) != 1:
                    raise RuntimeError("Legacy mixed-upgrade check failed")
                print("PASS old Yak received exactly one pairing approval across all browser launches", flush=True)
            elif not connected.exists() or test.wait(timeout=85) != 0:
                raise RuntimeError("Real Chrome Native Messaging integration failed")
        finally:
            stop(test)
    except (RuntimeError, subprocess.SubprocessError, KeyError):
        print(log.read_text(errors="replace")[-6000:].replace(password, "***"), file=sys.stderr)
        raise RuntimeError("Legacy engine smoke failed" if websocket else "Native engine smoke failed") from None
    finally:
        stop(engine)


if __name__ == "__main__":
    args = sys.argv[1:]
    websocket = args[0] == "--websocket"
    if websocket:
        args = args[1:]
    with tempfile.TemporaryDirectory(prefix="ytray-native-", dir="/tmp") as scratch:
        verify(args[0], Path(args[1]).resolve(), args[2:], scratch, websocket)
