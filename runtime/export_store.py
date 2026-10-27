"""One-time move off SQLite: writes the CulinaryOS store, opened with its keyring, as JSON lines on stdout.

The installer pipes this straight into `MesaNexo.Api --migrate-from-export -`, so the opened data never touches the
disk. Events carry the exact canonical bytes their digest covers, so a central that already received one sees the
same event again. The SQLite file is opened read-only and left as it was.
"""
import argparse
import base64
import hashlib
import json
import sqlite3
import sys
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--lib', default='/usr/local/lib/culinaryos', help='folder with store.py and securedata.py')
parser.add_argument('--database', default='/var/lib/culinaryos-data/store.sqlite')
parser.add_argument('--keyring', required=True)
parser.add_argument('--site', required=True)
args = parser.parse_args()
sys.path.insert(0, args.lib)
from securedata import Keyring, canonical

keys = Keyring.load(args.keyring)
site = args.site

def context(purpose, record_id, version, event_id=None):
    value = {'site_id': site, 'purpose': purpose, 'record_id': record_id, 'version': version}
    if event_id:
        value['event_id'] = event_id
    return value

def emit(value):
    sys.stdout.write(json.dumps(value, ensure_ascii=False, separators=(',', ':')) + '\n')

db = sqlite3.connect('file:' + str(Path(args.database).resolve()) + '?mode=ro', uri=True)
db.row_factory = sqlite3.Row
db.execute('BEGIN')
if db.execute("SELECT value FROM metadata WHERE key='site_id'").fetchone()[0] != site:
    raise SystemExit('El almacén pertenece a otra sucursal')
check = db.execute("SELECT value FROM metadata WHERE key='key_check'").fetchone()
if keys.open(json.loads(check[0]), {'site_id': site, 'purpose': 'key-check-v1'}) != {'schema': 1}:
    raise SystemExit('Claves no válidas para este almacén')
emit({'type': 'header', 'site_id': site, 'schema': 1})
for row in db.execute("SELECT key, value FROM metadata WHERE key NOT IN ('site_id', 'key_check') ORDER BY key"):
    emit({'type': 'metadata', 'key': row['key'], 'value': row['value']})
records = 0
for row in db.execute('SELECT id, version, envelope FROM records ORDER BY rowid'):
    emit({'type': 'record', 'id': row['id'], 'version': row['version'],
          'data': keys.open(json.loads(row['envelope']), context('record', row['id'], row['version']))})
    records += 1
events = 0
for row in db.execute('SELECT * FROM events ORDER BY rowid'):
    raw = canonical(keys.open(json.loads(row['envelope']), context('event', row['record_id'], row['version'], row['id'])))
    if hashlib.sha256(raw).hexdigest() != row['digest']:
        raise SystemExit('Integridad de evento inválida: ' + row['id'])
    emit({'type': 'event', 'id': row['id'], 'record_id': row['record_id'], 'version': row['version'],
          'raw': base64.b64encode(raw).decode('ascii'), 'digest': row['digest'], 'created': row['created'],
          'acked': row['acked'], 'attempts': row['attempts'], 'next_attempt': row['next_attempt']})
    events += 1
emit({'type': 'end', 'records': records, 'events': events})
