"""SQLite persistence. Secret records are AES-256-GCM envelopes bound to their purpose."""
import base64
import hashlib
import json
import os
import sqlite3
import time
from contextlib import contextmanager
from cryptography.hazmat.primitives.ciphers.aead import AESGCM


class Store:
    def __init__(self, directory, key):
        if len(key) != 32:
            raise ValueError('A 32-byte master key is required')
        os.makedirs(directory, mode=0o700, exist_ok=True)
        self.path = os.path.join(directory, 'tracking.sqlite3')
        self.aes = AESGCM(key)
        with self.db() as db:
            db.executescript('''
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS secrets (id TEXT PRIMARY KEY, envelope TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS trackers (id TEXT PRIMARY KEY, identity TEXT NOT NULL, status TEXT NOT NULL, queried REAL);
                CREATE TABLE IF NOT EXISTS reports (tracker TEXT NOT NULL, id TEXT NOT NULL, observed REAL NOT NULL, report TEXT NOT NULL, PRIMARY KEY(tracker,id));
                CREATE TABLE IF NOT EXISTS tokens (hash TEXT PRIMARY KEY, id TEXT UNIQUE NOT NULL, name TEXT NOT NULL, scope TEXT NOT NULL, expires REAL NOT NULL, revoked INTEGER NOT NULL DEFAULT 0);
            ''')
        if os.name != 'nt':
            os.chmod(self.path, 0o600)

    @contextmanager
    def db(self):
        connection = sqlite3.connect(self.path, timeout=30)
        connection.row_factory = sqlite3.Row
        try:
            yield connection
            connection.commit()
        finally:
            connection.close()

    def put_secret(self, purpose, value):
        nonce = os.urandom(12)
        envelope = base64.b64encode(nonce + self.aes.encrypt(nonce, json.dumps(value).encode(), purpose.encode())).decode()
        with self.db() as db:
            db.execute('INSERT OR REPLACE INTO secrets VALUES (?,?)', (purpose, envelope))

    def secret(self, purpose):
        with self.db() as db:
            row = db.execute('SELECT envelope FROM secrets WHERE id=?', (purpose,)).fetchone()
        if row is None:
            return None
        data = base64.b64decode(row['envelope'])
        return json.loads(self.aes.decrypt(data[:12], data[12:], purpose.encode()))

    @staticmethod
    def token_hash(token):
        return hashlib.sha256(token.encode()).hexdigest()

    def authenticate(self, token):
        with self.db() as db:
            return db.execute('SELECT id,scope FROM tokens WHERE hash=? AND expires>? AND revoked=0', (self.token_hash(token), time.time())).fetchone()
