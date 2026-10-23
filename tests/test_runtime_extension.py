import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch
ROOT=Path(__file__).resolve().parents[1]
sys.path[:0]=[str(ROOT/'vendor'/'culinaryos'),str(ROOT/'runtime')]
from securedata import Keyring,canonical
from store import Store,VersionConflict,EventConflict,StorageFull
from syncworker import deliver
import dataio,mesanexo_ipc

class RuntimeTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.path=Path(self.temp.name)/'store.sqlite';self.keys=Keyring(Keyring.generate());self.store=Store(self.path,'test-site',self.keys,min_free_bytes=0)
        self.config=Path(self.temp.name)/'branch.json';patcher=patch.object(mesanexo_ipc,'BRANCH_CONFIG',self.config);patcher.start();self.addCleanup(patcher.stop)
    def tearDown(self):self.temp.cleanup()
    def transaction(self,event='e1',expected=0,fingerprint='request1',changes=None):
        return {'protocol':1,'event_id':event,'receipt':{'actor':'user','fingerprint':fingerprint,'result':{'payment':'p1'}},'changes':changes or [{'record_id':'mn_meta_site','expected_version':expected,'data':{'id':'site','siteId':'test-site','revision':expected+1}},{'record_id':'mn_payment_p1','expected_version':expected,'data':{'amount':12500}}]}
    def put(self,request):return mesanexo_ipc.transaction_v1(self.store,request)
    def sql(self,statement):
        db=sqlite3.connect(self.path)
        try:db.execute(statement);db.commit()
        finally:db.close()
    def test_scan_includes_z_types(self):
        request=self.transaction(changes=[{'record_id':'mn_meta_site','expected_version':0,'data':{'kind':'meta','id':'site'}},{'record_id':'mn_zone_z1','expected_version':0,'data':{'kind':'zone','id':'z1'}}])
        self.put(request)
        page=mesanexo_ipc.dispatch(self.store,{'op':'scan_v1','protocol':1},dataio.dispatch)
        self.assertIn('mn_zone_z1',[x['record_id'] for x in page['records']])
    def test_service_controlled_encrypted_snapshot(self):
        self.put(self.transaction())
        result=mesanexo_ipc.dispatch(self.store,{'op':'snapshot_v1','protocol':1,'destination':'outside-is-ignored'},dataio.dispatch)
        path=self.path.parent/'schema-backups'/result['backup']
        self.assertTrue(result['encrypted'])
        self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(),result['sha256'])
        copy=Store(path,'test-site',self.keys,min_free_bytes=0)
        self.assertIsNotNone(copy.get('mn_payment_p1'))

    def test_crypto_official_vector(self):
        v=json.loads((ROOT/'tests'/'vectors'/'crypto-v1.json').read_text());ring=Keyring({'active':'vector-v1','keys':{'vector-v1':v['key_b64']}})
        self.assertEqual(ring.open(v['envelope'],v['context']),json.loads(v['plaintext_utf8']))
        self.assertEqual(canonical({'header':{k:v['envelope'][k] for k in ('v','alg','kid')},'context':v['context']}).decode(),v['aad_utf8'])
        with self.assertRaises(Exception):ring.open(v['envelope'],{**v['context'],'site_id':'other'})
    def test_transaction_durable_atomic_and_outbox(self):
        self.put(self.transaction());self.store=Store(self.path,'test-site',self.keys,min_free_bytes=0)
        self.assertEqual(self.store.get('mn_payment_p1')['data']['amount'],12500)
        self.assertEqual(self.store.status()['pending'],1)
        self.assertEqual(self.store.status()['synchronous'],'FULL')
        self.assertEqual(len(self.store.pending()[0][0]['data']['changes']),2)
    def test_lost_response_then_restart_no_duplicate(self):
        request=self.transaction();self.put(request);self.store=Store(self.path,'test-site',self.keys,min_free_bytes=0)
        retry=self.put(request);self.assertTrue(retry['duplicate']);self.assertEqual(self.store.status()['events'],1)
        self.assertEqual(self.store.get('mn_payment_p1')['version'],1)
    def test_operation_id_reuse_rejected(self):
        self.put(self.transaction())
        with self.assertRaises(EventConflict):self.put(self.transaction(fingerprint='different'))
    def test_conflict_rolls_back_all_records(self):
        self.put(self.transaction())
        request=self.transaction('e2',1);request['changes'][1]['expected_version']=0
        with self.assertRaises(VersionConflict):self.put(request)
        self.assertEqual(self.store.get('mn_meta_site')['version'],1);self.assertEqual(self.store.status()['events'],1)
    def test_two_process_equivalent_connections_one_wins(self):
        def commit(n):
            other=Store(self.path,'test-site',self.keys,min_free_bytes=0)
            try:mesanexo_ipc.transaction_v1(other,self.transaction('e'+str(n)));return 'ok'
            except VersionConflict:return 'conflict'
        with concurrent.futures.ThreadPoolExecutor(2) as pool:results=list(pool.map(commit,[1,2]))
        self.assertCountEqual(results,['ok','conflict']);self.assertEqual(self.store.status()['events'],1)
    def test_full_disk_no_false_commit(self):
        with patch('mesanexo_ipc.shutil.disk_usage',return_value=type('Disk',(),{'free':-1})()):
            with self.assertRaises(StorageFull):self.put(self.transaction())
        self.assertIsNone(self.store.get('mn_meta_site'));self.assertEqual(self.store.status()['events'],0)
    def test_permission_failure_no_commit(self):
        with patch.object(self.keys,'seal',side_effect=PermissionError('denied')):
            with self.assertRaises(PermissionError):self.put(self.transaction())
        self.assertIsNone(self.store.get('mn_meta_site'));self.assertEqual(self.store.status()['events'],0)
    def test_too_large_transaction_rejected_before_write(self):
        req=self.transaction();req['changes'][1]['data']['blob']='x'*60000
        with self.assertRaises(ValueError):self.put(req)
        self.assertEqual(self.store.status()['events'],0)
    def test_original_get_put_compatible(self):
        dataio.dispatch(self.store,{'op':'put','record_id':'opaque','version':1,'expected_version':0,'data':{'test':True},'event_id':'original'})
        self.assertTrue(dataio.dispatch(self.store,{'op':'get','record_id':'opaque'})['data']['test'])
    def test_pages_bounded_and_revision_consistent(self):
        changes=[{'record_id':'mn_meta_site','expected_version':0,'data':{'id':'site'}}]
        changes.extend({'record_id':'mn_unit_'+str(n).zfill(3),'expected_version':0,'data':{'notes':'x'*100}} for n in range(60))
        self.put(self.transaction(changes=changes));first=mesanexo_ipc.scan_v1(self.store,{'protocol':1})
        self.assertEqual(len(first['records']),32);self.assertEqual(first['revision'],1)
        second=mesanexo_ipc.scan_v1(self.store,{'protocol':1,'after':first['after']});self.assertEqual(len(second['records']),29)
    def test_offline_central_preserves_local_operation(self):
        self.put(self.transaction())
        class Offline:
            def send(self,*args):raise OSError('central unavailable; not proof WAN is down')
        self.assertEqual(deliver(self.store,Offline())['state'],'pending');self.assertEqual(self.store.status()['pending'],1)
        self.assertEqual(self.store.get('mn_payment_p1')['data']['amount'],12500)
    def test_invalid_ack_does_not_remove_event(self):
        self.put(self.transaction())
        class Wrong:
            def send(self,*args):return {'status':'committed','site_id':'wrong'}
        self.assertEqual(deliver(self.store,Wrong())['reason'],'ack');self.assertEqual(self.store.status()['pending'],1)
    def test_site_isolation(self):
        self.put(self.transaction())
        with self.assertRaises(ValueError):Store(self.path,'another-site',self.keys,min_free_bytes=0)
    def test_receipt_and_event_status(self):
        self.put(self.transaction());r=mesanexo_ipc.dispatch(self.store,{'op':'event_status_v1','event_id':'e1'},dataio.dispatch)
        self.assertEqual(r,{'local':'committed','remote':'pending'})
        self.assertEqual(self.store.get('tx_e1')['data']['result']['payment'],'p1')
    def test_snapshot_restore_preserves_receipt_and_outbox(self):
        self.put(self.transaction());snapshot=Path(self.temp.name)/'snapshot.sqlite';self.store.snapshot(snapshot)
        self.put(self.transaction('e2',1,'second'))
        restored=Store(snapshot,'test-site',self.keys,min_free_bytes=0)
        self.assertEqual(restored.status()['events'],1)
        self.assertEqual(restored.get('tx_e1')['data']['result']['payment'],'p1')
        self.assertEqual(restored.get('mn_payment_p1')['version'],1)
    def test_wrong_key_cannot_restore_snapshot(self):
        self.put(self.transaction());snapshot=Path(self.temp.name)/'snapshot.sqlite';self.store.snapshot(snapshot)
        with self.assertRaises(Exception):Store(snapshot,'test-site',Keyring(Keyring.generate()),min_free_bytes=0)
    def test_scan_excludes_kinds_and_grows_pages(self):
        changes=[{'record_id':'mn_meta_site','expected_version':0,'data':{'id':'site'}}]
        changes.extend({'record_id':kind+str(n).zfill(3),'expected_version':0,'data':{'n':n}} for kind in ('mn_audit_','mn_unit_','mn_zone_') for n in range(40))
        self.put(self.transaction(changes=changes));seen=[];after=''
        while True:
            page=mesanexo_ipc.scan_v1(self.store,{'protocol':1,'after':after,'exclude':['mn_audit_'],'max_records':7})
            self.assertLessEqual(len(page['records']),7);seen+=[x['record_id'] for x in page['records']];after=page['after']
            if not after:break
        self.assertEqual(seen,sorted(seen));self.assertEqual(len(seen),81);self.assertFalse(any(x.startswith('mn_audit_') for x in seen))
        self.assertEqual(len(mesanexo_ipc.scan_v1(self.store,{'protocol':1,'max_records':500,'max_bytes':1024*1024})['records']),121)
        for bad in ({'exclude':['mn_']},{'exclude':'mn_audit_'},{'max_bytes':4*1024*1024},{'max_records':0}):
            with self.assertRaises(ValueError):mesanexo_ipc.scan_v1(self.store,{'protocol':1,**bad})
    def test_recent_returns_newest_of_a_kind_in_pages(self):
        for n in range(30):
            changes=[{'record_id':'mn_meta_site','expected_version':n,'data':{'id':'site'}},{'record_id':'mn_audit_'+format((n*7919)%1000,'03x')+str(n),'expected_version':0,'data':{'sequence':n+1}}]
            if n%3==0:changes.append({'record_id':'mn_unit_'+str(n),'expected_version':0,'data':{'n':n}})
            self.put(self.transaction('e'+str(n),changes=changes))
        got=[];before=None
        while True:
            page=mesanexo_ipc.recent_v1(self.store,{'protocol':1,'prefix':'mn_audit_','limit':12-len(got),'before':before,'max_records':5})
            self.assertEqual(page['revision'],30);got+=[x['data']['sequence'] for x in page['records']];before=page['before']
            if not before:break
        self.assertEqual(got,list(range(30,18,-1)))
        with self.assertRaises(ValueError):mesanexo_ipc.recent_v1(self.store,{'protocol':1,'prefix':'mn_','limit':5})
    def test_acknowledged_events_compacted_unacknowledged_kept(self):
        self.put(self.transaction())
        class Central:
            def send(self,event,digest):return {'status':'committed','site_id':'test-site','event_id':event['event_id'],'sha256':digest}
        self.put(self.transaction('e2',1,'second'))
        self.assertEqual(deliver(self.store,Central(),now=0)['sent'],2)
        self.sql("UPDATE events SET acked=1.0 WHERE id='e1'")
        self.put(self.transaction('e3',2,'third'))
        self.assertEqual(self.store.status()['events'],2)
        status=lambda e:mesanexo_ipc.dispatch(self.store,{'op':'event_status_v1','event_id':e},dataio.dispatch)
        self.assertEqual(status('e1'),{'local':'committed','remote':'acknowledged'})
        self.assertEqual(status('e2'),{'local':'committed','remote':'acknowledged'})
        self.assertEqual(status('e3'),{'local':'committed','remote':'pending'})
        self.assertEqual(status('never'),{'local':'unknown','remote':'unknown'})
        self.assertTrue(self.put(self.transaction())['duplicate'])
    def test_old_unacknowledged_events_never_compacted(self):
        self.put(self.transaction())
        self.sql('UPDATE events SET created=1.0')
        self.put(self.transaction('e2',1,'second'))
        out=mesanexo_ipc.dispatch(self.store,{'op':'outbox_status_v1'},dataio.dispatch)
        self.assertEqual(out['pending'],2);self.assertEqual(out['oldest_pending'],1.0);self.assertEqual(len(self.store.pending()),2)
    def branch(self,central):self.config.write_text(json.dumps({'version':1,'site_id':'test-site',**({'central':{'url':'https://central.example/v1'}} if central else {})}))
    def maintain(self):return mesanexo_ipc.dispatch(self.store,{'op':'outbox_maintain_v1','protocol':1},dataio.dispatch)
    def test_outbox_upkeep_waits_for_service_configuration(self):
        self.put(self.transaction());self.sql('UPDATE events SET created=1.0')
        self.assertEqual(self.maintain(),{'mode':'unknown','more':False})
        self.config.write_text('{broken');self.assertEqual(self.maintain()['mode'],'unknown');self.assertEqual(self.store.status()['events'],1)
    def test_without_central_old_pending_events_dropped_recent_kept(self):
        self.put(self.transaction());self.put(self.transaction('e2',1,'second'));self.sql("UPDATE events SET created=1.0 WHERE id='e1'")
        self.branch(False);self.assertEqual(self.maintain(),{'mode':'standalone','dropped':1,'more':False})
        self.assertEqual([w['event_id'] for w,_ in self.store.pending()],['e2'])
        self.assertEqual(self.maintain()['dropped'],0);self.assertTrue(self.put(self.transaction())['duplicate'])
        # Dropped without any central acknowledging it: the receipt stays, but nothing claims it was delivered.
        self.assertEqual(mesanexo_ipc.dispatch(self.store,{'op':'event_status_v1','event_id':'e1'},dataio.dispatch),{'local':'committed','remote':'untracked'})
    def test_complete_outbox_needs_no_baseline(self):
        self.put(self.transaction());self.branch(True)
        self.assertEqual(self.maintain(),{'mode':'complete','more':False});self.assertEqual(self.store.status()['events'],1)
    def test_connecting_central_queues_baseline_that_rebuilds_the_mirror(self):
        big='x'*3000
        changes=[{'record_id':'mn_meta_site','expected_version':0,'data':{'id':'site','revision':1}}]
        changes+=[{'record_id':'mn_unit_'+str(n).zfill(3),'expected_version':0,'data':{'n':n,'notes':big}} for n in range(15)]
        self.put(self.transaction(changes=changes))
        for n in range(1,4):self.put(self.transaction('u'+str(n),changes=[{'record_id':'mn_meta_site','expected_version':n,'data':{'id':'site','revision':n+1}},{'record_id':'mn_audit_'+str(n),'expected_version':0,'data':{'sequence':n}}]))
        self.sql("UPDATE events SET created=1.0 WHERE id!='u3'")
        self.branch(False);self.assertEqual(self.maintain()['dropped'],3)
        self.put(self.transaction('u4',changes=[{'record_id':'mn_meta_site','expected_version':4,'data':{'id':'site','revision':5}},{'record_id':'mn_unit_000','expected_version':1,'data':{'n':0,'notes':'changed'}}]))
        self.branch(True);steps=[]
        with patch.object(mesanexo_ipc,'BASELINE_EVENTS',1):
            while True:
                steps.append(self.maintain())
                if not steps[-1]['more']:break
        self.assertEqual(steps[-1]['mode'],'complete');self.assertGreater(len(steps),1)
        self.assertEqual(self.maintain(),{'mode':'complete','more':False})
        mirror={};order=[]
        class Central:
            def send(self,event,digest):
                self.check(event,digest);order.append(event['data']['contract'])
                for c in event['data']['changes']:
                    if c['expected_version']+1>mirror.get(c['record_id'],(0,))[0]:mirror[c['record_id']]=(c['expected_version']+1,c['data'])
                return {'status':'committed','site_id':'test-site','event_id':event['event_id'],'sha256':digest}
            def check(self,event,digest):
                assert len(canonical(event))<=mesanexo_ipc.MAX_TRANSACTION and hashlib.sha256(canonical(event)).hexdigest()==digest
        central=Central()
        while deliver(self.store,central)['state']=='delivered':pass
        self.assertEqual(order[:2],['mesa-nexo-transaction-v1']*2);self.assertTrue(all(x=='mesa-nexo-baseline-v1' for x in order[2:]))
        stored={r['record_id']:(r['version'],r['data']) for page in [mesanexo_ipc.scan_v1(self.store,{'protocol':1,'max_records':500,'max_bytes':1024*1024})] for r in page['records']}
        self.assertEqual(mirror,stored);self.assertEqual(self.store.status()['pending'],0)
if __name__=='__main__':
    result=unittest.TextTestRunner(stream=sys.stdout,verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(RuntimeTests))
    report={'suite':'CulinaryOS runtime extension','tests':result.testsRun,'failures':len(result.failures),'errors':len(result.errors),'successful':result.wasSuccessful(),'platform':sys.platform,'fault_injection':['disk reserve failure','permission failure'],'not_executed':['POSIX Unix socket and systemd on Windows']}
    (ROOT/'evidence'/'runtime.json').write_text(json.dumps(report,indent=2))
    raise SystemExit(0 if result.wasSuccessful() else 1)