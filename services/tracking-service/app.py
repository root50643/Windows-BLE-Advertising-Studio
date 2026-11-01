import asyncio
import base64
import hashlib
import json
import math
import os
import secrets
import time
import uuid
from contextlib import asynccontextmanager
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, FileResponse
from fastapi.staticfiles import StaticFiles
from argon2 import PasswordHasher
from argon2.exceptions import VerifyMismatchError
from providers import Providers, ProviderError
from store import Store


@dataclass
class Settings:
    directory: str
    master_key: bytes
    owner_password: str
    public_url: str
    apple_url: str = ''
    google_root: str = ''
    poll_seconds: int = 300
    secure_cookies: bool = True

    @classmethod
    def from_env(cls):
        def read(name):
            return Path(os.environ[name]).read_text().strip()
        public_url = os.environ['PUBLIC_URL'].rstrip('/')
        if not public_url.startswith('https://') or len(read('OWNER_PASSWORD_FILE')) < 16:
            raise ValueError('Public HTTPS and a password of at least 16 characters are required')
        return cls(os.environ.get('DATA_DIR', '/data'), base64.b64decode(read('MASTER_KEY_FILE'), validate=True),
                   read('OWNER_PASSWORD_FILE'), public_url, os.environ.get('APPLE_ENDPOINT_URL', ''), os.environ.get('GOOGLE_TOOLS_ROOT', ''))


def create_app(settings, provider_factory=Providers):
    store = Store(settings.directory, settings.master_key)
    ph = PasswordHasher()
    password_hash = ph.hash(settings.owner_password)
    settings.owner_password = ''
    providers = provider_factory(store, settings.apple_url, settings.google_root)
    rate = {}
    query_locks = {}

    async def query(tracker_id):
        lock = query_locks.setdefault(tracker_id, asyncio.Lock())
        if lock.locked():
            return
        async with lock:
            with store.db() as db:
                row = db.execute('SELECT identity FROM trackers WHERE id=?', (tracker_id,)).fetchone()
            if not row:
                return
            try:
                identity = json.loads(row['identity'])
                secret = base64.b64decode(store.secret('identity:' + tracker_id))
                reports = await providers.locations(identity, secret)
                count = 0
                with store.db() as db:
                    for r in reports:
                        observed = datetime.fromisoformat(r['observedAt'].replace('Z', '+00:00')).timestamp()
                        if r.get('trackerId') != tracker_id or not r.get('cryptographicallyVerified') or r.get('source') != ('Apple Find My' if identity['protocol'] == 'AppleFindMy' else 'Google Find Hub'):
                            continue
                        lat, lon, accuracy = r['latitude'], r['longitude'], r['accuracyMeters']
                        if not all(isinstance(v, (float, int)) and math.isfinite(v) for v in (lat, lon, accuracy)) or not (-90 <= lat <= 90 and -180 <= lon <= 180 and accuracy >= 0 and 0 < observed <= time.time() + 300):
                            continue
                        db.execute('INSERT OR IGNORE INTO reports VALUES (?,?,?,?)', (tracker_id, r['reportId'], observed, json.dumps(r)))
                        count += 1
                    db.execute('UPDATE trackers SET status=?,queried=? WHERE id=?', ('ReportsDecrypted' if count else 'NoLocationReports', time.time(), tracker_id))
                    db.execute('DELETE FROM reports WHERE observed<?', (time.time() - 30 * 86400,))
            except ProviderError as error:
                with store.db() as db:
                    db.execute('UPDATE trackers SET status=?,queried=? WHERE id=?', (error.code, time.time(), tracker_id))
            except Exception:
                with store.db() as db:
                    db.execute('UPDATE trackers SET status=?,queried=? WHERE id=?', ('ProviderFailure', time.time(), tracker_id))

    async def poll():
        while True:
            with store.db() as db:
                db.execute('DELETE FROM reports WHERE observed<?', (time.time() - 30 * 86400,))
                ids = [r['id'] for r in db.execute('SELECT id FROM trackers')]
            for tracker_id in ids:
                await query(tracker_id)
            await asyncio.sleep(settings.poll_seconds)

    @asynccontextmanager
    async def lifespan(_app):
        task = asyncio.create_task(poll())
        yield
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass

    app = FastAPI(title='BLE Studio location service', docs_url=None, redoc_url=None, openapi_url=None, lifespan=lifespan)
    app.state.store = store
    app.state.query = query

    def error(code, status=400):
        return JSONResponse({'code': code}, status_code=status)

    def principal(request):
        authorization = request.headers.get('authorization', '')
        token = authorization[7:] if authorization.startswith('Bearer ') else request.cookies.get('ble_session', '')
        return store.authenticate(token) if token else None

    def owner(request):
        p = principal(request)
        return p is not None and p['scope'] == 'owner'

    @app.middleware('http')
    async def guard(request, call_next):
        if int(request.headers.get('content-length', '0') or '0') > 1024 * 1024:
            return error('RequestTooLarge', 413)
        if request.url.path.startswith('/api/') and request.method != 'GET' and not request.headers.get('authorization', '').startswith('Bearer ') and request.headers.get('x-requested-with') != 'BLEStudio':
            return error('CsrfRejected', 403)
        if request.url.path.startswith('/api/') and request.url.path not in ('/api/login', '/api/health'):
            if principal(request) is None:
                return error('Unauthorized', 401)
            if request.method != 'GET' and not request.headers.get('authorization', '').startswith('Bearer ') and request.headers.get('x-requested-with') != 'BLEStudio':
                return error('CsrfRejected', 403)
        response = await call_next(request)
        response.headers['Cache-Control'] = 'no-store' if request.url.path.startswith('/api/') else 'no-cache'
        response.headers['X-Content-Type-Options'] = 'nosniff'
        response.headers['Referrer-Policy'] = 'strict-origin-when-cross-origin'
        response.headers['Content-Security-Policy'] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: https://tile.openstreetmap.org; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'"
        return response

    def issue(name, scope, days):
        token, token_id = secrets.token_urlsafe(48), str(uuid.uuid4())
        with store.db() as db:
            db.execute('INSERT INTO tokens(hash,id,name,scope,expires) VALUES (?,?,?,?,?)', (store.token_hash(token), token_id, name, scope, time.time() + days * 86400))
        return token, token_id

    @app.get('/api/health')
    async def health():
        return {'status': 'ok', 'version': '1.1.0-rc.1'}

    @app.post('/api/login')
    async def login(request: Request):
        ip = request.client.host
        failures = [t for t in rate.get(ip, []) if t > time.monotonic() - 600]
        rate[ip] = failures
        if len(failures) >= 5:
            return error('LoginRateLimited', 429)
        try:
            data = await request.json()
            valid = ph.verify(password_hash, data.get('password', '')) and data.get('username') == 'owner'
        except (VerifyMismatchError, ValueError, TypeError):
            valid = False
        if not valid:
            failures.append(time.monotonic())
            return error('InvalidCredentials', 401)
        token, _ = issue('Owner browser session', 'owner', 1)
        response = JSONResponse({'status': 'ok'})
        response.set_cookie('ble_session', token, httponly=True, secure=settings.secure_cookies, samesite='strict', max_age=86400)
        return response

    @app.post('/api/logout')
    async def logout(request: Request):
        p = principal(request)
        with store.db() as db:
            db.execute('UPDATE tokens SET revoked=1 WHERE id=?', (p['id'],))
        response = JSONResponse({'status': 'ok'}); response.delete_cookie('ble_session')
        return response

    @app.get('/api/tokens')
    async def tokens(request: Request):
        if not owner(request):
            return error('OwnerRequired', 403)
        with store.db() as db:
            return [dict(r) for r in db.execute("SELECT id,name,expires,revoked FROM tokens WHERE scope='desktop' ORDER BY expires DESC")]

    @app.post('/api/tokens')
    async def new_token(request: Request):
        if not owner(request):
            return error('OwnerRequired', 403)
        data = await request.json(); name = str(data.get('name', 'Windows PC'))[:80]
        token, token_id = issue(name, 'desktop', 90)
        return {'url': settings.public_url, 'token': token, 'id': token_id, 'expiresInDays': 90}

    @app.delete('/api/tokens/{token_id}')
    async def revoke(token_id: str, request: Request):
        if not owner(request):
            return error('OwnerRequired', 403)
        with store.db() as db:
            db.execute("UPDATE tokens SET revoked=1 WHERE id=? AND scope='desktop'", (token_id,))
        return {'status': 'revoked'}

    @app.post('/api/providers/google')
    async def google_authorize(request: Request):
        if not owner(request):
            return error('OwnerRequired', 403)
        data = await request.json()
        if not isinstance(data, dict) or not data:
            return error('InvalidGoogleAuthorization')
        store.put_secret('google-account', data)
        return {'status': 'AuthorizationImportedNotYetVerified'}

    @app.post('/api/providers/apple')
    async def apple_authorize(request: Request):
        if not owner(request):
            return error('OwnerRequired', 403)
        data = await request.json()
        if not all(isinstance(data.get(k), str) and data[k] for k in ('username', 'password')):
            return error('InvalidAppleEndpointCredential')
        store.put_secret('apple-endpoint', {k: data[k] for k in ('username', 'password')})
        return {'status': 'EndpointCredentialImportedNotYetVerified'}

    @app.delete('/api/providers/{provider}')
    async def remove_provider(provider: str, request: Request):
        if not owner(request):
            return error('OwnerRequired', 403)
        if provider not in ('apple', 'google'):
            return error('UnknownProvider', 404)
        with store.db() as db:
            db.execute('DELETE FROM secrets WHERE id=?', ('apple-endpoint' if provider == 'apple' else 'google-account',))
        return {'status': 'LocalAuthorizationRemoved; revoke upstream account access separately'}

    @app.get('/api/trackers')
    async def trackers():
        with store.db() as db:
            return [{**json.loads(r['identity']), 'status': r['status']} for r in db.execute('SELECT * FROM trackers')]

    @app.post('/api/trackers/{tracker_id}/register')
    async def register(tracker_id: str, request: Request):
        try:
            tracker_id = str(uuid.UUID(tracker_id)); data = await request.json()
            if str(uuid.UUID(data['id'])) != tracker_id or data['protocol'] not in ('AppleFindMy', 'GoogleFindHub'):
                return error('InvalidIdentity')
            secret = base64.b64decode(data['secretBase64'], validate=True)
            if len(secret) != (28 if data['protocol'] == 'AppleFindMy' else 32):
                return error('InvalidKeyLength')
            paired = datetime.fromisoformat(data['pairDate'].replace('Z', '+00:00'))
            if paired.tzinfo is None or paired.timestamp() <= 0 or paired.timestamp() > time.time() + 300:
                return error('InvalidPairDate')
            identity = {k: data.get(k) for k in ('id', 'name', 'protocol', 'pairDate', 'publicKeyHex', 'canonicId')}
            identity['name'] = str(identity['name'])[:80]
            with store.db() as db:
                existing = db.execute('SELECT identity FROM trackers WHERE id=?', (tracker_id,)).fetchone()
            if existing:
                # Never silently overwrite an established identity or its key material.
                if store.secret('identity:' + tracker_id) != data['secretBase64']:
                    return error('IdentityKeyConflict', 409)
                return json.loads(existing['identity'])
            identity = await providers.register(identity, secret)
            store.put_secret('identity:' + tracker_id, data['secretBase64'])
            with store.db() as db:
                db.execute('INSERT INTO trackers VALUES (?,?,?,NULL)', (tracker_id, json.dumps(identity), 'RegisteredNoLocationReports'))
            return identity
        except ProviderError as e:
            return error(e.code, 502)
        except (KeyError, ValueError, TypeError):
            return error('InvalidIdentity')

    @app.get('/api/trackers/{tracker_id}/locations')
    async def locations(tracker_id: str):
        with store.db() as db:
            row = db.execute('SELECT status,queried FROM trackers WHERE id=?', (tracker_id,)).fetchone()
            if row is None:
                return error('TrackerNotFound', 404)
            reports = [json.loads(r['report']) for r in db.execute('SELECT report FROM reports WHERE tracker=? ORDER BY observed DESC LIMIT 10000', (tracker_id,))]
        return {'reports': reports, 'status': row['status'], 'lastQueryAt': datetime.fromtimestamp(row['queried'], timezone.utc).isoformat() if row['queried'] else None}

    @app.post('/api/trackers/{tracker_id}/refresh')
    async def refresh(tracker_id: str):
        with store.db() as db:
            if not db.execute('SELECT id FROM trackers WHERE id=?', (tracker_id,)).fetchone():
                return error('TrackerNotFound', 404)
        await query(tracker_id)
        return {'status': 'Queried'}

    static = Path(__file__).parent / 'static'
    app.mount('/static', StaticFiles(directory=static), name='static')

    @app.get('/')
    async def index():
        return FileResponse(static / 'index.html')

    return app
