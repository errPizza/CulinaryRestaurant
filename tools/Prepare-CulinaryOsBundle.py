"""Prepare a data CD tree; never copies production configuration or Windows executables."""
from pathlib import Path
import argparse,hashlib,json,shutil,sys
ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser();parser.add_argument('--revision',type=int,default=1);args=parser.parse_args()
if not 1<=args.revision<=99:raise SystemExit('Invalid revision')
tag='' if args.revision==1 else '-r'+str(args.revision)
OUT=ROOT/('artifacts/culinaryos-cd'+tag)
if OUT.exists():raise SystemExit('Existing staging tree; use a new path instead of replacing it')
OUT.mkdir(parents=True)
def copy(source,target):
    source=ROOT/source;target=OUT/target
    target.parent.mkdir(parents=True,exist_ok=True)
    if source.is_symlink():raise ValueError('No links allowed')
    shutil.copyfile(source,target)
def tree(source,target):
    for p in sorted((ROOT/source).rglob('*')):
        if p.is_symlink():raise ValueError('No links allowed')
        if p.is_file():copy(p.relative_to(ROOT),Path(target)/p.relative_to(ROOT/source))
for folder in ('backend','bridge'):
    for p in (ROOT/'artifacts/culinaryos-linux'/folder).iterdir():
        if p.is_file() and p.suffix!='.pdb':copy(p.relative_to(ROOT),Path('payload')/folder/p.name)
copy('artifacts/culinaryos-linux/rustup-init','toolchain/rustup-init')
expected=(ROOT/'deploy/culinaryos/rustup-init.sha256').read_text().split()[0].lower()
assert hashlib.sha256((OUT/'toolchain/rustup-init').read_bytes()).hexdigest()==expected
for name in ('Cargo.toml','Cargo.lock','build.rs','tauri.conf.json','tauri.linux.conf.json'):
    copy('src/MesaNexo.Web/src-tauri/'+name,'client-source/src-tauri/'+name)
for name in ('src','icons','capabilities'):tree('src/MesaNexo.Web/src-tauri/'+name,'client-source/src-tauri/'+name)
tree('src/MesaNexo.Web/dist','client-source/dist')
tree('deploy/culinaryos','deploy/culinaryos')
copy('deploy/culinaryos/install.sh','instalar.sh')
copy('deploy/install-runtime-extension.py','deploy/install-runtime-extension.py')
copy('runtime/mesanexo_ipc.py','runtime/mesanexo_ipc.py')
copy('vendor/culinaryos/source-hashes.json','vendor/culinaryos/source-hashes.json')
copy('branding/icons/icon-256.png','branding/icon-256.png')
copy('docs/CULINARYOS-INSTALACION.md','LEEME.md')
# Sanity checks: current packaged UI, native Linux payloads, no login artwork.
assert b'\x7fELF'==(OUT/'payload/backend/MesaNexo.Api').read_bytes()[:4]
assert b'\x7fELF'==(OUT/'payload/bridge/culinary-bridge').read_bytes()[:4]
assert 'login-art-empty' in (ROOT/'src/MesaNexo.Web/src/main.tsx').read_text()
lines=[]
for p in sorted(OUT.rglob('*')):
    if p.is_file():lines.append(hashlib.sha256(p.read_bytes()).hexdigest()+'  '+p.relative_to(OUT).as_posix())
(OUT/'SHA256SUMS').write_text('\n'.join(lines)+'\n',encoding='ascii',newline='\n')
report={'name':'Culinary Restaurant','version':'0.3.0','bundleRevision':args.revision,'target':'CulinaryOS Debian 13 amd64','tree':str(OUT),'files':len(lines),'linuxBackendPublished':True,'linuxBridgePublished':True,'linuxTauriBuildExecuted':False,'guestInstallationExecuted':False,'osLoginPolicyChanged':False,'externalAdaptersActivated':False,'rustToolchain':'1.99.1','rustupInitSha256':expected,'excluded':['credentials','Windows executables','runtime data','production configuration','local development caches']}
(ROOT/'evidence/culinaryos-integration.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,indent=2))
