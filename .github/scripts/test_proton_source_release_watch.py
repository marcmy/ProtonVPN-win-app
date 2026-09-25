import ast
from pathlib import Path
import textwrap
import unittest

from proton_source_release_watch import (
    find_latest_shared_release_tag,
    select_source_patch_branch,
)


class SelectSourcePatchBranchTests(unittest.TestCase):
    def test_candidate_ancestry_uses_live_temporary_repository(self):
        workflow = (
            Path(__file__).resolve().parents[1]
            / "workflows/proton-source-release-watch.yml"
        ).read_text(encoding="utf-8")
        embedded_python = workflow.split("python3 <<'PY'\n", 1)[1].split("\n          PY", 1)[0]
        tree = ast.parse(textwrap.dedent(embedded_python))

        temporary_scope = next(
            node
            for node in ast.walk(tree)
            if isinstance(node, ast.With)
            and any(
                isinstance(item.context_expr, ast.Call)
                and isinstance(item.context_expr.func, ast.Attribute)
                and item.context_expr.func.attr == "TemporaryDirectory"
                for item in node.items
            )
        )
        candidate_inspection = next(
            node
            for node in ast.walk(tree)
            if isinstance(node, ast.Call)
            and isinstance(node.func, ast.Name)
            and node.func.id == "find_latest_shared_release_tag"
        )
        self.assertLess(temporary_scope.lineno, candidate_inspection.lineno)
        self.assertLessEqual(candidate_inspection.end_lineno, temporary_scope.end_lineno)

    def test_finds_prior_official_tag_shared_by_early_candidate_and_new_source(self):
        candidate_ancestors = {"refs/tags/v5.1.8"}
        target_ancestors = {"refs/tags/v5.1.8", "refs/tags/v5.1.9"}

        def is_ancestor(tag_ref, branch_ref):
            ancestors = candidate_ancestors if branch_ref == "candidate" else target_ancestors
            return tag_ref in ancestors

        self.assertEqual(
            find_latest_shared_release_tag(
                "5.1.9",
                "5.1.9",
                ["5.1.7", "5.1.8", "5.1.9"],
                candidate_ref="candidate",
                target_ref="target",
                is_ancestor=is_ancestor,
            ),
            "5.1.8",
        )

    def test_returns_no_shared_tag_for_unrelated_candidate_history(self):
        self.assertIsNone(
            find_latest_shared_release_tag(
                "5.1.9",
                "5.1.9",
                ["5.1.8", "5.1.9"],
                candidate_ref="candidate",
                target_ref="target",
                is_ancestor=lambda _tag_ref, _branch_ref: False,
            )
        )

    def test_uses_maintained_fork_when_no_reviewed_candidate_exists(self):
        self.assertEqual(
            select_source_patch_branch(
                "5.1.9",
                candidate_versions=[],
                compatible_candidate_versions=[],
            ),
            "marc/proton",
        )

    def test_uses_reviewed_candidate_when_current_release_tag_is_its_base(self):
        self.assertEqual(
            select_source_patch_branch(
                "5.1.8",
                candidate_versions=["5.1.8"],
                compatible_candidate_versions=["5.1.8"],
            ),
            "future/proton/v5.1.8",
        )

    def test_uses_early_candidate_based_on_previous_official_tag(self):
        self.assertEqual(
            select_source_patch_branch(
                "5.1.9",
                candidate_versions=["5.1.8", "5.1.9"],
                compatible_candidate_versions=["5.1.9"],
            ),
            "future/proton/v5.1.9",
        )

    def test_uses_newest_compatible_prior_candidate(self):
        self.assertEqual(
            select_source_patch_branch(
                "5.1.9",
                candidate_versions=["5.1.7", "5.1.8"],
                compatible_candidate_versions=["5.1.7", "5.1.8"],
            ),
            "future/proton/v5.1.8",
        )

    def test_fails_closed_when_newest_candidate_has_no_common_release_ancestor(self):
        with self.assertRaisesRegex(ValueError, "refusing to fall back to marc/proton"):
            select_source_patch_branch(
                "5.1.9",
                candidate_versions=["5.1.8", "5.1.9"],
                compatible_candidate_versions=["5.1.8"],
            )

    def test_ignores_candidates_newer_than_target_release(self):
        self.assertEqual(
            select_source_patch_branch(
                "5.1.8",
                candidate_versions=["5.1.9"],
                compatible_candidate_versions=[],
            ),
            "marc/proton",
        )

    def test_rejects_non_semver_target_version(self):
        with self.assertRaisesRegex(ValueError, "Invalid Proton source version"):
            select_source_patch_branch(
                "v5.1.8",
                candidate_versions=[],
                compatible_candidate_versions=[],
            )


if __name__ == "__main__":
    unittest.main()
