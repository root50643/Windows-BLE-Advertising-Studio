"""Offline fixtures only. Passing these tests does not verify Apple/Google network tracking."""
import asyncio
import base64
import hashlib
import json
import os
import struct
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path
import pytest
from fastapi.testclient import TestClient
from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.serialization import Encoding, PublicFormat
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

sys.path.insert(0, str(Path(__file__).parent))
from app import Settings, create_app
from providers import Providers, ProviderError, apple_public, decrypt_apple
from store import Store

PASSWORD = 'offline-test-password-12345'
HEADERS = {'X-Requested-With': 'BLEStudio'}


@pytest.fixture
def client(tmp_path):
    app = create_app(Settings(str(tmp_path), os.urandom(32), PASSWORD, 'https://testserver'))
    with TestClient(app, base_url='https://testserver', headers=HEADERS) as client:
        assert client.post('/api/login', json={'username': 'owner', 'password': PASSWORD}).status_code == 200
        yield client


def identity():
    private = ec.generate_private_key(ec.SECP224R1()).private_numbers().private_value.to_bytes(28, 'big')
    return dict(id=str(uuid.uuid4()), name='Offline test identity', protocol='AppleFindMy',
        pairDate=datetime.now(timezone.utc).isoformat(), publicKeyHex=apple_public(private).hex(), secretBase64=base64.b64encode(private).decode()), private


def register(client):
    data, private = identity()
    response = client.post('/api/trackers/' + data['id'] + '/register', json=data)
    assert response.status_code == 200
    assert 'secretBase64' not in response.json()
    return data, private


def test_unauthenticated_access_rejected(client):
    client.cookies.clear()
    assert client.get('/api/trackers').status_code == 401


def test_csrf_and_owner_only_boundaries(client):
    assert client.post('/api/tokens', json={}, headers={'X-Requested-With': ''}).status_code == 403
    token = client.post('/api/tokens', json={'name': 'test desktop'}).json()['token']
    client.cookies.clear()
    assert client.post('/api/providers/google', headers={'Authorization': 'Bearer ' + token}, json={'secret': 'test-only'}).status_code == 403
    assert client.get('/api/trackers', headers={'Authorization': 'Bearer ' + token}).status_code == 200


def test_desktop_token_revocation_is_persistent(client):
    token = client.post('/api/tokens', json={}).json()
    assert client.delete('/api/tokens/' + token['id']).status_code == 200
    client.cookies.clear()
    assert client.get('/api/trackers', headers={'Authorization': 'Bearer ' + token['token']}).status_code == 401


def test_empty_history_has_no_fallback_coordinates(client):
    data, _ = register(client)
    result = client.get('/api/trackers/' + data['id'] + '/locations').json()
    assert result['reports'] == []
    assert result['status'] == 'RegisteredNoLocationReports'
    assert 'latitude' not in json.dumps(result)


def test_unavailable_provider_is_explicit_and_keeps_empty_history(client):
    data, _ = register(client)
    assert client.post('/api/trackers/' + data['id'] + '/refresh').status_code == 200
    result = client.get('/api/trackers/' + data['id'] + '/locations').json()
    assert result['reports'] == [] and result['status'] == 'AppleAuthorizationRequired'


def test_secret_material_not_stored_or_exported_in_plaintext(client):
    data, _ = register(client)
    store = client.app.state.store
    assert data['secretBase64'].encode() not in Path(store.path).read_bytes()
    assert data['secretBase64'] not in client.get('/api/trackers').text
    assert store.secret('identity:' + data['id']) == data['secretBase64']


def test_identity_key_cannot_be_silently_replaced(client):
    data, _ = register(client)
    data['secretBase64'] = base64.b64encode(os.urandom(28)).decode()
    assert client.post('/api/trackers/' + data['id'] + '/register', json=data).status_code == 409


def test_google_requires_account_authorization(client):
    data, _ = identity(); data['protocol'] = 'GoogleFindHub'; data['secretBase64'] = base64.b64encode(os.urandom(32)).decode()
    response = client.post('/api/trackers/' + data['id'] + '/register', json=data)
    assert response.status_code == 502 and response.json()['code'] == 'GoogleAuthorizationRequired'


def test_owner_can_remove_local_provider_authorization(client):
    assert client.post('/api/providers/google', json={'unit-test': 'not-a-real-account'}).status_code == 200
    assert client.app.state.store.secret('google-account')
    assert client.delete('/api/providers/google').status_code == 200
    assert client.app.state.store.secret('google-account') is None


def test_master_key_and_purpose_binding_protect_secrets(tmp_path):
    store = Store(str(tmp_path), os.urandom(32)); store.put_secret('one', {'value': 'secret-fixture'})
    with store.db() as db:
        db.execute("INSERT INTO secrets SELECT 'two',envelope FROM secrets WHERE id='one'")
    with pytest.raises(InvalidTag):
        store.secret('two')


def encrypted_apple_fixture(private, timestamp, latitude, longitude):
    public = ec.derive_private_key(int.from_bytes(private, 'big'), ec.SECP224R1()).public_key()
    ephemeral = ec.generate_private_key(ec.SECP224R1())
    encoded = ephemeral.public_key().public_bytes(Encoding.X962, PublicFormat.UncompressedPoint)
    derived = hashlib.sha256(ephemeral.exchange(ec.ECDH(), public) + b'\x00\x00\x00\x01' + encoded).digest()
    clear = struct.pack('>iiBB', round(latitude * 1e7), round(longitude * 1e7), 12, 0)
    return struct.pack('>I', timestamp - 978307200) + b'\x00' + encoded + AESGCM(derived[:16]).encrypt(derived[16:], clear, None)


def test_apple_crypto_preserves_negative_coordinates_and_authenticates_tag():
    _id, private = identity(); timestamp = int(datetime.now(timezone.utc).timestamp())
    payload = encrypted_apple_fixture(private, timestamp, -12.3456789, -100.2345678)
    result = decrypt_apple(payload, private)
    assert result == (timestamp, -12.3456789, -100.2345678, 12)
    with pytest.raises(InvalidTag):
        decrypt_apple(payload[:-1] + bytes([payload[-1] ^ 1]), private)


def test_unsupported_apple_frame_rejected():
    with pytest.raises(ProviderError):
        decrypt_apple(b'wrong-format', bytes(28))


def test_login_rate_limit(tmp_path):
    app = create_app(Settings(str(tmp_path), os.urandom(32), PASSWORD, 'https://testserver'))
    with TestClient(app, base_url='https://testserver', headers=HEADERS) as client:
        for _ in range(5):
            assert client.post('/api/login', json={'username': 'owner', 'password': 'wrong'}).status_code == 401
        assert client.post('/api/login', json={'username': 'owner', 'password': PASSWORD}).status_code == 429


def test_report_filtering_and_network_failure_preserve_cached_history(tmp_path):
    class OfflineFixtureProviders:
        def __init__(self, *_): self.fail = False
        async def register(self, value, _): return value
        async def locations(self, value, _):
            if self.fail: raise ProviderError('NetworkUnavailableFixture')
            self.fail = True
            base = dict(trackerId=value['id'], observedAt=datetime.now(timezone.utc).isoformat(), latitude=0, longitude=0, accuracyMeters=1, source='Apple Find My', cryptographicallyVerified=True, reportId='offline-crypto-fixture')
            return [base, {**base, 'reportId': 'bad-coordinate', 'latitude': 500}, {**base, 'reportId': 'unverified', 'cryptographicallyVerified': False}]
    app = create_app(Settings(str(tmp_path), os.urandom(32), PASSWORD, 'https://testserver'), OfflineFixtureProviders)
    with TestClient(app, base_url='https://testserver', headers=HEADERS) as client:
        client.post('/api/login', json={'username': 'owner', 'password': PASSWORD}); data, _ = register(client)
        client.post('/api/trackers/' + data['id'] + '/refresh')
        assert len(client.get('/api/trackers/' + data['id'] + '/locations').json()['reports']) == 1
        client.post('/api/trackers/' + data['id'] + '/refresh')
        history = client.get('/api/trackers/' + data['id'] + '/locations').json()
        assert history['status'] == 'NetworkUnavailableFixture' and len(history['reports']) == 1
