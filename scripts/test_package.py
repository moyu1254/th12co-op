"""Check release source boundaries with a disposable Git checkout and fake binaries.

Runs the real PowerShell packager; no game or compiled executable is launched.
"""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import verify_package as verifier


ROOT = Path(__file__).resolve().parent.parent
POWERSHELL = shutil.which("pwsh") or shutil.which("powershell")


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="th12-package-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.git("init", "--quiet")
        for name in verifier.ROOT_FILES:
            shutil.copyfile(ROOT / name, self.root / name)
        for name in ("scripts/Package.ps1", "scripts/Test-Repository.ps1", "profiles/th12-online.cfg"):
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
        self.write("docs/reviewed.md", b"Reviewed public documentation\n")
        self.git("add", "--all")
        for name in verifier.REQUIRED_FILES:
            if name.startswith("dist/"):
                self.write(name, b"Synthetic packaging fixture; not executable\n")
        for name in ("th12coop-smoke.dll", "th12coop-quiet-test.dll", "launcher-test-results.txt", "TH12Coop.pdb"):
            self.write("dist/" + name, b"Local diagnostic fixture\n")
        self.archive = self.root / "release/fixture.zip"

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.root), *args], check=True, capture_output=True)

    def write(self, name, content):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    def package(self):
        return subprocess.run([POWERSHELL, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                               str(self.root / "scripts/Package.ps1"), "-Name", "fixture"],
                              cwd=self.root, capture_output=True, text=True, timeout=90)

    def test_untracked_source_and_diagnostics_do_not_enter_archive(self):
        for name in ("docs/private.md", "docs/th12.dat", "src/private.cs", "verification/local.json"):
            self.write(name, b"Untracked local fixture\n")
        result = self.package()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        with patch.object(verifier, "ROOT", self.root):
            manifest, _ = verifier.expected_manifest()
        with zipfile.ZipFile(self.archive) as archive:
            contents = {name.removeprefix("fixture/"): archive.read(name)
                        for name in archive.namelist() if not name.endswith("/")}
        self.assertEqual(set(contents), set(manifest))
        self.assertIn("LICENSE", contents)
        self.assertIn("NOTICE.md", contents)
        self.assertIn("docs/reviewed.md", contents)
        for name, path in manifest.items():
            self.assertEqual(contents[name], path.read_bytes(), name)
        self.assertFalse(any("private" in name or "smoke" in name or "-test" in name for name in contents))

    def test_tracked_original_asset_is_rejected_before_packaging(self):
        self.write("docs/th12.dat", b"Not actual game data\n")
        self.git("add", "docs/th12.dat")
        result = self.package()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("outside the source allowlist", result.stdout + result.stderr)
        self.assertFalse(self.archive.exists())

    def test_license_must_be_tracked_even_if_present_on_disk(self):
        self.git("rm", "--cached", "LICENSE")
        with patch.object(verifier, "ROOT", self.root):
            with self.assertRaisesRegex(verifier.InvalidPackage, "Required distribution inputs"):
                verifier.expected_manifest()

    def test_missing_tracked_input_is_rejected(self):
        (self.root / "docs/reviewed.md").unlink()
        result = self.package()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.archive.exists())

    def test_existing_archive_is_preserved(self):
        self.write("release/fixture.zip", b"Existing release fixture\n")
        result = self.package()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Archive already exists", result.stdout + result.stderr)
        self.assertEqual(self.archive.read_bytes(), b"Existing release fixture\n")


if __name__ == "__main__":
    if not POWERSHELL:
        raise SystemExit("PowerShell is required to test Package.ps1")
    unittest.main(verbosity=2)
