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
    wire = {'protocol':'culinaryos-events-v1', 'site_id':store.site,
            'event_id':event_id, 'record_id':'tx_'+event_id, 'version':1,
            'data':{'contract':'mesa-nexo-transaction-v1','changes':changes,'receipt':receipt}}
    if len(canonical(wire)) > MAX_TRANSACTION:
        raise ValueError('transaction exceeds 56 KiB; split the command before confirmation')
    digest = hashlib.sha256(canonical(wire)).hexdigest()
    with store.connection() as db:
        db.execute('BEGIN IMMEDIATE')
        prior = db.execute('SELECT envelope FROM records WHERE id=?', ('tx_'+event_id,)).fetchone()
        if prior:
            existing = store.keys.open(json.loads(prior['envelope']),store.context('record','tx_'+event_id,1))
            if existing.get('fingerprint') != receipt.get('fingerprint') or existing.get('actor') != receipt.get('actor'):
                raise EventConflict('operation id reused')
            return {'duplicate':True,'receipt':existing,'event_id':event_id}
        for change in changes:
            row = db.execute('SELECT version FROM records WHERE id=?',(change['record_id'],)).fetchone()
            if (row['version'] if row else 0) != change['expected_version']:
                raise VersionConflict('stale state')
        if shutil.disk_usage(store.path.parent).free < store.min_free_bytes:
            raise StorageFull('disk reserve')
        for change in changes:
            rid=change['record_id']; version=change['expected_version']+1
            envelope=store.keys.seal(change['data'],store.context('record',rid,version))
            db.execute('INSERT INTO records VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET version=excluded.version,envelope=excluded.envelope',
                       (rid,version,canonical(envelope).decode()))
        rid='tx_'+event_id
        encrypted_receipt=store.keys.seal(receipt,store.context('record',rid,1))
        db.execute('INSERT INTO records VALUES(?,?,?)',(rid,1,canonical(encrypted_receipt).decode()))
        encrypted_event=store.keys.seal(wire,store.context('event',rid,1,event_id))
        db.execute('INSERT INTO events(id,record_id,version,envelope,digest,created) VALUES(?,?,?,?,?,?)',
                   (event_id,rid,1,canonical(encrypted_event).decode(),digest,time.time()))
    return {'duplicate':False,'receipt':receipt,'event_id':event_id}

def scan_v1(store, request):
    if request.get('protocol') != 1: raise ValueError('protocol')
    prefix=request.get('prefix','mn_'); after=request.get('after','')
    if prefix != 'mn_' or (after and not after.startswith(prefix)): raise ValueError('prefix')
    if after: identity(after)
    # Byte bounded pages, not unbounded list responses. Each page names its revision.
    with store.connection() as db:
        db.execute('BEGIN')
        revision=db.execute("SELECT version FROM records WHERE id='mn_meta_site'").fetchone()
        rows=db.execute('SELECT * FROM records WHERE id>=? AND id<? AND id>? ORDER BY id LIMIT 33',
                        (prefix,prefix+'~',after)).fetchall()
        records=[]; size=256
        for row in rows:
            value={'record_id':row['id'],'version':row['version'],
                   'data':store.keys.open(json.loads(row['envelope']),store.context('record',row['id'],row['version']))}
            length=len(canonical(value))
            if records and (size+length > 100*1024 or len(records)>=32): break
            records.append(value);size+=length
        more=len(records)<len(rows)
        return {'records':records,'revision':revision['version'] if revision else 0,
                'after':records[-1]['record_id'] if more else None}

def dispatch(store, request, original):
    op=request.get('op') if isinstance(request,dict) else None
    if op=='capabilities_v1':
        return {'protocol':1,'extensions':['transaction_v1','scan_v1','event_status_v1','snapshot_v1'],'max_transaction':MAX_TRANSACTION}
    if op=='snapshot_v1':
        if request.get('protocol') != 1: raise ValueError('protocol')
        # Destination is service-controlled, never supplied by the terminal.
        root=store.path.parent/'schema-backups'
        if root.is_symlink(): raise ValueError('snapshot directory cannot be a symlink')
        root.mkdir(mode=0o700,exist_ok=True)
        if os.name=='posix': os.chmod(root,0o700)
        path=root/('schema-'+uuid.uuid4().hex+'.sqlite')
        store.snapshot(path)
        digest=hashlib.sha256(path.read_bytes()).hexdigest()
        return {'backup':path.name,'sha256':digest,'site_id':store.site,'encrypted':True}
    if op=='transaction_v1': return transaction_v1(store,request)
    if op=='scan_v1': return scan_v1(store,request)
    if op=='event_status_v1':
        identity(request['event_id'])
        with store.connection() as db:
            row=db.execute('SELECT acked FROM events WHERE id=?',(request['event_id'],)).fetchone()
        return {'local':'committed' if row else 'unknown','remote':'acknowledged' if row and row['acked'] else 'pending' if row else 'unknown'}
    return original(store,request)

def install(dataio):
    original=dataio.dispatch
    dataio.dispatch=lambda store,request:dispatch(store,request,original)