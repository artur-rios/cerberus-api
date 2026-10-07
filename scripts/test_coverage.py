import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


class CoverageTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("coverage_helper", Path(__file__).with_name("coverage.py"))
        self.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.module)

    def test_given_invalid_or_empty_summary_when_reading_then_fail_closed(self):
        with tempfile.TemporaryDirectory(prefix="cerberus-coverage-test-") as temporary:
            path = Path(temporary) / "Summary.json"
            for value, lines in [(float("nan"), 100), (float("inf"), 100), (-1, 100), (101, 100), (True, 100), (100, 0)]:
                with self.subTest(value=value, lines=lines):
                    path.write_text(json.dumps({"summary": {"linecoverage": value, "coverablelines": lines}}), encoding="utf-8")
                    self.assertIsNone(self.module.read_line_coverage(path))

    def test_given_valid_merged_summary_when_checking_then_enforce_floor(self):
        with tempfile.TemporaryDirectory(prefix="cerberus-coverage-test-") as temporary:
            root = Path(temporary)
            with patch.object(self.module, "REPORT_DIR", root):
                for percentage, expected in [(89.9, 1), (90.0, 0), (94.7, 0)]:
                    (root / "Summary.json").write_text(json.dumps({"summary": {"linecoverage": percentage, "coverablelines": 100}}), encoding="utf-8")
                    self.assertEqual(expected, self.module.check_threshold(90))

    def test_given_old_results_when_starting_fresh_collection_then_remove_only_test_artifacts(self):
        with tempfile.TemporaryDirectory(prefix="cerberus-coverage-test-") as temporary:
            root = Path(temporary)
            stale = root / "Fixture.Tests/TestResults/old/coverage.cobertura.xml"
            stale.parent.mkdir(parents=True)
            stale.write_text("stale fixture", encoding="utf-8")
            source = root / "Fixture.Tests/Fixture.cs"
            source.write_text("preserved source fixture", encoding="utf-8")
            with patch.object(self.module, "TESTS_DIR", root):
                self.module.clear_previous_results()
                self.assertFalse(stale.exists())
                self.assertTrue(source.exists())

    def test_given_coverage_inputs_when_generating_then_exclude_test_and_foreign_assemblies(self):
        with tempfile.TemporaryDirectory(prefix="cerberus-coverage-test-") as temporary:
            root = Path(temporary)
            report = root / "Fixture.Tests/TestResults/current/coverage.cobertura.xml"
            report.parent.mkdir(parents=True)
            report.write_text("collector fixture", encoding="utf-8")
            commands = []

            def generator(command, **kwargs):
                commands.append(command)
                return type("Exit", (), {"returncode": 0})()

            with patch.object(self.module, "TESTS_DIR", root), patch.object(self.module, "REPORT_DIR", root / "report"), patch.object(self.module, "run", generator):
                self.assertEqual(0, self.module.generate_report())
            self.assertIn("-assemblyfilters:+ArturRios.Cerberus.*;-*.Tests", commands[0])
