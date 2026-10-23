"""TEST ONLY: stdin/stdout framing over a real CulinaryOS SQLite encrypted store.
Windows embedded Python lacks AF_UNIX. Production uses the original Unix service.
No TCP server, no demo accounts, no fake durability or sync ACK.
"""
import argparse
import json
import sys
from pathlib import Path
root=Path(__file__).resolve().parents[1]
sys.path[:0]=[str(root/'vendor'/'culinaryos'),str(root/'runtime')]
from securedata import Keyring,canonical
from store import Store,VersionConflict,EventConflict,StorageFull
import dataio,mesanexo_ipc
p=argparse.ArgumentParser();p.add_argument('--directory',required=True);p.add_argument('--site',required=True)
a=p.parse_args();folder=Path(a.directory);folder.mkdir(parents=True,exist_ok=True)
k=folder/'test-only-keyring.json'
if not k.exists():
    with k.open('x',encoding='utf-8') as f: json.dump(Keyring.generate(),f)
store=Store(folder/'store.sqlite',a.site,Keyring.load(k),min_free_bytes=1024*1024)
# The lab branch has no central unless its own branch.json names one.
mesanexo_ipc.BRANCH_CONFIG=folder/'branch.json'
if not mesanexo_ipc.BRANCH_CONFIG.exists():
    with mesanexo_ipc.BRANCH_CONFIG.open('x',encoding='utf-8') as f: json.dump({'version':1,'site_id':a.site},f)
mesanexo_ipc.install(dataio)
for raw in sys.stdin.buffer:
    try:
        if len(raw)>dataio.LIMIT or not raw.endswith(b'\n'):raise ValueError()
        result=dataio.dispatch(store,json.loads(raw));reply={'ok':True,'result':result}
    except Exception as e:
        code='CONFLICT' if isinstance(e,VersionConflict) else 'IDEMPOTENCY' if isinstance(e,EventConflict) else 'FULL' if isinstance(e,StorageFull) else 'INVALID' if isinstance(e,(ValueError,KeyError,TypeError)) else 'UNAVAILABLE'
        reply={'ok':False,'error':'COS-DATA-'+code}
    sys.stdout.buffer.write(canonical(reply)+b'\n');sys.stdout.buffer.flush()