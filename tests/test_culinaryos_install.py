import hashlib, importlib.util, json, os, ssl, struct, sys, tempfile, unittest
from pathlib import Path
from unittest.mock import patch
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('culinary_install',ROOT/'deploy/culinaryos/install.py')
i=importlib.util.module_from_spec(spec);spec.loader.exec_module(i)
class CulinaryInstallation(unittest.TestCase):
    def site(self,**changes):
        site={'site_id':'local-01','server_name':'culinaryos.internal','server_address':'10.77.0.10','network':'10.77.0.0/24','role':'servidor'};site.update(changes);return site
    def test_valid_site_preserves_identity(self):self.assertEqual(i.validate_site(self.site())['site_id'],'local-01')
    def test_bad_site_ids_rejected(self):
        for value in ('../other','x\ncommand','A','', 'a'*33):
            with self.subTest(value=value),self.assertRaises(ValueError):i.validate_site(self.site(site_id=value))
    def test_invalid_tls_name_rejected(self):
        for value in ('host;id','https://host','HOST','-host','host.','x\ncommand',''):
            with self.subTest(value=value),self.assertRaises(ValueError):i.validate_site(self.site(server_name=value))
    def test_wrong_subnet_rejected(self):
        with self.assertRaises(ValueError):i.validate_site(self.site(server_address='192.168.1.10'))
    def test_terminal_not_mistaken_for_server(self):
        with self.assertRaises(ValueError):i.validate_site(self.site(role='meseros'))
    def runtime(self,root,hook=False,extension=False):
        runtime=root/'runtime';runtime.mkdir();vendor=root/'bundle/vendor/culinaryos';vendor.mkdir(parents=True)
        hashes=[]
        for name in ('dataio.py','securedata.py','store.py','syncworker.py'):
            data=b'baseline\n'+(b'if __name__=="__main__":main()\n' if name=='dataio.py' else b'')
            hashes.append({'file':name,'sha256':hashlib.sha256(data).hexdigest()})
            if hook and name=='dataio.py':data=data.replace(b'if __name__',i.HOOK.encode()+b'if __name__')
            (runtime/name).write_bytes(data)
        (vendor/'source-hashes.json').write_text(json.dumps(hashes))
        extension_dir=root/'bundle/runtime';extension_dir.mkdir()
        (extension_dir/'mesanexo_ipc.py').write_text('extension')
        if extension:(runtime/'mesanexo_ipc.py').write_text('extension')
        return root/'bundle',runtime
    def test_exact_baseline_accepted(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,runtime=self.runtime(Path(tmp));self.assertEqual(i.validate_runtime(bundle,runtime),'absent')
    def test_existing_exact_extension_accepted(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,runtime=self.runtime(Path(tmp),True,True);self.assertEqual(i.validate_runtime(bundle,runtime),'current')
    def test_modified_runtime_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,runtime=self.runtime(Path(tmp));(runtime/'store.py').write_text('changed')
            with self.assertRaises(ValueError):i.validate_runtime(bundle,runtime)
    def test_missing_extension_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,runtime=self.runtime(Path(tmp),True)
            with self.assertRaises(ValueError):i.validate_runtime(bundle,runtime)
    def test_unknown_extension_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,runtime=self.runtime(Path(tmp),True,True);(runtime/'mesanexo_ipc.py').write_text('other')
            with self.assertRaises(ValueError):i.validate_runtime(bundle,runtime)
    def test_incomplete_hook_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,runtime=self.runtime(Path(tmp),False,True)
            with self.assertRaises(ValueError):i.validate_runtime(bundle,runtime)
    def test_linux_elf_x64_required(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'app';data=bytearray(64);data[:6]=b'\x7fELF\x02\x01';struct.pack_into('<H',data,18,62);path.write_bytes(data)
            self.assertEqual(i.check_elf(path),data)
            struct.pack_into('<H',data,18,183);path.write_bytes(data)
            with self.assertRaises(ValueError):i.check_elf(path)
            path.write_bytes(b'MZ'+bytes(100))
            with self.assertRaises(ValueError):i.check_elf(path)
    def test_new_files_not_overwritten(self):
        with tempfile.TemporaryDirectory() as tmp,patch.object(os,'chown',create=True):
            path=Path(tmp)/'config';i.write_new(path,b'existing');i.write_new(path,b'existing')
            with self.assertRaises(FileExistsError):i.write_new(path,b'new')
            self.assertEqual(path.read_bytes(),b'existing')
    def test_install_rejects_windows_before_mutations(self):
        with patch.object(i.sys,'platform','win32'),self.assertRaises(PermissionError):i.preflight(ROOT)
    def test_public_profile_has_exact_ca_hash_and_no_secrets(self):
        import base64
        der=b'public certificate fixture'
        with tempfile.TemporaryDirectory() as tmp:
            ca=Path(tmp)/'ca.crt';ca.write_text('-----BEGIN CERTIFICATE-----\n'+base64.b64encode(der).decode()+'\n-----END CERTIFICATE-----\n')
            result=i.connection(self.site(),ca)
            self.assertEqual(result['CaSha256'],hashlib.sha256(der).hexdigest());self.assertEqual(result['ConnectAddress'],'10.77.0.10')
            self.assertEqual(result['ServerUrl'],'https://culinaryos.internal:8443')
            self.assertEqual(set(result),{'ServerUrl','SiteId','CaFile','CaSha256','ConnectAddress','TenantId','BranchId'})
    def test_bundle_ca_not_accepted(self):
        with tempfile.TemporaryDirectory() as tmp:
            ca=Path(tmp)/'ca.crt';ca.write_text('-----BEGIN CERTIFICATE-----\n-----BEGIN CERTIFICATE-----')
            with self.assertRaises(ValueError):i.connection(self.site(),ca)
    def test_real_certificate_and_profile_match(self):
        openssl=Path('C:/Program Files/Git/usr/bin/openssl.exe') if os.name=='nt' else Path('/usr/bin/openssl')
        original=i.run
        def command(*args,**kwargs):return original(str(openssl) if args[0]=='openssl' else args[0],*args[1:],**kwargs)
        with tempfile.TemporaryDirectory() as tmp,patch.object(i,'CONFIG',Path(tmp)),patch.object(i,'run',side_effect=command),patch.object(os,'chown',create=True):
            ca,cert,key=i.certificates(self.site(),tmp)
            self.assertTrue(key.is_file());profile=i.connection(self.site(),ca)
            self.assertEqual(profile['CaSha256'],hashlib.sha256(ssl.PEM_cert_to_DER_cert(ca.read_text())).hexdigest())
            before=[i.digest(p) for p in (ca,cert,key)]
            i.certificates(self.site(),tmp);self.assertEqual(before,[i.digest(p) for p in (ca,cert,key)])
            cert.unlink()
            with self.assertRaises(ValueError):i.certificates(self.site(),tmp)
    def historical_runtime(self,root):
        bundle,runtime=self.runtime(root)
        archived=ROOT/'vendor/culinaryos/compat/store-0.2.0-installed.py'
        (runtime/'store.py').write_bytes(archived.read_bytes())
        path=bundle/'vendor/culinaryos/source-hashes.json';rows=json.loads(path.read_text())
        audited=next(row for row in json.loads((ROOT/'vendor/culinaryos/source-hashes.json').read_text()) if row['file']=='store.py')
        for row in rows:
            if row['file']=='store.py':row.update(audited)
        path.write_text(json.dumps(rows))
        return bundle,runtime
    def test_archived_store_diff_is_only_the_reviewed_comment(self):
        import ast
        older=(ROOT/'vendor/culinaryos/compat/store-0.2.0-installed.py').read_bytes()
        current=(ROOT/'vendor/culinaryos/store.py').read_bytes()
        self.assertEqual(hashlib.sha256(older).hexdigest(),'10a1e6e100312a17662d6f2eaf7e3d0dc8554d661dd58c8fedbd4cbc5a667213')
        self.assertEqual(older.replace(b'            # FIFO estricto: un evento pendiente bloquea posteriores hasta su ACK.\n',b''),current)
        self.assertEqual(ast.dump(ast.parse(older)),ast.dump(ast.parse(current)))
    def test_known_historical_store_accepted_and_preserved(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,runtime=self.historical_runtime(Path(tmp));before=(runtime/'store.py').read_bytes()
            i.validate_runtime(bundle,runtime)
            self.assertEqual(before,(runtime/'store.py').read_bytes())
    def test_unlisted_comment_change_still_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,runtime=self.historical_runtime(Path(tmp))
            with (runtime/'store.py').open('ab') as stream:stream.write(b'\n# not audited\n')
            with self.assertRaises(ValueError):i.validate_runtime(bundle,runtime)
    def extension_precheck(self,bundle,runtime,upgrade=False):
        spec=importlib.util.spec_from_file_location('extension_install',ROOT/'deploy/install-runtime-extension.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        class StopBeforeMutation(Exception):pass
        with patch.object(module,'ROOT',bundle),patch.object(module,'TARGET',runtime),patch.object(module.sys,'platform','linux'),patch.object(module.os,'geteuid',return_value=0,create=True),patch.object(module,'command',side_effect=StopBeforeMutation) as command:
            try:module.main(upgrade)
            except StopBeforeMutation:pass
            return command.call_args_list
    def test_extension_installer_accepts_same_audited_store(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,runtime=self.historical_runtime(Path(tmp));calls=self.extension_precheck(bundle,runtime)
            self.assertEqual(calls[0].args,('systemctl','start','culinaryos-snapshot.service'))
            self.assertFalse((runtime/'mesanexo_ipc.py').exists())
    def test_extension_installer_stops_unknown_store_before_snapshot(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,runtime=self.historical_runtime(Path(tmp));(runtime/'store.py').write_text('different')
            with self.assertRaises(SystemExit):self.extension_precheck(bundle,runtime)
    def test_backup_shared_parent_directories(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp)/'guest';backup=Path(tmp)/'backup'
            files={'etc/xdg/autostart/culinaryos.desktop':b'original startup','etc/culinaryos/site.json':b'original site','etc/nftables.conf':b'original firewall'}
            for name,data in files.items():
                p=root/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(data)
            i.backup_configuration(backup,root)
            for name,data in files.items():
                self.assertEqual((backup/name).read_bytes(),data)
                self.assertEqual((root/name).read_bytes(),data)
            with self.assertRaises(FileExistsError):i.backup_configuration(backup,root)
    def compiled_fixture(self,root,tag='ABCDEF'):
        bundle=root/'bundle';expected=bundle/'client-source'
        for name,data in {'src-tauri/Cargo.lock':b'locked source','src-tauri/src/main.rs':b'fixed code','dist/index.html':b'packaged UI'}.items():
            p=expected/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(data)
        payload=bundle/'payload/bridge/culinary-bridge';payload.parent.mkdir(parents=True);payload.write_bytes(b'bridge')
        build_root=root/'build';work=build_root/('culinary-restaurant-build.'+tag)
        import shutil
        shutil.copytree(expected,work/'client-source')
        bridge=work/'client-source/src-tauri/binaries/culinary-bridge-x86_64-unknown-linux-gnu';bridge.parent.mkdir();bridge.write_bytes(b'bridge')
        binary=work/'client-source/src-tauri/target/release/culinary-restaurant';binary.parent.mkdir(parents=True)
        data=bytearray(64);data[:6]=b'\x7fELF\x02\x01';struct.pack_into('<H',data,18,62);binary.write_bytes(data)
        return bundle,build_root,work,binary
    def test_resume_reuses_matching_client_without_running_compiler(self):
        with tempfile.TemporaryDirectory() as tmp,patch.object(i,'run') as command:
            bundle,build_root,work,binary=self.compiled_fixture(Path(tmp))
            before=binary.read_bytes()
            self.assertEqual(i.find_compiled_client(bundle,build_root,work.stat().st_uid),binary)
            command.assert_not_called();self.assertEqual(binary.read_bytes(),before)
    def test_resume_rejects_other_owner(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,build_root,work,binary=self.compiled_fixture(Path(tmp))
            with self.assertRaises(ValueError):i.find_compiled_client(bundle,build_root,work.stat().st_uid+1)
    def test_resume_rejects_changed_source(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,build_root,work,binary=self.compiled_fixture(Path(tmp));(work/'client-source/src-tauri/src/main.rs').write_bytes(b'changed')
            with self.assertRaises(ValueError):i.find_compiled_client(bundle,build_root,work.stat().st_uid)
    def test_resume_rejects_changed_packaged_interface(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,build_root,work,binary=self.compiled_fixture(Path(tmp));(work/'client-source/dist/index.html').write_bytes(b'changed')
            with self.assertRaises(ValueError):i.find_compiled_client(bundle,build_root,work.stat().st_uid)
    def test_resume_rejects_changed_bridge(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,build_root,work,binary=self.compiled_fixture(Path(tmp));(work/'client-source/src-tauri/binaries/culinary-bridge-x86_64-unknown-linux-gnu').write_bytes(b'changed')
            with self.assertRaises(ValueError):i.find_compiled_client(bundle,build_root,work.stat().st_uid)
    def test_resume_rejects_non_linux_binary(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,build_root,work,binary=self.compiled_fixture(Path(tmp));binary.write_bytes(b'MZ'+bytes(100))
            with self.assertRaises(ValueError):i.find_compiled_client(bundle,build_root,work.stat().st_uid)
    def test_resume_missing_binary_does_not_compile(self):
        with tempfile.TemporaryDirectory() as tmp,patch.object(i,'run') as command:
            with self.assertRaises(ValueError):i.find_compiled_client(Path(tmp)/'bundle',Path(tmp),owner=1)
            command.assert_not_called()
    def application_fixture(self,root):
        app=root/'app'
        for folder in ('backend','client'):(app/folder).mkdir(parents=True)
        for name in ('backend/MesaNexo.Api','client/CulinaryRestaurant','client/culinary-restaurant','client/culinary-bridge'):(app/name).write_bytes(b'fixed application code')
        return app
    def test_code_permissions_explicit_even_with_private_umask(self):
        with tempfile.TemporaryDirectory() as tmp:
            app=self.application_fixture(Path(tmp));private=Path(tmp)/'private';private.mkdir();key=private/'key';key.write_bytes(b'private fixture')
            before=key.read_bytes()
            with patch.object(i,'APP',app),patch.object(i.os,'chmod') as chmod:
                i.set_application_permissions()
                expected={app,app/'backend',app/'client',app/'backend/MesaNexo.Api',app/'client/CulinaryRestaurant',app/'client/culinary-restaurant',app/'client/culinary-bridge'}
                self.assertEqual({call.args[0] for call in chmod.call_args_list},expected)
                self.assertTrue(all(call.args[1]==0o755 for call in chmod.call_args_list))
            self.assertEqual(key.read_bytes(),before)
    def test_code_permissions_refuse_incomplete_tree_before_chmod(self):
        with tempfile.TemporaryDirectory() as tmp:
            app=self.application_fixture(Path(tmp));(app/'client/culinary-bridge').unlink()
            with patch.object(i,'APP',app),patch.object(i.os,'chmod') as chmod,self.assertRaises(PermissionError):i.set_application_permissions()
            chmod.assert_not_called()
    def test_service_and_operator_access_checked_before_credentials(self):
        with tempfile.TemporaryDirectory() as tmp:
            app=self.application_fixture(Path(tmp))
            with patch.object(i,'APP',app),patch.object(i,'run') as command:
                i.verify_application_access()
                self.assertEqual(len(command.call_args_list),4)
                self.assertEqual(command.call_args_list[0].args,('runuser','-u','culinaryos-app','--','/usr/bin/test','-x',app/'backend/MesaNexo.Api'))
                for call in command.call_args_list[1:]:self.assertEqual(call.args[2],'operador')
            source=(ROOT/'deploy/culinaryos/install.py').read_text()
            install_source=source[source.index('def install(bundle,client):'):]
            self.assertLess(install_source.index('verify_application_access()'),install_source.index('initialize_branch(site)'))
    def upgradable(self,root):
        # A branch hooked to a delivered earlier extension, and a bundle with the current one and its history.
        bundle,runtime=self.runtime(root,True,True)
        (bundle/'runtime/mesanexo_ipc.py').write_bytes((ROOT/'runtime/mesanexo_ipc.py').read_bytes())
        (bundle/'deploy/culinaryos').mkdir(parents=True);(bundle/'deploy/culinaryos/extension-history.json').write_bytes((ROOT/'deploy/culinaryos/extension-history.json').read_bytes())
        legacy=(ROOT/'artifacts/culinaryos-cd-r3/runtime/mesanexo_ipc.py').read_bytes();(runtime/'mesanexo_ipc.py').write_bytes(legacy)
        return bundle,runtime,legacy
    def test_every_delivered_extension_is_known_for_upgrade(self):
        payload=(ROOT/'runtime/mesanexo_ipc.py').read_bytes()
        self.assertNotIn(b'\r\n',payload)
        known=i.known_extensions(ROOT)
        delivered=list((ROOT/'artifacts').glob('*/runtime/mesanexo_ipc.py'))
        self.assertTrue(delivered)
        for path in delivered:
            with self.subTest(release=path.parts[-3]):self.assertIn(i.digest(path),known)
        self.assertNotIn(hashlib.sha256(payload).hexdigest(),known)
    def test_known_earlier_extension_reported_for_upgrade_unchanged(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,runtime,legacy=self.upgradable(Path(tmp))
            self.assertEqual(i.validate_runtime(bundle,runtime),'upgrade')
            self.assertEqual((runtime/'mesanexo_ipc.py').read_bytes(),legacy)
            (bundle/'deploy/culinaryos/extension-history.json').unlink()
            with self.assertRaises(ValueError):i.validate_runtime(bundle,runtime)
    def test_changed_earlier_extension_not_upgraded(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,runtime,legacy=self.upgradable(Path(tmp))
            with (runtime/'mesanexo_ipc.py').open('ab') as f:f.write(b'\n# unknown change\n')
            with self.assertRaises(ValueError):i.validate_runtime(bundle,runtime)
            self.assertEqual(self.extension_precheck_error(bundle,runtime,True),'not a known earlier version')
    def test_extension_install_without_upgrade_keeps_existing(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle,runtime,legacy=self.upgradable(Path(tmp))
            self.assertEqual(self.extension_precheck_error(bundle,runtime,False),'use --upgrade')
    def extension_precheck_error(self,bundle,runtime,upgrade):
        with self.assertRaises(SystemExit) as stop:self.assertEqual(self.extension_precheck(bundle,runtime,upgrade),[])
        message=str(stop.exception);return next(x for x in ('not a known earlier version','use --upgrade') if x in message)
    def extension_upgrade(self,root,fail_health=False):
        import subprocess,types
        spec=importlib.util.spec_from_file_location('extension_upgrade',ROOT/'deploy/install-runtime-extension.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        bundle,runtime,legacy=self.upgradable(root);backups=root/'backups';backups.mkdir();calls=[]
        def command(*args,check=True):
            calls.append(args)
            if fail_health and args[-1]=='health':raise subprocess.CalledProcessError(1,args)
            return types.SimpleNamespace(returncode=0)
        dataio=(runtime/'dataio.py').read_bytes()
        with patch.object(module,'ROOT',bundle),patch.object(module,'TARGET',runtime),patch.object(module,'BACKUPS',backups),patch.object(module.sys,'platform','linux'),patch.object(module.os,'geteuid',return_value=0,create=True),patch.object(os,'chown',create=True),patch.object(module,'command',side_effect=command):
            try:module.main(True);error=None
            except subprocess.CalledProcessError as e:error=e
        self.assertEqual(calls[0],('systemctl','start','culinaryos-snapshot.service'))
        self.assertEqual((runtime/'dataio.py').read_bytes(),dataio)
        backup=next(backups.iterdir());self.assertEqual((backup/'mesanexo_ipc.py').read_bytes(),legacy)
        return runtime,legacy,error,calls
    def test_extension_upgrade_backs_up_and_replaces_known_version(self):
        with tempfile.TemporaryDirectory() as tmp:
            runtime,legacy,error,calls=self.extension_upgrade(Path(tmp))
            self.assertIsNone(error);self.assertEqual((runtime/'mesanexo_ipc.py').read_bytes(),(ROOT/'runtime/mesanexo_ipc.py').read_bytes())
            self.assertIn(('systemctl','stop','culinaryos-data.service'),calls);self.assertIn(('systemctl','start','culinaryos-data.service'),calls)
    def test_extension_upgrade_restores_previous_when_health_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            runtime,legacy,error,calls=self.extension_upgrade(Path(tmp),fail_health=True)
            self.assertIsNotNone(error);self.assertEqual((runtime/'mesanexo_ipc.py').read_bytes(),legacy)
            self.assertEqual(calls[-1],('systemctl','restart','culinaryos-data.service'))
    def test_extension_installer_preserves_lf_and_crlf_bytes(self):
        spec=importlib.util.spec_from_file_location('extension_install_bytes',ROOT/'deploy/install-runtime-extension.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as tmp,patch.object(os,'chown',create=True):
            path=Path(tmp)/'extension.py'
            for body in (b'# LF\npass\n',b'# CRLF\r\npass\r\n'):
                module.install_file(path,body)
                self.assertEqual(path.read_bytes(),body)
                self.assertFalse(path.with_name(path.name+'.mesanexo-new').exists())
            pending=path.with_name(path.name+'.mesanexo-new');pending.write_bytes(b'previous pending write')
            before=path.read_bytes()
            with self.assertRaises(FileExistsError):module.install_file(path,b'replacement')
            self.assertEqual(path.read_bytes(),before)
    def test_public_icon_directories_get_traversal_without_touching_parent(self):
        with tempfile.TemporaryDirectory() as tmp:
            base=Path(tmp)/'hicolor';base.mkdir()
            directory=base/'256x256/apps'
            with patch.object(i.os,'chmod') as chmod:
                self.assertEqual(i.set_public_icon_directory(directory),directory)
                self.assertEqual([call.args for call in chmod.call_args_list],[(directory.parent,0o755),(directory,0o755)])
    def test_no_os_login_policy_mutations(self):
        source=(ROOT/'deploy/culinaryos/install.py').read_text()
        self.assertNotIn('60-culinaryos-autologin.conf',source)
        self.assertNotIn('autologin-user=',source)
        self.assertNotIn("'init'",source)
    def test_existing_autostart_identifier_reused(self):
        desktop=(ROOT/'deploy/culinaryos/culinaryos.desktop').read_text()
        self.assertEqual(desktop.count('Exec='),1)
        self.assertIn('apphost.py session',desktop)
    def test_frontend_packaged_not_remote_browser(self):
        cfg=json.loads((ROOT/'src/MesaNexo.Web/src-tauri/tauri.conf.json').read_text())
        self.assertEqual(cfg['build']['frontendDist'],'../dist')
        linux=json.loads((ROOT/'src/MesaNexo.Web/src-tauri/tauri.linux.conf.json').read_text())
        self.assertTrue(linux['app']['windows'][0]['maximized'])
if __name__=='__main__':unittest.main(verbosity=2)
