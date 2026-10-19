"""Merges logs/<App>.log (one JSON object per line) into one stream sorted by time. Called by scripts/logs.sh."""
import json
import re
import sys
from pathlib import Path


def main() -> int:
    args = sys.argv[1:]
    directory, args = Path(args[0]), args[1:]
    wanted_id = wanted_app = None
    raw = False
    while args:
        flag = args.pop(0)
        if flag == "--id":
            wanted_id = args.pop(0)
        elif flag == "--app":
            wanted_app = args.pop(0)
        elif flag == "--json":
            raw = True
        else:
            print(f"unknown argument: {flag}", file=sys.stderr)
            return 2

    entries = []
    for path in sorted(directory.glob("*.log")):
        app = path.stem
        if wanted_app and app.lower() != wanted_app.lower():
            continue
        for number, line in enumerate(path.read_text(errors="replace").splitlines()):
            try:
                entry = json.loads(line)
            except ValueError:
                continue  # a line that is not JSON (Envoy's startup warning, a stack trace)
            if not isinstance(entry, dict) or "ts" not in entry:
                continue
            if wanted_id and wanted_id not in (entry.get("requestId"), entry.get("id")):
                continue
            entries.append((entry["ts"], app, number, line, entry))

    entries.sort(key=lambda e: (e[0], e[1], e[2]))  # ISO timestamps sort as text
    for ts, app, _, line, entry in entries:
        print(line if raw else f"{ts[11:23]} {app:<17} {describe(entry)}")
    return 0


def describe(entry: dict) -> str:
    ids = ""
    request_id = entry.get("requestId") or entry.get("id")
    if request_id:
        ids = f"[{request_id}] "
    if "message" in entry:  # a .NET app: the template with its values put in
        return ids + re.sub(r"\{(\w+)\}", lambda m: render(entry, m.group(1)), entry["message"]) + (
            "\n" + entry["exception"] if "exception" in entry else "")
    # the Forwarder's access log
    return f"{ids}{entry.get('method')} {entry.get('authority')}{entry.get('path') or ''} -> {entry.get('upstream')} {entry.get('status')} from={entry.get('from')}"


def render(entry: dict, name: str) -> str:
    value = entry.get(name[:1].lower() + name[1:], entry.get(name, "-"))
    return "-" if value is None else str(value)


if __name__ == "__main__":
    sys.exit(main())
