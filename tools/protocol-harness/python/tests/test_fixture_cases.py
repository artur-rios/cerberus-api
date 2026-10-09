import unittest
from cerberus_protocol import fixture_cases as fixtures
from cerberus_protocol.symmetric import seal, open_envelope, NonceGuard


class FixtureCasesTests(unittest.TestCase):
    def test_GivenValidNegativeEnvelopeWithDifferentPlaintext_WhenExecuted_ThenFixtureFailureIsNotProtocolRejection(self):
        expected=fixtures.slot('account','account')
        output=seal(fixtures.ROOT,expected,'cerberus-content-v1',b'different public plaintext',NonceGuard(0))
        self.assertEqual(b'different public plaintext',open_envelope(fixtures.ROOT,expected,'cerberus-content-v1',output))
        with self.assertRaises(AssertionError):
            fixtures._execute(dict(id='content.account.mutate.tag',output=output))
