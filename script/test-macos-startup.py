#!/usr/bin/env python3
"""Exercise an unmodified app's real startup in a disposable macOS CI account.

The process must stay alive and reach app.ready. Unlike a render command, this
includes state loading, login item registration, edge panels and updater setup.
Use only a disposable account: normal startup may migrate application data and
register a login item, just as launching the app in Finder does.
"""
import argparse
import json
import os
import pathlib
import re
import shutil
import signal
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--expect-foreground", action="store_true",
                        help="Require the manual-launch and reopen behavior added in 0.2.3")
    parser.add_argument("--disposable-account", action="store_true", required=True,
                        help="Confirm this is a disposable account, not a user's working profile")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    executable = args.app.resolve() / "Contents/MacOS/YTray"
    logs = pathlib.Path.home() / "Library/Application Support/YTray/Logs"
    reports = pathlib.Path.home() / "Library/Logs/DiagnosticReports"
    failed = False
    results = []
    try:
        for attempt, mode in enumerate(["launch-services", "launch-services", "direct", "direct"], 1):
            log = logs / "ytray.log"
            previous = log.read_text() if log.exists() else ""
            with (args.output / f"launch-{attempt}.txt").open("w") as output:
                command = [str(executable)] if mode == "direct" else ["open", "-n", "-W", str(args.app.resolve())]
                process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT)
                app_pid = process.pid if mode == "direct" else None
                try:
                    time.sleep(20)
                    code = process.poll()
                    current = log.read_text() if log.exists() else ""
                    added = current[len(previous):] if current.startswith(previous) else current
                    if mode != "direct":
                        starts = re.findall(r"\[app.start\] \[pid:(\d+)\]", added)
                        app_pid = int(starts[-1]) if starts else None
                    ready = app_pid is not None and any(
                        "[app.ready]" in line and f"[pid:{app_pid}]" in line
                        for line in added.splitlines())
                    alive = False
                    if app_pid is not None:
                        try:
                            os.kill(app_pid, 0)
                            alive = True
                        except ProcessLookupError:
                            pass
                    result = dict(attempt=attempt, mode=mode, pid=app_pid, exit=code, ready=ready, alive=alive)
                    results.append(result)
                    print(json.dumps(result), flush=True)
                    failed |= code is not None or not ready or not alive
                    if args.expect_foreground:
                        presented = any("[app.presentation]" in line and f"[pid:{app_pid}]" in line
                                        and "manager_visible=true" in line for line in added.splitlines())
                        failed |= not presented
                        if alive:
                            # Reopen the existing app, without -n, as a second Finder double-click does.
                            subprocess.run(["open", str(args.app.resolve())], check=True, timeout=10)
                            time.sleep(2)
                            reopened = log.read_text()[len(current):]
                            visible = any("[app.reopen]" in line and f"[pid:{app_pid}]" in line
                                          and "manager_visible=true; minimized=false" in line
                                          for line in reopened.splitlines())
                            result.update(presented=presented, reopened=visible)
                            failed |= not visible
                        else:
                            result.update(presented=presented, reopened=False)
                finally:
                    if app_pid is not None:
                        try:
                            os.kill(app_pid, signal.SIGTERM)
                        except ProcessLookupError:
                            pass
                    if process.poll() is None:
                        try:
                            process.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait()
        if args.expect_foreground:
            smoke = subprocess.run([str(executable), "--smoke-reopen"], capture_output=True, text=True, timeout=30)
            (args.output / "reopen-smoke.txt").write_text(smoke.stdout + smoke.stderr)
            failed |= smoke.returncode != 0 or "reopen smoke: closed=true minimized=true" not in smoke.stdout
    finally:
        (args.output / "results.json").write_text(json.dumps(results, indent=2))
        (args.output / "system.txt").write_text(subprocess.check_output(["sw_vers"], text=True))
        if logs.exists():
            shutil.copytree(logs, args.output / "logs", dirs_exist_ok=True)
        if reports.exists():
            for report in reports.glob("YTray*"):
                if report.is_file():
                    shutil.copy2(report, args.output)
    if failed:
        raise SystemExit("YTray failed the real startup check; inspect captured evidence")


if __name__ == "__main__":
    main()
