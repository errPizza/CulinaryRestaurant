"""Explicit export from a COPY only. Never opens original prototype/data.
Produces an offline import plan, consistent backup and report in a new directory.
No sessions, device secrets, demo passwords, master keys or provider access imported.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import sys

ORIGINAL=Path(r'C:\Users\kikin\Desktop\SoftwarePrototipo\data')
COLLECTIONS={'tables':'table','products':'product','categories':'category','users':'user','visits':'visit','commands':'command','units':'unit','notifications':'notification','requests':'request','payments':'payment','reservations':'reservation','reviews':'review','cash':'cash','movements':'movement','promotions':'promotion','audit':'audit','tickets':'ticket','grants':'grant'}

def validate(s,rid,bid):
    if s.get('schema')!=1 or any(not isinstance(s.get(k),list) for k in COLLECTIONS):raise ValueError('Unsupported prototype schema')
    restaurants=[x for x in s['restaurants'] if x['id']==rid]
    if len(restaurants)!=1:raise ValueError('Select exactly one existing restaurant')
    branches=[x for x in s['branches'] if x['restaurantId']==rid]
    if bid not in [x['id'] for x in branches]:raise ValueError('Branch not found')
    tables={x['id'] for x in s['tables'] if x.get('branchId')==bid}
    visits={x['id'] for x in s['visits'] if x.get('tableId') in tables}
    cash={x['id'] for x in s['cash'] if x.get('branchId')==bid}
    result={}
    for collection,kind in COLLECTIONS.items():
        rows=[dict(x) for x in s[collection] if x.get('restaurantId')==rid]
        if kind=='table':rows=[x for x in rows if x['id'] in tables]
        if kind=='cash':rows=[x for x in rows if x['id'] in cash]
        if kind in ('visit','unit','notification','request','payment','review'):rows=[x for x in rows if (x['id'] if kind=='visit' else x.get('visitId')) in visits]
        if kind=='command':rows=[x for x in rows if x.get('visitId') in visits]
        if kind=='movement':rows=[x for x in rows if x.get('cashId') in cash]
        if kind=='reservation':rows=[x for x in rows if not x.get('tableId') or x['tableId'] in tables]
        for row in rows:
            if not re.fullmatch(r'[A-Za-z0-9_-]{1,60}',row['id']):raise ValueError('Identifier exceeds native bound')
            row.pop('restaurantId',None);row.pop('branchId',None)
            if kind in ('notification','request','movement'):row['type']=row.pop('kind','')
            row['kind']=kind
            if kind=='user':
                row.pop('password',None);row['active']=False
            if kind=='grant':row['revoked']=True;row['simulation']=True
            if kind=='product' and 'supportsCooking' not in row:row['supportsCooking']=row.get('id') in ('r1-p1','r2-p1') and row.get('name')=='Hamburguesa de la casa'
            if kind=='visit' and not row.get('trackingKey'):row['trackingKey']=os.urandom(32).hex()
            if kind=='command':
                valid={x['id'] for x in s['units'] if x.get('visitId') in visits}
                row['unitIds']=[uid for uid in row.get('unitIds',[]) if uid in valid]
            if kind=='payment':row['externalProcessed']=False
            for key in ('price','basePrice','amount','tip','initial','counted','expected','difference'):
                if key in row and (type(row[key]) is not int or (row[key]<0 and key!='difference')):raise ValueError('Invalid minor-unit amount')
            if len(json.dumps(row,ensure_ascii=False).encode())>40*1024:raise ValueError('Record exceeds bound; externalize product artwork before migrating')
        result[kind]=rows
    units=result['unit'];payments=result['payment']
    for v in result['visit']:
        gross=sum(x['price'] for x in units if x['visitId']==v['id'] and x['status'] not in ('cancelled','unavailable'))
        d=v.get('discount') or {};discount=int(gross*d.get('value',0)/100+0.5) if d.get('kind')=='percent' else min(gross,d.get('value',0))
        paid=sum(x['amount'] for x in payments if x['visitId']==v['id'])
        if paid>gross-discount:raise ValueError('Negative balance in source account')
    known_units={u['id'] for u in units}
    if any(uid not in known_units for c in result['command'] for uid in c['unitIds']):raise ValueError('Dangling command unit')
    return restaurants[0],result

def export(source,output,rid,bid,site):
    source=Path(source).resolve();output=Path(output).resolve()
    if source==ORIGINAL.resolve() or ORIGINAL.resolve() in source.parents:raise ValueError('Original data folder forbidden; supply an explicit copy')
    if output.exists():raise FileExistsError('Output must be a new directory')
    output.mkdir(parents=True)
    # Read-only connection; SQLite backup includes a consistent WAL snapshot of the supplied copy.
    if source.suffix in ('.sqlite','.db'):
        uri=source.as_uri()+'?mode=ro'
        with sqlite3.connect(uri,uri=True) as db:
            if db.execute('PRAGMA quick_check').fetchone()[0]!='ok':raise ValueError('Source database integrity failed')
            backup=output/'source-copy.sqlite'
            with sqlite3.connect(backup) as destination:db.backup(destination)
            row=db.execute('SELECT value FROM state WHERE id=1').fetchone()
            if not row:raise ValueError('Missing state')
            state=json.loads(row[0])
    else:
        raw=source.read_bytes();state=json.loads(raw)
        if state.get('format')=='mesa-nexo-aes-256-gcm':raise ValueError('Encrypted legacy backup requires original key and decrypt-backup command; not CulinaryOS crypto-v1')
        (output/'source-copy.json').write_bytes(raw)
    restaurant,records=validate(state,rid,bid)
    plan={'format':'mesa-nexo-import-v1','siteId':site,'sourceSha256':hashlib.sha256(source.read_bytes()).hexdigest(),'restaurantId':rid,'branchId':bid,'records':[r for rows in records.values() for r in rows]}
    body=json.dumps(plan,ensure_ascii=False,indent=2);(output/'import-plan.json').write_text(body,encoding='utf-8')
    report={'format':'mesa-nexo-migration-report-v1','validated':True,'imported':False,'sourceSha256':plan['sourceSha256'],'planSha256':hashlib.sha256(body.encode()).hexdigest(),'counts':{k:len(v) for k,v in records.items()},'warnings':['All legacy users disabled; passwords excluded. Set new passwords explicitly.','Sessions and installation credentials excluded.','No data written to branch runtime. Run offline import during maintenance.','Each branch requires a separate server/site identity.','Historical imports enqueue events; coordinate initial sync volume.']}
    (output/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8');return report

def main():
    p=argparse.ArgumentParser();p.add_argument('--source-copy',required=True);p.add_argument('--output',required=True);p.add_argument('--restaurant-id',required=True);p.add_argument('--branch-id',required=True);p.add_argument('--site-id',required=True);a=p.parse_args()
    try:report=export(a.source_copy,a.output,a.restaurant_id,a.branch_id,a.site_id);print(json.dumps({'validated':report['validated'],'imported':False,'counts':report['counts']}))
    except Exception as error:print(str(error),file=sys.stderr);return 1
    return 0
if __name__=='__main__':raise SystemExit(main())