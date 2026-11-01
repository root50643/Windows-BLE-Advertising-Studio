import os
os.umask(0o077)
from app import Settings, create_app
app = create_app(Settings.from_env())
