#!/usr/bin/env python3
"""Exercise an unmodified app's real startup in a disposable macOS CI account.

The process must stay alive and reach app.ready. Unlike a render command, this
includes state loading, login item registration, edge panels and updater setup.
Use only a disposable account: normal startup may migrate application data and
register a login item, just as launching the app in Finder does.
"""
import argparse
import pathlib
import shutil
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    executable = args.app.resolve() / "Contents/MacOS/YTray"
    logs = pathlib.Path.home() / "Library/Application Support/YTray/Logs"
    reports = pathlib.Path.home() / "Library/Logs/DiagnosticReports"
    failed = False
    try:
        for attempt in range(1, 3):
            with (args.output / f"launch-{attempt}.txt").open("w") as output:
                process = subprocess.Popen([str(executable)], stdout=output, stderr=subprocess.STDOUT)
                try:
                    time.sleep(20)
                    code = process.poll()
                    log = logs / "ytray.log"
                    ready = log.exists() and any(
                        "[app.ready]" in line and f"[pid:{process.pid}]" in line
                        for line in log.read_text().splitlines()
                    )
                    print(f"attempt={attempt} pid={process.pid} exit={code} ready={ready}", flush=True)
                    failed |= code is not None or not ready
                finally:
                    if process.poll() is None:
                        process.terminate()
                        try:
                            process.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait()
    finally:
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
