"""Extension v1 of CulinaryOS, same SQLite/keyring/outbox and crypto format.
Not a second store. Deployment adds one import hook to dataio.dispatch.
"""
import hashlib
import json
import shutil
import time
import os
from pathlib import Path
import uuid
from store import identity, VersionConflict, EventConflict, StorageFull
from securedata import canonical

MAX_TRANSACTION = 56 * 1024
# Default pages fit clients that read replies of up to 128 KiB; newer clients ask for bigger ones.
PAGE_BYTES, PAGE_RECORDS = 100 * 1024, 32
MAX_PAGE_BYTES, MAX_PAGE_RECORDS = 1024 * 1024, 4096
# The central acknowledges an event only after committing it durably. Acknowledged events are kept this long and
# then dropped a few per transaction; unacknowledged ones are never dropped, so delivery stays at least once.
ACKED_RETENTION = 7 * 24 * 3600
COMPACT_BATCH = 16
# The same service-controlled file the sync worker reads; never supplied by a terminal. Without a central nothing
# will ever acknowledge the outbox, so pending events are kept this long and then dropped. Connecting a central later
# first queues a baseline: every mn_ record at its current version, audits included, in events of at most 56 KiB.
BRANCH_CONFIG = Path('/etc/culinaryos/branch.json')
PENDING_RETENTION = 7 * 24 * 3600
MAINTAIN_EVENTS = 256
BASELINE_EVENTS = 1

def transaction_v1(store, request):
    if request.get('protocol') != 1:
        raise ValueError('protocol')
    event_id = identity(request['event_id'])
    receipt = request['receipt']
    changes = request['changes']
    if not isinstance(receipt, dict) or not isinstance(changes, list) or not 1 <= len(changes) <= 256:
        raise ValueError('transaction bounds')
    ids = [identity(change['record_id']) for change in changes]
    if len(ids) != len(set(ids)) or not all(x.startswith('mn_') for x in ids):
        raise ValueError('record ids')
    if 'mn_meta_site' not in ids:
        raise ValueError('serializable revision required')
    for change in changes:
        version = change['expected_version']
        if type(version) is not int or version < 0 or not isinstance(change['data'], dict):
            raise ValueError('version or data')
    wire = {'protocol': 'culinaryos-events-v1', 'site_id': store.site,
            'event_id': event_id, 'record_id': 'tx_' + event_id, 'version': 1,
            'data': {'contract': 'mesa-nexo-transaction-v1', 'changes': changes, 'receipt': receipt}}
    if len(canonical(wire)) > MAX_TRANSACTION:
        raise ValueError('transaction exceeds 56 KiB; split the command before confirmation')
    digest = hashlib.sha256(canonical(wire)).hexdigest()
    with store.connection() as db:
        db.execute('BEGIN IMMEDIATE')
        prior = db.execute('SELECT envelope FROM records WHERE id=?', ('tx_' + event_id,)).fetchone()
        if prior:
            existing = store.keys.open(json.loads(prior['envelope']), store.context('record', 'tx_' + event_id, 1))
            if existing.get('fingerprint') != receipt.get('fingerprint') or existing.get('actor') != receipt.get('actor'):
                raise EventConflict('operation id reused')
            return {'duplicate': True, 'receipt': existing, 'event_id': event_id}
        for change in changes:
            row = db.execute('SELECT version FROM records WHERE id=?', (change['record_id'],)).fetchone()
            if (row['version'] if row else 0) != change['expected_version']:
                raise VersionConflict('stale state')
        if shutil.disk_usage(store.path.parent).free < store.min_free_bytes:
            raise StorageFull('disk reserve')
        for change in changes:
            rid = change['record_id']
            version = change['expected_version'] + 1
            envelope = store.keys.seal(change['data'], store.context('record', rid, version))
            db.execute('INSERT INTO records VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET version=excluded.version,envelope=excluded.envelope',
                       (rid, version, canonical(envelope).decode()))
        rid = 'tx_' + event_id
        encrypted_receipt = store.keys.seal(receipt, store.context('record', rid, 1))
        db.execute('INSERT INTO records VALUES(?,?,?)', (rid, 1, canonical(encrypted_receipt).decode()))
        encrypted_event = store.keys.seal(wire, store.context('event', rid, 1, event_id))
        db.execute('INSERT INTO events(id,record_id,version,envelope,digest,created) VALUES(?,?,?,?,?,?)',
                   (event_id, rid, 1, canonical(encrypted_event).decode(), digest, time.time()))
        db.execute('DELETE FROM events WHERE rowid IN (SELECT rowid FROM events WHERE acked<? LIMIT ?)',
                   (time.time() - ACKED_RETENTION, COMPACT_BATCH))
    return {'duplicate': False, 'receipt': receipt, 'event_id': event_id}

def _page(request):
    size = request.get('max_bytes', PAGE_BYTES)
    count = request.get('max_records', PAGE_RECORDS)
    if type(size) is not int or type(count) is not int or not 1024 <= size <= MAX_PAGE_BYTES or not 1 <= count <= MAX_PAGE_RECORDS:
        raise ValueError('page bounds')
    return size, count

def _kind(prefix):
    # Every record of a kind is mn_<kind>_<id>, so a kind is one contiguous id range.
    if not isinstance(prefix, str) or len(prefix) < 5 or not prefix.startswith('mn_') or not prefix.endswith('_'):
        raise ValueError('prefix')
    return identity(prefix)

def _revision(db):
    row = db.execute("SELECT version FROM records WHERE id='mn_meta_site'").fetchone()
    return row['version'] if row else 0

def _fill(store, rows, size, count):
    # Byte bounded pages, not unbounded list responses.
    records = []
    total = 256
    for row in rows:
        value = {'record_id': row['id'], 'version': row['version'],
               'data': store.keys.open(json.loads(row['envelope']), store.context('record', row['id'], row['version']))}
        length = len(canonical(value)) + 1
        if records and (total + length > size or len(records) >= count):
            return records, True
        records.append(value)
        total += length
    return records, False

def scan_v1(store, request):
    if request.get('protocol') != 1:
        raise ValueError('protocol')
    prefix = request.get('prefix', 'mn_')
    after = request.get('after', '')
    if prefix != 'mn_' or (after and not after.startswith(prefix)):
        raise ValueError('prefix')
    if after:
        identity(after)
    size, count = _page(request)
    exclude = request.get('exclude', [])
    if not isinstance(exclude, list) or len(exclude) > 8:
        raise ValueError('exclude')
    # Excluded kinds are cut out of the id range, so their records are never read nor decrypted.
    ranges = [(prefix, prefix + '~')]
    for kind in sorted(set(map(_kind, exclude))):
        ranges = [part for low, high in ranges for part in ((low, min(high, kind)), (max(low, kind + '~'), high)) if part[0] < part[1]]
    # Each page names its revision.
    with store.connection() as db:
        db.execute('BEGIN')
        revision = _revision(db)
        rows = []
        for low, high in ranges:
            if len(rows) > count:
                break
            rows += db.execute('SELECT * FROM records WHERE id>=? AND id<? AND id>? ORDER BY id LIMIT ?',
                             (low, high, after, count + 1 - len(rows))).fetchall()
        records, more = _fill(store, rows, size, count)
        return {'records': records, 'revision': revision, 'after': records[-1]['record_id'] if more else None}

def recent_v1(store, request):
    """The newest records of one kind, newest first. Records are never deleted, so rowid follows insertion."""
    if request.get('protocol') != 1:
        raise ValueError('protocol')
    kind = _kind(request.get('prefix'))
    size, count = _page(request)
    limit = request.get('limit')
    before = request.get('before')
    if type(limit) is not int or not 1 <= limit <= 100000 or (before is not None and (type(before) is not int or before < 1)):
        raise ValueError('bounds')
    count = min(count, limit)
    with store.connection() as db:
        db.execute('BEGIN')
        revision = _revision(db)
        # Positions come from the id index alone; only the rows returned are read and decrypted.
        rows = db.execute('SELECT rowid AS position,* FROM records WHERE rowid IN (SELECT rowid FROM records INDEXED BY sqlite_autoindex_records_1 '
                        'WHERE id>=? AND id<? AND rowid<? ORDER BY rowid DESC LIMIT ?) ORDER BY rowid DESC',
                        (kind, kind + '~', before or 1 << 62, count + 1)).fetchall()
        records, more = _fill(store, rows, size, count)
        more = more and len(records) < limit
        return {'records': records, 'revision': revision, 'before': rows[len(records) - 1]['position'] if more else None}

def _central():
    try:
        config = json.loads(BRANCH_CONFIG.read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return None
    return bool(config.get('central')) if isinstance(config, dict) and config.get('version') == 1 else None

def _baseline_event(store, db, after, part):
    # Fills one event with the next records by id; returns the last id it carries, or None when none are left.
    changes = []
    size = 1024
    for row in db.execute("SELECT * FROM records WHERE id>='mn_' AND id<'mn_~' AND id>? ORDER BY id LIMIT 512", (after,)):
        change = {'record_id': row['id'], 'expected_version': row['version'] - 1,
                'data': store.keys.open(json.loads(row['envelope']), store.context('record', row['id'], row['version']))}
        length = len(canonical(change)) + 1
        if changes and size + length > MAX_TRANSACTION:
            break
        changes.append(change)
        size += length
    if not changes:
        return None
    event_id = 'bl' + uuid.uuid4().hex
    rid = 'bl_' + event_id
    wire = {'protocol': 'culinaryos-events-v1', 'site_id': store.site, 'event_id': event_id, 'record_id': rid, 'version': 1,
          'data': {'contract': 'mesa-nexo-baseline-v1', 'changes': changes,
                  'receipt': {'actor': 'outbox-baseline', 'operation': 'outbox.baseline', 'part': part,
                             'at': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}}}
    raw = canonical(wire)
    # transaction_v1 wrote every mn_ record inside a 56 KiB event, so one record always fits.
    if len(raw) > MAX_TRANSACTION:
        raise ValueError('baseline event exceeds 56 KiB')
    encrypted = store.keys.seal(wire, store.context('event', rid, 1, event_id))
    db.execute('INSERT INTO events(id,record_id,version,envelope,digest,created) VALUES(?,?,?,?,?,?)',
               (event_id, rid, 1, canonical(encrypted).decode(), hashlib.sha256(raw).hexdigest(), time.time()))
    return changes[-1]['record_id']

def outbox_maintain_v1(store, request):
    """One bounded step of outbox upkeep; `more` asks the caller to come back soon."""
    if request.get('protocol') != 1:
        raise ValueError('protocol')
    central = _central()
    if central is None:
        return {'mode': 'unknown', 'more': False}
    with store.connection() as db:
        db.execute('BEGIN IMMEDIATE')
        row = db.execute("SELECT value FROM metadata WHERE key='mesanexo_outbox'").fetchone()
        # No state: an outbox that has never dropped anything, so a central can still receive all of it.
        state = json.loads(row['value']) if row else {'mode': 'complete'}
        before = dict(state)
        result = {}
        if not central:
            state = {'mode': 'standalone'}
            cutoff = time.time() - PENDING_RETENTION
            dropped = []
            for event in db.execute('SELECT rowid,created FROM events WHERE acked IS NULL ORDER BY rowid LIMIT ?', (MAINTAIN_EVENTS,)):
                if event['created'] >= cutoff:
                    break
                dropped.append((event['rowid'],))
            db.executemany('DELETE FROM events WHERE rowid=?', dropped)
            # From now on a missing event may never have reached a central: event_status_v1 must not claim it did.
            if dropped:
                db.execute("INSERT OR IGNORE INTO metadata VALUES('mesanexo_outbox_dropped','1')")
            result = {'dropped': len(dropped), 'more': len(dropped) == MAINTAIN_EVENTS}
        else:
            if state['mode'] == 'standalone':
                state = {'mode': 'baseline', 'after': '', 'events': 0, 'started': time.time()}
            if state['mode'] == 'baseline':
                if shutil.disk_usage(store.path.parent).free < store.min_free_bytes:
                    raise StorageFull('disk reserve')
                for _ in range(BASELINE_EVENTS):
                    last = _baseline_event(store, db, state['after'], state['events'] + 1)
                    if last is None:
                        state = {'mode': 'complete', 'baseline_events': state['events'], 'baseline_completed': time.time()}
                        break
                    state['after'] = last
                    state['events'] += 1
            result = {'more': state['mode'] == 'baseline'}
        if state != before:
            db.execute("INSERT INTO metadata VALUES('mesanexo_outbox',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", (json.dumps(state),))
    return {'mode': state['mode'], **result}

def dispatch(store, request, original):
    op = request.get('op') if isinstance(request, dict) else None
    if op == 'capabilities_v1':
        return {'protocol': 1, 'extensions': ['transaction_v1', 'scan_v1', 'recent_v1', 'event_status_v1', 'outbox_status_v1', 'outbox_maintain_v1', 'snapshot_v1'], 'max_transaction': MAX_TRANSACTION}
    if op == 'snapshot_v1':
        if request.get('protocol') != 1:
            raise ValueError('protocol')
        # Destination is service-controlled, never supplied by the terminal.
        root = store.path.parent / 'schema-backups'
        if root.is_symlink():
            raise ValueError('snapshot directory cannot be a symlink')
        root.mkdir(mode=0o700, exist_ok=True)
        if os.name == 'posix':
            os.chmod(root, 0o700)
        path = root / ('schema-' + uuid.uuid4().hex + '.sqlite')
        store.snapshot(path)
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        return {'backup': path.name, 'sha256': digest, 'site_id': store.site, 'encrypted': True}
    if op == 'transaction_v1':
        return transaction_v1(store, request)
    if op == 'scan_v1':
        return scan_v1(store, request)
    if op == 'recent_v1':
        return recent_v1(store, request)
    if op == 'outbox_maintain_v1':
        return outbox_maintain_v1(store, request)
    if op == 'outbox_status_v1':
        # The outbox count without health's integrity check, which reads the whole file.
        with store.connection() as db:
            pending, oldest = db.execute('SELECT count(*),min(created) FROM events WHERE acked IS NULL').fetchone()
        return {'pending': pending, 'oldest_pending': oldest}
    if op == 'event_status_v1':
        identity(request['event_id'])
        with store.connection() as db:
            row = db.execute('SELECT acked FROM events WHERE id=?', (request['event_id'],)).fetchone()
            # A receipt without its event: compacted after the central acknowledged it, unless this branch once ran
            # without a central and dropped unacknowledged events, in which case nobody can tell any more.
            if row is None and db.execute('SELECT 1 FROM records WHERE id=?', ('tx_' + request['event_id'],)).fetchone():
                dropped = db.execute("SELECT 1 FROM metadata WHERE key='mesanexo_outbox_dropped'").fetchone()
                return {'local': 'committed', 'remote': 'untracked' if dropped else 'acknowledged'}
        return {'local': 'committed' if row else 'unknown', 'remote': 'acknowledged' if row and row['acked'] else 'pending' if row else 'unknown'}
    return original(store, request)

def install(dataio):
    original = dataio.dispatch
    dataio.dispatch = lambda store, request: dispatch(store, request, original)