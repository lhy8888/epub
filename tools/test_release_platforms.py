"""Exercise release rejection and draft publication without network mutations."""
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import release_platforms as release


class ReleaseChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.tag = "windows-v" + release.project_version()
        self.assets = []
        for suffix in release.PLATFORMS["windows"][2]:
            path = self.root / ("QuietRead-" + release.project_version() + "-" + suffix)
            path.write_bytes(b"verified build output")
            checksum = path.with_name(path.name + ".sha256")
            checksum.write_text(hashlib.sha256(path.read_bytes()).hexdigest() + "  " + path.name + "\n")
            self.assets.extend([path, checksum])

    def test_all_platforms_have_independent_tags(self):
        plan = release.release_plan("release-v" + release.project_version())["include"]
        self.assertEqual(len(plan), 5)
        self.assertEqual(len({p["tag"] for p in plan}), 5)
        for item in plan:
            self.assertEqual(release.release_plan(item["tag"])["include"], [item])

    def test_invalid_and_wrong_source_versions_rejected(self):
        for tag in ("windows-v0.0.0", "windows-v01.0.0", "v1.0.0", "windows-v1.0.0;echo unsafe"):
            with self.assertRaises(ValueError):
                release.release_plan(tag)

    def test_changed_package_rejected(self):
        self.assets[0].write_bytes(b"modified output")
        with self.assertRaisesRegex(ValueError, "Checksum mismatch"):
            release.assets_for("windows", self.tag, self.root)

    def test_missing_checksum_rejected(self):
        self.assets[1].unlink()
        with self.assertRaises(FileNotFoundError):
            release.assets_for("windows", self.tag, self.root)

    def test_other_platform_payload_rejected(self):
        (self.root / "QuietRead-1.0.0-linux-x64.tar.gz").write_bytes(b"wrong platform")
        with self.assertRaisesRegex(ValueError, "unexpected"):
            release.assets_for("windows", self.tag, self.root)

    def test_traversal_checksum_rejected(self):
        self.assets[1].write_text(hashlib.sha256(self.assets[0].read_bytes()).hexdigest() + "  ../other.zip\n")
        with self.assertRaisesRegex(ValueError, "Checksum mismatch"):
            release.assets_for("windows", self.tag, self.root)

    def exercise_publication(self, corrupt=False, wrong_ref=False, published=False):
        calls = []
        draft = not published
        present = published
        sha = "a" * 40

        def fake_gh(*args, optional=False):
            nonlocal draft, present
            calls.append(args)
            if args[:2] == ("release", "view"):
                if not present:
                    return None
                return json.dumps({"tagName": self.tag, "isDraft": draft, "isPrerelease": False,
                                   "assets": [{"name": p.name} for p in self.assets], "url": "https://github.com/example/repo/releases/tag/" + self.tag})
            if args[0] == "api":
                return json.dumps({"object": {"type": "commit", "sha": "b" * 40 if wrong_ref else sha}})
            if args[:2] == ("release", "create"):
                present = True
            if args[:2] == ("release", "download"):
                destination = Path(args[args.index("--dir") + 1])
                for path in self.assets:
                    (destination / path.name).write_bytes(path.read_bytes())
                if corrupt:
                    (destination / self.assets[0].name).write_bytes(b"corrupt uploaded package")
            if args[:2] == ("release", "edit"):
                draft = False
            return ""

        previous = Path.cwd()
        os.chdir(self.root)
        try:
            with patch.dict(os.environ, {"GITHUB_REPOSITORY": "example/repo", "GITHUB_SHA": sha}), patch.object(release, "gh", fake_gh):
                try:
                    release.publish("windows", self.tag, self.root)
                    error = None
                except ValueError as e:
                    error = e
            return calls, error
        finally:
            os.chdir(previous)

    def test_draft_published_only_after_download_verification(self):
        calls, error = self.exercise_publication()
        self.assertIsNone(error)
        download = next(i for i, args in enumerate(calls) if args[:2] == ("release", "download"))
        publish = next(i for i, args in enumerate(calls) if args[:2] == ("release", "edit"))
        self.assertLess(download, publish)
        self.assertTrue((self.root / "release-verification.json").is_file())

    def test_bad_upload_stays_draft(self):
        calls, error = self.exercise_publication(corrupt=True)
        self.assertIsNotNone(error)
        self.assertFalse(any(args[:2] == ("release", "edit") for args in calls))

    def test_wrong_source_tag_blocks_mutation(self):
        calls, error = self.exercise_publication(wrong_ref=True)
        self.assertIsNotNone(error)
        self.assertFalse(any(args[:2] in (("release", "create"), ("release", "upload"), ("release", "edit")) for args in calls))

    def test_public_asset_is_never_replaced(self):
        calls, error = self.exercise_publication(corrupt=True, published=True)
        self.assertIsNotNone(error)
        self.assertFalse(any(args[:2] in (("release", "create"), ("release", "upload"), ("release", "edit")) for args in calls))


if __name__ == "__main__":
    unittest.main()
