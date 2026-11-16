import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from resolve_upstream_baseline import resolve_baseline


class ResolveBaselineTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def git(self, repository, *args):
        environment = dict(os.environ, GIT_AUTHOR_NAME="Baseline test",
                           GIT_AUTHOR_EMAIL="baseline@example.invalid",
                           GIT_COMMITTER_NAME="Baseline test",
                           GIT_COMMITTER_EMAIL="baseline@example.invalid")
        return subprocess.check_output(["git", "-C", str(repository), *args],
                                       env=environment).decode().strip()

    def repository(self, name, version):
        path = self.root / name
        path.mkdir()
        self.git(path, "init", "--quiet", "--initial-branch=master")
        (path / ".gitattributes").write_text("*.props -text\n")
        (path / "Directory.Packages.props").write_bytes(
            f'<Project><ItemGroup><PackageVersion Include="Example" Version="{version}" /></ItemGroup></Project>\r\n'.encode())
        self.git(path, "add", ".")
        self.git(path, "commit", "--quiet", "-m", "initial")
        return path

    def test_uses_common_ancestor_not_either_changed_tip(self):
        upstream = self.repository("upstream", "1.0")
        expected_sha = self.git(upstream, "rev-parse", "HEAD")
        expected_bytes = (upstream / "Directory.Packages.props").read_bytes()
        fork = self.root / "fork"
        self.git(self.root, "clone", "--quiet", str(upstream), str(fork))
        for repository, version in [(fork, "2.0"), (upstream, "3.0")]:
            file = repository / "Directory.Packages.props"
            file.write_bytes(file.read_bytes().replace(b"1.0", version.encode()))
            self.git(repository, "commit", "--quiet", "-am", "change dependency")
        destination = self.root / "output" / "baseline.props"
        actual = resolve_baseline(str(fork), self.git(fork, "rev-parse", "HEAD"),
                                  str(upstream), destination)
        self.assertEqual(actual, expected_sha)
        self.assertEqual(destination.read_bytes(), expected_bytes)

    def test_rejects_unrelated_histories_without_writing_a_baseline(self):
        upstream = self.repository("upstream", "1.0")
        fork = self.repository("fork", "2.0")
        destination = self.root / "baseline.props"
        with self.assertRaises(subprocess.CalledProcessError):
            resolve_baseline(str(fork), self.git(fork, "rev-parse", "HEAD"),
                             str(upstream), destination)
        self.assertFalse(destination.exists())

    def test_rejects_a_non_commit_input(self):
        with self.assertRaises(ValueError):
            resolve_baseline("unused", "master", "unused", self.root / "baseline.props")


if __name__ == "__main__":
    unittest.main()
