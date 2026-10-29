"""Verify a Package.ps1 ZIP against the current workspace, without extracting it.

Uses only Python's standard library. Freeze dist/source/evidence before running.
The JSON report belongs outside packaged inputs (for example build/ or release/).
This checks package integrity, not gameplay or the outcome of embedded test logs.
"""
import argparse
from datetime import datetime, timezone
import fnmatch
import hashlib
import json
from pathlib import Path
import re
import stat
import struct
import unicodedata
import zipfile
import zlib

ROOT = Path(__file__).resolve().parent.parent
ROOT_FILES = ("README.md", "Start.cmd", "Offline.cmd", "build.ps1")
SOURCE_DIRS = ("src", "scripts", "docs", "licenses", "profiles", "verification")
REQUIRED_FILES = {"dist/TH12Coop.exe", "dist/TH12Coop.dll", "dist/th12coop-native.dll", "dist/injector.exe",
                  "dist/th12-online.cfg", "dist/coreclr.dll", "dist/TH12Coop.runtimeconfig.json",
                  "dist/TH12Coop.deps.json", "profiles/th12-online.cfg"}
EXCLUDED = {"bin", "obj", "__pycache__"}
MAX_ENTRIES, MAX_FILE, MAX_TOTAL, MAX_ARCHIVE = 20000, 64 << 20, 1 << 30, 512 << 20
CONFIG_HASH = "9d3016fdf6f7c1395a399de7de0081b0fa766787b4e0c472a2564ca713833a8f"
SECRET = re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{50,}|T12L3\.[A-Za-z0-9_-]{86}")
FORBIDDEN_DIRS = EXCLUDED | {".git", ".vs", ".cache", "node_modules", "sessions", "replay", "replays", "save", "saves", "research", "artifacts"}
FORBIDDEN_EXTS = {".dat", ".rpy", ".rep", ".sav", ".save", ".bgm", ".anm", ".ecl", ".sht", ".msg", ".std", ".pdb", ".pyc", ".log", ".dmp", ".pfx", ".p12", ".key", ".pem", ".wav", ".ogg", ".mp3", ".flac", ".m4a", ".mp4", ".webm", ".mov", ".avi", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".dds"}


class InvalidPackage(Exception):
    pass


def require(condition, message):
    if not condition:
        raise InvalidPackage(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def file_sha(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def safe_path(name):
    require(isinstance(name, str) and 0 < len(name) <= 240, "ZIP path length is invalid")
    require(unicodedata.normalize("NFC", name) == name, "Noncanonical Unicode ZIP path")
    require(not any(ord(c) < 32 or ord(c) == 127 or c in '\\<>:"|?*' for c in name), "Unsafe character in ZIP path")
    parts = (name[:-1] if name.endswith("/") else name).split("/")
    require(len(parts) <= 16 and all(p not in ("", ".", "..") and len(p) <= 128 for p in parts), "Absolute, traversing or overlong ZIP path")
    for part in parts:
        require(part[-1] not in ". ", "Windows-normalized ZIP path alias")
        device = part.partition(".")[0].rstrip(" ")
        require(not re.fullmatch(r"CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³]", device, re.I), "Windows device name in ZIP path")
    return "/".join(parts)


def regular(path, directory=False):
    value = path.lstat()
    require(not stat.S_ISLNK(value.st_mode) and not getattr(value, "st_file_attributes", 0) & 0x400,
            f"Linked/reparse source is forbidden: {path.relative_to(ROOT)}")
    require(stat.S_ISDIR(value.st_mode) if directory else stat.S_ISREG(value.st_mode),
            f"Unexpected source file type: {path.relative_to(ROOT)}")
    return value


def visible(path):
    # Package.ps1 uses Get-ChildItem without -Force (Windows hidden/system).
    return not getattr(path.lstat(), "st_file_attributes", 0) & 6


def expected_manifest():
    files, directories = {}, {"dist"}

    def add(path):
        regular(path)
        relative = path.relative_to(ROOT).as_posix()
        safe_path(relative)
        require(relative not in files, f"Duplicate expected input: {relative}")
        files[relative] = path
        parent = Path(relative).parent
        while str(parent) != ".":
            directories.add(parent.as_posix())
            parent = parent.parent

    def walk(path, source=False):
        regular(path, True)
        for child in sorted(path.iterdir()):
            if not visible(child):
                continue
            if source and child.name.casefold() in EXCLUDED and child.is_dir():
                continue
            regular(child, child.is_dir())
            if child.is_dir():
                if not source:
                    directories.add(child.relative_to(ROOT).as_posix())
                walk(child, source)
            else:
                add(child)

    for name in ROOT_FILES:
        add(ROOT / name)
    dist = ROOT / "dist"
    regular(dist, True)
    for child in sorted(dist.iterdir()):
        if not visible(child):
            continue
        if child.is_dir():
            directories.add(child.relative_to(ROOT).as_posix())
            walk(child)
        else:
            name = child.name.casefold()
            if any(fnmatch.fnmatchcase(name, pattern) for pattern in ("*-test-*", "*-results.*", "*-smoke*", "*.pdb")):
                continue
            add(child)
    for name in SOURCE_DIRS:
        source = ROOT / name
        if source.exists():
            walk(source, True)
    require(len(files) <= MAX_ENTRIES, "Too many expected files")
    require(len({name.casefold() for name in files}) == len(files), "Case-colliding source paths")
    require(REQUIRED_FILES <= files.keys(), f"Required distribution inputs are missing: {sorted(REQUIRED_FILES - files.keys())}")
    return files, directories


def allowed_file(name):
    parts = name.split("/")
    lower = [p.casefold() for p in parts]
    suffix = Path(lower[-1]).suffix
    require(not any(p in FORBIDDEN_DIRS or p == "game" or p.startswith("game-") for p in lower[:-1]), f"Forbidden directory: {name}")
    require(suffix not in FORBIDDEN_EXTS, f"Forbidden asset/media/debug/key extension: {name}")
    require(not (lower[-1].startswith(".env") or lower[-1] in {"id_rsa", "id_ed25519", "th12.exe", "th12.cfg", "steam_appid.txt"}), f"Game/save/credential file: {name}")
    require(not (suffix in {".dll", ".exe"} and "smoke" in lower[-1]), f"Smoke binary: {name}")
    if len(parts) == 1:
        require(name in ROOT_FILES, f"Unexpected root file: {name}")
    elif lower[0] == "dist":
        if lower[1] == "licenses":
            require(suffix == ".txt", f"Unexpected runtime license format: {name}")
        elif suffix == ".exe":
            require(len(parts) == 2 and lower[-1] in {"th12coop.exe", "injector.exe", "createdump.exe"}, f"Unexpected executable: {name}")
        elif suffix == ".dll":
            pass
        else:
            require(name in {"dist/TH12Coop.runtimeconfig.json", "dist/TH12Coop.deps.json", "dist/th12-online.cfg"}, f"Unexpected runtime content: {name}")
    else:
        extensions = {"src": {".cs", ".csproj", ".cpp", ".h"}, "scripts": {".py", ".ps1"}, "docs": {".md"}, "licenses": {".txt"}, "verification": {".md", ".json", ".txt"}}
        require(name == "profiles/th12-online.cfg" or suffix in extensions.get(lower[0], set()), f"Outside source allowlist: {name}")


def pe(data, name):
    require(len(data) >= 64 and data[:2] == b"MZ", f"Missing PE header: {name}")
    offset = struct.unpack_from("<I", data, 60)[0]
    require(64 <= offset <= len(data) - 24 and data[offset:offset + 4] == b"PE\0\0", f"Invalid PE offset/signature: {name}")
    machine, count = struct.unpack_from("<HH", data, offset + 4)
    size, flags = struct.unpack_from("<HH", data, offset + 20)
    require(1 <= count <= 96 and size >= 96 and offset + 24 + size + count * 40 <= len(data), f"Truncated PE headers: {name}")
    magic = struct.unpack_from("<H", data, offset + 24)[0]
    require(magic in (0x10B, 0x20B), f"Unexpected PE optional header: {name}")
    directory = 96 if magic == 0x10B else 112
    require(size >= directory, f"Truncated PE optional header: {name}")
    clr = struct.unpack_from("<II", data, offset + 24 + directory + 14 * 8) if size >= directory + 15 * 8 else (0, 0)
    return {"machine": f"0x{machine:04x}", "optional_header": f"0x{magic:03x}", "dll": bool(flags & 0x2000), "managed": clr != (0, 0)}


def json_document(data):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, "Duplicate property in runtime JSON")
            result[key] = value
        return result
    return json.loads(data.decode("utf-8-sig"), object_pairs_hook=unique)


def verify(archive, report):
    expected, directories = expected_manifest()
    game_source = (ROOT / "src/launcher/GameBuild.cs").read_text(encoding="utf-8-sig")
    game_hashes = set(re.findall(r'"([a-f0-9]{64})"', game_source))
    require(len(game_hashes) >= 6, "Original-game fingerprint denylist is incomplete")
    require(0 < archive.stat().st_size <= MAX_ARCHIVE, "Archive size outside bounds")
    report["archive"] = {"path": str(archive), "bytes": archive.stat().st_size, "sha256": file_sha(archive)}
    report["expected_file_count"] = len(expected)
    with zipfile.ZipFile(archive) as package:
        entries = package.infolist()
        require(0 < len(entries) <= MAX_ENTRIES, "ZIP entry count outside bounds")
        names, members, prefix, total, directory_count = set(), {}, None, 0, 0
        for entry in entries:
            normalized = safe_path(entry.filename)
            require(entry.orig_filename == entry.filename, "NUL-truncated ZIP name")
            key = normalized.casefold()
            require(key not in names, f"Duplicate/case-colliding ZIP path: {normalized}")
            names.add(key)
            top, _, relative = normalized.partition("/")
            require(re.fullmatch(r"[A-Za-z0-9_-]+", top) is not None, "Invalid package root name")
            prefix = prefix or top
            require(top == prefix, "ZIP contains multiple package roots")
            mode = stat.S_IFMT(entry.external_attr >> 16)
            require(mode in (0, stat.S_IFREG, stat.S_IFDIR) and not entry.external_attr & 0x400, f"Symlink/special/reparse ZIP entry: {normalized}")
            require(not entry.flag_bits & 1 and entry.compress_type in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED), f"Encrypted/unsupported ZIP member: {normalized}")
            if entry.is_dir():
                require(mode != stat.S_IFREG and entry.file_size == 0 and (not relative or relative in directories), f"Unexpected ZIP directory: {normalized}")
                directory_count += 1
                continue
            require(mode != stat.S_IFDIR and not entry.external_attr & 0x10, f"Ambiguous file/directory ZIP entry: {normalized}")
            require(relative and 0 <= entry.file_size <= MAX_FILE, f"Invalid ZIP member size: {normalized}")
            total += entry.file_size
            require(total <= MAX_TOTAL, "ZIP expanded content exceeds bound")
            allowed_file(relative)
            members[relative] = entry
        missing, extra = sorted(set(expected) - set(members)), sorted(set(members) - set(expected))
        require(not missing and not extra, f"Manifest mismatch: missing({len(missing)})={missing[:20]}, extra({len(extra)})={extra[:20]}")
        require(package.testzip() is None, "ZIP CRC validation failed")
        report.update(package_root=prefix, file_count=len(members), directory_entries=directory_count, expanded_bytes=total, zip_crc_checked=True)
        special, rows, artifacts = {}, [], {}
        for name in sorted(members):
            path = expected[name]
            regular(path)
            require(path.stat().st_size <= MAX_FILE, f"Source file exceeds bound: {name}")
            data = package.read(members[name])
            require(len(data) == members[name].file_size and data == path.read_bytes(), f"Packaged bytes differ from current source: {name}")
            digest = sha(data)
            require(digest not in game_hashes, f"Original game content under packaged name: {name}")
            binary = Path(name).suffix.casefold() in {".dll", ".exe"}
            if binary:
                info = pe(data, name)
                # A published .NET WinExe is a managed executable assembly named
                # .dll; the native apphost .exe loads it through CoreCLR.
                if name == "dist/TH12Coop.dll":
                    require(info["machine"] == "0x8664" and info["optional_header"] == "0x20b" and info["managed"] and not info["dll"], "Wrong managed launcher assembly type")
                    artifacts[name] = {"sha256": digest, "bytes": len(data), **info}
                else:
                    require(info["dll"] == name.casefold().endswith(".dll"), f"PE file type/name mismatch: {name}")
                if name in {"dist/th12coop-native.dll", "dist/injector.exe", "dist/TH12Coop.exe", "dist/coreclr.dll"}:
                    x86 = name in {"dist/th12coop-native.dll", "dist/injector.exe"}
                    require(info["machine"] == ("0x014c" if x86 else "0x8664") and info["optional_header"] == ("0x10b" if x86 else "0x20b") and not info["managed"], f"Wrong native architecture/type: {name}")
                    artifacts[name] = {"sha256": digest, "bytes": len(data), **info}
                if name == "dist/th12coop-native.dll":
                    require(b"TH12CoopAudit_" not in data, "Production DLL exposes smoke audit mapping")
            elif name.endswith(".cfg"):
                require(digest == CONFIG_HASH and len(data) == 60, f"Unexpected configuration: {name}")
            else:
                require(b"\0" not in data, f"Non-text source/evidence content: {name}")
                data.decode("utf-8-sig")
                require(SECRET.search(data) is None, f"Possible credential/room invite in {name} (value withheld)")
            if name in {"dist/TH12Coop.runtimeconfig.json", "dist/TH12Coop.deps.json"}:
                special[name] = json_document(data)
            rows.append({"path": name, "bytes": len(data), "sha256": digest})
        require(len(artifacts) == 5, "Required launcher/runtime/native artifacts are missing")
        options = special["dist/TH12Coop.runtimeconfig.json"]["runtimeOptions"]
        frameworks = options.get("includedFrameworks", [])
        required = {"Microsoft.NETCore.App": "10.0.14", "Microsoft.WindowsDesktop.App": "10.0.14"}
        require(options.get("tfm") == "net10.0" and "framework" not in options and "frameworks" not in options and len(frameworks) == 2 and {f["name"]: f["version"] for f in frameworks} == required, "Runtime configuration is not self-contained .NET10.0.14")
        deps = special["dist/TH12Coop.deps.json"]
        require(deps["runtimeTarget"]["name"] == ".NETCoreApp,Version=v10.0/win-x64", "Runtime dependency target is not win-x64/net10.0")
        for framework in required:
            require(f"runtimepack.{framework}.Runtime.win-x64/10.0.14" in deps["libraries"], "Runtime dependency version mismatch")
        report.update(artifacts=artifacts, runtime={"tfm": options["tfm"], "included_frameworks": required}, files=rows,
                      manifest_sha256=sha(json.dumps(rows, sort_keys=True, separators=(",", ":")).encode("utf-8")))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    archive, output = args.archive.resolve(), args.out.resolve()
    if output == archive or output.suffix.casefold() != ".json" or any(output == ROOT / name for name in ROOT_FILES) or any(output.is_relative_to(ROOT / name) for name in ("dist", *SOURCE_DIRS)):
        parser.error("--out must be a JSON report outside packaged inputs, for example build/package-check.json")
    report = {"schema_version": 1, "checked_at_utc": datetime.now(timezone.utc).isoformat(), "workspace": str(ROOT), "scope": "Package source integrity, structure and runtime architecture only; embedded verification results are not interpreted as pass/fail", "secret_scan_scope": "Filename restrictions and representative private-key/GitHub-token/room-invite patterns in text files; not an exhaustive secret audit", "passed": False}
    try:
        verify(archive, report)
        report["passed"] = True
    except (InvalidPackage, OSError, ValueError, KeyError, TypeError, zipfile.BadZipFile, zlib.error, RuntimeError) as error:
        report["error"] = str(error)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"passed": report["passed"], "report": str(output), "files": report.get("file_count"), "error": report.get("error")}, ensure_ascii=False))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
