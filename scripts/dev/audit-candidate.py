"""Bounded publication audit; heuristic secret detection, not a certified scanner."""
import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[2]


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--staged", action="store_true")
    args = parser.parse_args()
    names = git("diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z") if args.staged else git("ls-files", "-co", "--exclude-standard", "-z")
    paths = sorted(set(n.decode("utf-8") for n in names.split(b"\0") if n))
    failures = []
    rules = {
        "private-key": rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
        "github-token": rb"(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})",
        "literal-bearer": rb"Bearer [A-Fa-f0-9]{64}(?![A-Fa-f0-9])",
        "api-secret": rb"sk-(?:proj-)?[A-Za-z0-9_-]{40,}",
    }
    for name in paths:
        path = pathlib.PurePosixPath(name)
        if set(path.parts) & {".local", ".tools", "artifacts", "bin", "obj", "__pycache__"} or path.suffix.lower() in {".pfx", ".p12", ".pem", ".key", ".env"}:
            failures.append([name, "excluded-publication-path"])
        data = git("show", ":" + name) if args.staged else (ROOT / name).read_bytes()
        for label, pattern in rules.items():
            if re.search(pattern, data):
                failures.append([name, label])
        if path.suffix == ".shortcut":
            expected = {"CB 发送.shortcut": "4fa041833570714d0fed7590808d0c6dd59368a7cf770ed681630f7d72d82f61",
                        "CB 取回.shortcut": "79f37ea5f815e751aa4836db83cd4cb60b826f0afbc85c5a6c61d3977a0d28bc"}
            if hashlib.sha256(data).hexdigest() != expected.get(path.name):
                failures.append([name, "shortcut-provenance-mismatch"])
    for product in ["ContinuityBridge.App", "ContinuityBridge.Relay"]:
        pending = [ROOT / "src" / product / (product + ".csproj")]
        seen = set()
        while pending:
            project = pending.pop().resolve()
            if project in seen:
                continue
            seen.add(project)
            if any(x in project.stem for x in ("Qa.", "TestAgent", ".Api")):
                failures.append([product, "forbidden-product-dependency:" + project.stem])
            relative = project.relative_to(ROOT).as_posix()
            data = git("show", ":" + relative) if args.staged else project.read_bytes()
            for reference in ET.fromstring(data).iter("ProjectReference"):
                pending.append(project.parent / reference.attrib["Include"].replace("\\", "/"))
    print(json.dumps({"filesChecked": len(paths), "heuristicOnly": True, "failures": failures}, ensure_ascii=False))
    return bool(failures)


if __name__ == "__main__":
    raise SystemExit(main())
