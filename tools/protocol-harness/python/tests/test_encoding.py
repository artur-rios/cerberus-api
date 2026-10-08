"""Scenarios: canonical bytes, strict JSON, Unicode, GUID, integers, base64url and whole-request bounds.

Removing any corresponding boundary validation must fail its rejection test.
Expected encoding bytes are hand-written, not computed by another encoder.
"""
import importlib
import importlib.util
import unittest


class EncodingTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(importlib.util.find_spec("cerberus_protocol.encoding"), "encoding feature missing")
        self.e = importlib.import_module("cerberus_protocol.encoding")
        self.Error = importlib.import_module("cerberus_protocol.errors").ProtocolError

    def test_given_canonical_array_when_encoded_then_bytes_match(self):
        self.assertEqual(self.e.context(["cerberus-v1", 1, [], True, None]), b'["cerberus-v1",1,[],true,null]')

    def test_given_ascii_escapes_when_encoded_then_minimal_json(self):
        self.assertEqual(self.e.context(['/', '"', '\\', '\n']), b'["/","\\\"","\\\\","\\n"]')

    def test_given_control_escape_when_encoded_then_lowercase_canonical_bytes(self):
        self.assertEqual(self.e.context(['\x1b']), b'["\\u001b"]')

    def test_given_noncanonical_array_values_when_encoded_then_reject(self):
        for value in ("é", "\ud800", {}, 1.0, 9007199254740992):
            with self.subTest(value_type=type(value).__name__), self.assertRaises(self.Error):
                self.e.context([value])

    def test_given_integer_boundaries_when_decoded_then_accept(self):
        self.assertEqual(self.e.integer(1, 1, 9007199254740991), 1)
        self.assertEqual(self.e.integer(9007199254740991, 1, 9007199254740991), 9007199254740991)
        self.assertEqual(self.e.integer(0, 0, 9007199254740991), 0)

    def test_given_invalid_integer_when_decoded_then_reject(self):
        for value in (True, False, 0, -1, 9007199254740992, "1", 1.0, None):
            with self.subTest(value_type=type(value).__name__), self.assertRaises(self.Error):
                self.e.integer(value, 1, 9007199254740991)

    def test_given_reordered_properties_when_parsed_then_same_meaning(self):
        self.assertEqual(self.e.parse(b'{"y":2,"x":1}', {"x", "y"}, set(), 1024), {"x": 1, "y": 2})

    def test_given_invalid_json_when_parsed_then_reject(self):
        cases = (b'{"x":1,"x":2}', b'{"x":{"y":1,"y":2}}', b'{"x":1e0}', b'{"x":1.0}',
                 b'{"x":9007199254740992}', b'{"x":NaN}', b'{"x":Infinity}', b'{"x":1}{}',
                 b'[]', b'{}', b'{"x":1,"unknown":2}', b'{"x":1} trailing', b'{"x":}')
        for case in cases:
            with self.subTest(raw_length=len(case)), self.assertRaises(self.Error):
                self.e.parse(case, {"x"}, set(), 1048576)

    def test_given_bad_unicode_when_parsed_then_reject(self):
        for raw in (b'\xef\xbb\xbf{"x":1}', b'{"x":"\xff"}', b'{"x":"\\ud800"}', b'{"\\udfff":1}'):
            with self.subTest(raw_length=len(raw)), self.assertRaises(self.Error):
                self.e.parse(raw, {"x"}, set(), 1024)

    def test_given_valid_unicode_pair_when_parsed_then_accept(self):
        self.assertEqual(self.e.parse(b'{"x":"\\ud83d\\ude00"}', {"x"}, set(), 1024), {"x": "😀"})

    def test_given_request_boundary_when_parsed_then_bound_aggregate_bytes(self):
        self.assertEqual(self.e.parse(b'{"x":1}', {"x"}, set(), 7), {"x": 1})
        with self.assertRaises(self.Error): self.e.parse(b'{"x":1} ', {"x"}, set(), 7)
        with self.assertRaises(self.Error): self.e.parse(b'{"x":[1,2,3,4]}', {"x"}, set(), 7)

    def test_given_canonical_guid_when_validated_then_preserve(self):
        self.assertEqual(self.e.guid("00000000-0000-0000-0000-000000000001"), "00000000-0000-0000-0000-000000000001")

    def test_given_invalid_guid_when_validated_then_reject(self):
        for value in ("", None, "00000000-0000-0000-0000-000000000000", "AAAAAAAA-0000-0000-0000-000000000001",
                      "00000000000000000000000000000001", "{00000000-0000-0000-0000-000000000001}"):
            with self.subTest(value_type=type(value).__name__), self.assertRaises(self.Error): self.e.guid(value)

    def test_given_canonical_base64_when_decoded_then_bytes_match(self):
        self.assertEqual(self.e.base64(b'\xff\x00'), "_wA")
        self.assertEqual(self.e.unbase64("_wA", 2), b'\xff\x00')

    def test_given_invalid_base64_when_decoded_then_reject(self):
        for value in (None, "", "_wA=", "_wA\n", "/wA", "_wB", "A", "AA"):
            with self.subTest(value_type=type(value).__name__), self.assertRaises(self.Error): self.e.unbase64(value, 2)

    def test_given_failure_when_rejected_then_stable_redacted_code(self):
        with self.assertRaises(self.Error) as caught: self.e.guid("secret marker")
        self.assertEqual(str(caught.exception), "invalid_protocol")
        self.assertEqual(caught.exception.code, "invalid_protocol")


if __name__ == "__main__": unittest.main()
