"""Only real upstream reports enter the public history. No synthetic location fallback."""
import asyncio
import base64
import hashlib
import json
import os
import struct
import sys
from datetime import datetime, timezone
import httpx
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM


class ProviderError(Exception):
    def __init__(self, code):
        self.code = code


def apple_public(secret):
    private = int.from_bytes(secret, 'big')
    if len(secret) != 28 or not 0 < private < int('FFFFFFFFFFFFFFFFFFFFFFFFFFFF16A2E0B8F03E13DD29455C5C2A3D', 16):
        raise ProviderError('InvalidAppleKey')
    return ec.derive_private_key(private, ec.SECP224R1()).public_key().public_numbers().x.to_bytes(28, 'big')


def decrypt_apple(payload, secret):
    # Offline Finding report format / ANSI X9.63: SHA256(ECDH || counter || ephemeral public key).
    if len(payload) == 89:
        payload = payload[:4] + payload[5:]
    if len(payload) != 88:
        raise ProviderError('UnsupportedAppleReportFormat')
    private = ec.derive_private_key(int.from_bytes(secret, 'big'), ec.SECP224R1())
    ephemeral = ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP224R1(), payload[5:62])
    shared = private.exchange(ec.ECDH(), ephemeral)
    key = hashlib.sha256(shared + b'\x00\x00\x00\x01' + payload[5:62]).digest()
    clear = AESGCM(key[:16]).decrypt(key[16:], payload[62:], None)
    lat, lon, accuracy, _status = struct.unpack('>iiBB', clear)
    timestamp = int.from_bytes(payload[:4], 'big') + 978307200
    return timestamp, lat / 1e7, lon / 1e7, accuracy


class Providers:
    def __init__(self, store, apple_url, google_root):
        self.store, self.apple_url, self.google_root = store, apple_url, google_root
        self.google_lock = asyncio.Lock()

    async def google(self, operation, identity, secret):
        auth = self.store.secret('google-account')
        if not auth:
            raise ProviderError('GoogleAuthorizationRequired')
        if not self.google_root or not os.path.isdir(self.google_root):
            raise ProviderError('GoogleAdapterNotInstalled')
        async with self.google_lock:
            child = await asyncio.create_subprocess_exec(sys.executable, '-u', os.path.join(os.path.dirname(__file__), 'google_adapter.py'),
                stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL,
                env={**os.environ, 'GOOGLE_TOOLS_ROOT': self.google_root})
            try:
                request = json.dumps(dict(operation=operation, identity=identity, secret=base64.b64encode(secret).decode(), auth=auth)).encode()
                stdout, _ = await asyncio.wait_for(child.communicate(request), timeout=85)
            except (TimeoutError, asyncio.CancelledError):
                child.kill()
                await child.wait()
                raise ProviderError('GoogleRequestTimeout') from None
            try:
                data = json.loads(stdout)
            except (ValueError, UnicodeError):
                raise ProviderError('GoogleAdapterFailure') from None
            if data.get('auth'):
                self.store.put_secret('google-account', data['auth'])
            if data.get('code'):
                raise ProviderError(data['code'])
            return data

    async def register(self, identity, secret):
        if identity['protocol'] == 'AppleFindMy':
            if apple_public(secret).hex().upper() != ''.join(identity['publicKeyHex'].split()).upper():
                raise ProviderError('AppleKeyMismatch')
            return identity
        result = await self.google('register', identity, secret)
        return {**identity, 'canonicId': result['canonicId']}

    async def locations(self, identity, secret):
        if identity['protocol'] == 'GoogleFindHub':
            result = await self.google('locations', identity, secret)
            return result.get('reports', [])
        if not self.apple_url:
            raise ProviderError('AppleAuthorizationRequired')
        credentials = self.store.secret('apple-endpoint')
        auth = httpx.BasicAuth(credentials['username'], credentials['password']) if credentials else None
        key_id = base64.b64encode(hashlib.sha256(apple_public(secret)).digest()).decode()
        async with httpx.AsyncClient(timeout=60, follow_redirects=False) as client:
            try:
                response = await client.post(self.apple_url, json={'ids': [key_id], 'days': 7}, auth=auth)
                response.raise_for_status()
                data = response.json()
            except (httpx.HTTPError, ValueError):
                raise ProviderError('AppleUpstreamUnavailableOrUnauthorized') from None
        reports = []
        for entry in data.get('results', []):
            if entry.get('id') != key_id:
                continue
            try:
                raw = base64.b64decode(entry['payload'], validate=True)
                timestamp, latitude, longitude, accuracy = decrypt_apple(raw, secret)
                reports.append(dict(trackerId=identity['id'], observedAt=datetime.fromtimestamp(timestamp, timezone.utc).isoformat(),
                    latitude=latitude, longitude=longitude, accuracyMeters=accuracy, source='Apple Find My',
                    cryptographicallyVerified=True, reportId=hashlib.sha256(key_id.encode() + raw).hexdigest()))
            except (ValueError, KeyError, ProviderError):
                continue
            except Exception:
                # Invalid GCM tags are rejected, not converted into coordinates.
                continue
        return reports
