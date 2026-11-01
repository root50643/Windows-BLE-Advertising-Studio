"""Run once on your Linux/VPS. Never put generated secrets into Git or a deployment ZIP."""
import base64
import configparser
import getpass
import os
from pathlib import Path
import re
import secrets

os.umask(0o077)
root = Path(__file__).resolve().parent
if (root / '.env').exists():
    raise SystemExit('Already configured. Existing identity keys and credentials were left intact.')
domain = input('Public DNS hostname (A/AAAA must point to this VPS): ').strip().lower()
if not re.fullmatch(r'(?=.{1,253}$)[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?', domain) or '.' not in domain:
    raise SystemExit('Enter a DNS hostname without scheme, port or path.')
password = getpass.getpass('New owner password (at least 16 characters): ')
if len(password) < 16 or password != getpass.getpass('Repeat password: '):
    raise SystemExit('Password too short or confirmation mismatch; no configuration was created.')
for directory in ('secrets', 'data', 'apple-data'):
    (root / directory).mkdir(mode=0o700, exist_ok=True)
(root / 'secrets/master_key').write_text(base64.b64encode(secrets.token_bytes(32)).decode())
(root / 'secrets/owner_password').write_text(password)
(root / '.env').write_text(f'DOMAIN={domain}\nSERVICE_UID={os.getuid() if hasattr(os,"getuid") else 1000}\nSERVICE_GID={os.getgid() if hasattr(os,"getgid") else 1000}\n')
config = configparser.ConfigParser()
config['Settings'] = dict(port='6176', binding_address='0.0.0.0', anisette_url='http://anisette:6969', loglevel='WARNING',
    endpoint_user='ble-studio', endpoint_pass=secrets.token_urlsafe(48))
with (root / 'apple-data/config.ini').open('w') as output:
    config.write(output)
print('Configured. Run: docker compose up -d --build')
print('Open https://' + domain + ' and sign in as owner. Apple endpoint credentials are in apple-data/config.ini; never publish that file.')
