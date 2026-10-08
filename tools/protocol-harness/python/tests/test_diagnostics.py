import contextlib
import importlib
import io
import unittest


class DiagnosticsTests(unittest.TestCase):
    def setUp(self):
        try: self.cli = importlib.import_module("cerberus_protocol.__main__")
        except ModuleNotFoundError: self.fail("CLI diagnostics feature missing")

    def test_GivenSecretBearingException_WhenHandled_ThenOnlyStableCodeEmitted(self):
        output = io.StringIO()
        with contextlib.redirect_stderr(output), contextlib.redirect_stdout(output):
            code = self.cli.safe(lambda: (_ for _ in ()).throw(RuntimeError("marker-secret plaintext private proof")))
        self.assertEqual(1, code)
        self.assertIn("invalid_protocol", output.getvalue())
        self.assertNotIn("marker-secret", output.getvalue())

    def test_GivenMissingDependency_WhenHandled_ThenUnsupportedCodeEmitted(self):
        output = io.StringIO()
        with contextlib.redirect_stderr(output):
            code = self.cli.safe(lambda: (_ for _ in ()).throw(ModuleNotFoundError("marker-secret")))
        self.assertEqual(1, code); self.assertIn("unsupported_dependency", output.getvalue()); self.assertNotIn("marker-secret", output.getvalue())
