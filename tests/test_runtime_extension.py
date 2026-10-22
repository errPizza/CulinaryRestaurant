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
    def tearDown(self):self.temp.cleanup()
    def transaction(self,event='e1',expected=0,fingerprint='request1',changes=None):
        return {'protocol':1,'event_id':event,'receipt':{'actor':'user','fingerprint':fingerprint,'result':{'payment':'p1'}},'changes':changes or [{'record_id':'mn_meta_site','expected_version':expected,'data':{'id':'site','siteId':'test-site','revision':expected+1}},{'record_id':'mn_payment_p1','expected_version':expected,'data':{'amount':12500}}]}
    def put(self,request):return mesanexo_ipc.transaction_v1(self.store,request)
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
if __name__=='__main__':
    result=unittest.TextTestRunner(stream=sys.stdout,verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(RuntimeTests))
    report={'suite':'CulinaryOS runtime extension','tests':result.testsRun,'failures':len(result.failures),'errors':len(result.errors),'successful':result.wasSuccessful(),'platform':sys.platform,'fault_injection':['disk reserve failure','permission failure'],'not_executed':['POSIX Unix socket and systemd on Windows']}
    (ROOT/'evidence'/'runtime.json').write_text(json.dumps(report,indent=2))
    raise SystemExit(0 if result.wasSuccessful() else 1)