import importlib.util
import unittest
from pathlib import Path


class OpenApiTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("openapi", Path(__file__).with_name("openapi.py"))
        self.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.module)

    def test_given_changed_route_when_comparing_then_detect_drift(self):
        old = {"openapi": "3.0.4", "paths": {"/fixture": {"get": {"responses": {"200": {"description": "OK"}}}}}}
        new = {"openapi": "3.0.4", "paths": {"/changed": {"get": {"responses": {"200": {"description": "OK"}}}}}}
        self.assertFalse(self.module.is_current(old, new))

    def test_given_only_property_order_changed_when_comparing_then_accept(self):
        self.assertTrue(self.module.is_current({"openapi": "3.0.4", "paths": {}}, {"paths": {}, "openapi": "3.0.4"}))

    def test_given_missing_contracts_when_comparing_then_fail_closed(self):
        self.assertFalse(self.module.is_current(None, None))

    def test_given_changed_schema_when_comparing_then_detect_drift(self):
        old = {"openapi": "3.0.4", "paths": {}, "components": {"schemas": {"Fixture": {"type": "string"}}}}
        new = {"openapi": "3.0.4", "paths": {}, "components": {"schemas": {"Fixture": {"type": "integer"}}}}
        self.assertFalse(self.module.is_current(old, new))
