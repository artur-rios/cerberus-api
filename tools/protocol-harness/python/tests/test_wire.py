import importlib.util
import unittest
from cerberus_protocol.errors import ProtocolError


class WireTest(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(importlib.util.find_spec("cerberus_protocol.wire"), "wire object encoder missing")
        from cerberus_protocol import wire
        self.api = wire

    def test_GivenNestedProtocolObject_WhenEncoded_ThenStableStrictUtf8Bytes(self):
        self.assertEqual(b'{"a":[1,true,null,"\xc3\xa9","\\u001b"],"z":{"x":9007199254740991}}',
                         self.api.encode({"z": {"x": 9007199254740991}, "a": [1, True, None, "é", "\x1b"]}))

    def test_GivenInvalidProtocolObject_WhenEncoded_ThenRedactedRejection(self):
        for value in (None, [], {"x": 1.0}, {"x": float("nan")}, {"x": 9007199254740992}, {"x": "\ud800"},
                      {"x": object()}, {1: "value"}, {"é": 1}):
            with self.subTest(value=type(value)):
                with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
                    self.api.encode(value)

    def test_GivenAggregateLimit_WhenEncoded_ThenRejectBeforeOversizedResult(self):
        self.assertEqual(b'{"x":"a"}', self.api.encode({"x": "a"}, max_bytes=9))
        with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
            self.api.encode({"x": "a"}, max_bytes=8)
        with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
            self.api.encode({"x": "x" * 1048576})

    def test_GivenCyclicObject_WhenEncoded_ThenStableRejection(self):
        cyclic = {}; cyclic["x"] = cyclic
        with self.assertRaisesRegex(ProtocolError, "^invalid_protocol$"):
            self.api.encode(cyclic)
