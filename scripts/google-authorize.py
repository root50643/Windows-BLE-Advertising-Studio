"""Interactive account setup on the owner's computer; no BLE device/firmware is involved.
Install the pinned GoogleFindMyTools requirements first. Requires a local Chrome browser.
"""
import argparse
import builtins
import contextlib
import getpass
import json
import os
from pathlib import Path
import sys

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--tools', type=Path, required=True, help='GoogleFindMyTools checkout at 660be5395702ff7f1a6174f4ec4c865a57c7b527')
parser.add_argument('--output', type=Path, required=True, help='Private authorization JSON; upload through your HTTPS owner page, then delete')
args = parser.parse_args()
os.umask(0o077)
args.output.parent.mkdir(parents=True, exist_ok=True)
sys.path.insert(0, str(args.tools.resolve()))
import Auth.token_cache as cache
cache._get_secrets_file = lambda: str(args.output.resolve())
console = sys.stdout
def private_prompt(prompt=''):
    console.write(str(prompt)); console.flush()
    return getpass.getpass('', stream=console)
builtins.input = private_prompt
print('Complete Google sign-in in Chrome and any owner-key prompts locally. No firmware will be generated.')
try:
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        from SpotApi.GetEidInfoForE2eeDevices.get_owner_key import get_owner_key
        get_owner_key()
    data = json.loads(args.output.read_text())
    if not data.get('owner_key'):
        raise ValueError('Owner key not obtained')
    print('Authorization saved privately. Upload this JSON at your own HTTPS service → Owner settings → Google. Delete the plaintext export after import.')
except BaseException:
    print('Authorization did not complete. Check the account/browser prerequisites in deploy/README.en.md; no account secrets were printed.')
    raise SystemExit(1) from None
