#!/usr/bin/env python3
"""Synthetic regression tests for the restored-NuGet dependency snapshot builder."""
import json
from pathlib import Path
import runpy
import tempfile
import unittest

build_snapshot = runpy.run_path(str(Path(__file__).with_name("submit-nuget-snapshot.py")))["build_snapshot"]


def write_project(root, i):
    directory = root / "src" / f"Project{i}"
    directory.mkdir(parents=True)
    (directory / f"Project{i}.csproj").write_text("<Project />\n")
    packages = {
        "log4net": {"type": "Direct", "resolved": "3.2.0", "dependencies": {"Example1": "1.0.0"}}
    }
    for n in range(1, 35):
        packages[f"Example{n}"] = {"type": "Transitive", "resolved": "1.0.0"}
    (directory / "packages.lock.json").write_text(json.dumps({
        "version": 1,
        "dependencies": {"net10.0-windows": packages},
    }))


class NuGetSubmissionTests(unittest.TestCase):
    def test_nonempty_resolved_graph_contains_direct_transitive_and_edges(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for i in range(3):
                write_project(root, i)
            snap, lock_count, unique_count = build_snapshot(root)
            self.assertEqual(lock_count, 3)
            self.assertEqual(unique_count, 35)
            resolved = snap["manifests"]["src/Project0/Project0.csproj"]["resolved"]
            self.assertEqual(resolved["pkg:nuget/log4net@3.2.0"]["relationship"], "direct")
            self.assertIn("pkg:nuget/Example1@1.0.0",
                          resolved["pkg:nuget/log4net@3.2.0"]["dependencies"])
            self.assertEqual(resolved["pkg:nuget/Example1@1.0.0"]["relationship"], "indirect")

    def test_fail_closed_on_empty_inventory(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(RuntimeError):
                build_snapshot(Path(tmp))

    def test_fail_closed_on_missing_log4net(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for i in range(3):
                write_project(root, i)
                lock = root / "src" / f"Project{i}" / "packages.lock.json"
                doc = json.loads(lock.read_text())
                del doc["dependencies"]["net10.0-windows"]["log4net"]
                lock.write_text(json.dumps(doc))
            with self.assertRaisesRegex(RuntimeError, "Incomplete inventory"):
                build_snapshot(root)


if __name__ == "__main__":
    unittest.main()
