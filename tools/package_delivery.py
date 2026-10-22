"""Package only explicit public project inputs; never caches, lab state or private keys."""
import hashlib
import json
from pathlib import Path
import re
import stat
import zipfile

ROOT=Path(__file__).resolve().parents[1]
ART=ROOT/'artifacts'

def digest(path):
    h=hashlib.sha256()
    with path.open('rb') as f:
        for data in iter(lambda:f.read(1024*1024),b''):h.update(data)
    return h.hexdigest()

def write_json(path,value):path.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')

def archive(target,files,executable=False):
    with zipfile.ZipFile(target,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for file,name in sorted(files,key=lambda pair:pair[1]):
            info=zipfile.ZipInfo(name)
            info.create_system=3
            mode=0o755 if executable and (file.name in ('MesaNexo.Central','MesaNexo.Public') or '.so' in file.name) else 0o644
            info.external_attr=(stat.S_IFREG|mode)<<16
            info.compress_type=zipfile.ZIP_DEFLATED
            z.writestr(info,file.read_bytes())
    with zipfile.ZipFile(target) as z:
        if z.testzip() is not None:raise ValueError('ZIP integrity failure')

def main():
    baseline=json.loads((ROOT/'docs/prototype-source-hashes.json').read_text(encoding='utf-8-sig'))
    modified=[entry['path'] for entry in baseline if digest(Path(entry['path'])).upper()!=entry['sha256'].upper()]
    if modified:raise ValueError('Prototype integrity differs: '+str(modified))
    vendor=json.loads((ROOT/'vendor/culinaryos/source-hashes.json').read_text(encoding='utf-8-sig'))
    if any(digest(ROOT/'vendor/culinaryos'/x['file']).upper()!=x['sha256'].upper() for x in vendor):raise ValueError('Runtime reference integrity differs')
    integration=json.loads((ROOT/'evidence/integration.json').read_text(encoding='utf-8'))
    runtime=json.loads((ROOT/'evidence/runtime.json').read_text(encoding='utf-8'))
    startup=json.loads((ROOT/'evidence/windows-startup.json').read_text(encoding='utf-8'))
    if not all(x['result']=='passed' for x in integration) or not runtime['successful'] or not startup['nativeStartup']:raise ValueError('Missing successful evidence')
    installer=ART/'CulinaryRestaurant-0.2.0-dev-win-x64.exe'
    if installer.read_bytes()[:2]!=b'MZ':raise ValueError('Installer is not PE')
    apk=ART/'android/CulinaryRestaurant-0.2.0-debug.apk'
    with zipfile.ZipFile(apk) as z:
        if 'AndroidManifest.xml' not in z.namelist() or not any(n.endswith('.dex') for n in z.namelist()) or z.testzip():raise ValueError('Invalid APK container')
    browser=[]
    for area in ('win-x64','linux-x64','central-linux-x64','public-linux-x64'):
        for file in (ART/'0.2.0'/area).rglob('*'):
            if file.is_file() and re.search(r'(^|[._-])(electron|chromium|chrome|webview)([._-]|$)',file.name,re.I):browser.append(str(file.relative_to(ART)))
    if browser:raise ValueError('Unexpected browser engine in deliverables: '+str(browser))
    report={'appName':'Culinary Restaurant','version':'0.2.0','brandingEvidence':'branding.json','externalAdaptersActivation':'isolated-by-user-decision; no routes or activation commands','portalVisualEvidence':'portal-visual.json','nativeLayoutAssertionsPassed':sum('native layout' in x.get('test','') for x in integration),'date':'2026-10-15','prototypeSourceFilesVerifiedUnchanged':len(baseline),'vendorRuntimeFilesVerifiedUnchanged':len(vendor),'integrationAssertionsPassed':len(integration),'runtimeTestsPassed':runtime['tests'],'windowsPublishedStartup':startup,'packageChecks':{'installer':'PE header verified; no installation performed','debian':'ar/tar payload and checksums checked by package_deb.py; dpkg not executed','android':'ZIP manifest, dex and CRC checked; see android-signature.txt for signature verification','browserEngineFilenameScan':'no matching engine filenames; native Avalonia architecture audited'},'notExecuted':['Debian VM integration, systemd, Unix socket and dpkg','Android device or emulator execution','iOS compilation or execution','Installer installation/uninstallation and full update cycle','Physical printer and physical power-cut recovery','PAC/bank/AMI/geocoding/FCM/APNs real or vendor sandbox execution'],'privateStateExcluded':['.lab','.dotnet','.packages','.reference','.tools','bin','obj','private key files']}
    write_json(ROOT/'evidence/delivery.json',report)
    central=ART/'CulinaryRestaurant-0.2.0-central-linux-x64.zip'
    archive(central,[(p,p.relative_to(ART/'0.2.0'/'central-linux-x64').as_posix()) for p in (ART/'0.2.0'/'central-linux-x64').rglob('*') if p.is_file()],True)
    public=ART/'CulinaryRestaurant-0.2.0-public-linux-x64.zip'
    archive(public,[(p,p.relative_to(ART/'0.2.0'/'public-linux-x64').as_posix()) for p in (ART/'0.2.0'/'public-linux-x64').rglob('*') if p.is_file()],True)
    source_files=[]
    blocked={'.lab','.packages','.dotnet','.reference','.tools','bin','obj','__pycache__','.git'}
    for area in ('src','tests','runtime','deploy','tools','vendor','docs','evidence','branding','.vscode'):
        for file in (ROOT/area).rglob('*'):
            relative=file.relative_to(ROOT)
            if not file.is_file() or relative.as_posix()=='evidence/artifacts.json' or blocked.intersection(relative.parts) or file.suffix.lower() in ('.key','.pfx','.p12','.keystore','.log'):continue
            if file.stat().st_size<2*1024*1024 and re.search(rb'^-----BEGIN (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----',file.read_bytes(),re.M):raise ValueError('Private PEM in public source: '+str(relative))
            source_files.append((file,relative.as_posix()))
    for name in ('README.md','.gitignore','Directory.Build.props','global.json','MesaNexo.slnx','NuGet.Config','requirements-lab.txt','CulinaryRestaurant.code-workspace'):
        source_files.append((ROOT/name,name))
    source=ART/'CulinaryRestaurant-0.2.0-source.zip';archive(source,source_files)
    deliverables=[installer,ART/'CulinaryRestaurant-0.2.0-dev-amd64.deb',apk,central,public,source]
    (ART/'SHA256SUMS.txt').write_text(''.join(digest(p)+'  '+p.relative_to(ART).as_posix()+'\n' for p in deliverables),encoding='utf-8')
    write_json(ROOT/'evidence/artifacts.json',[{'path':p.relative_to(ROOT).as_posix(),'bytes':p.stat().st_size,'sha256':digest(p)} for p in deliverables])
    print(json.dumps({'prototype_files_unchanged':len(baseline),'source_files':len(source_files),'deliverables':[{'name':p.name,'bytes':p.stat().st_size} for p in deliverables]},indent=2))

if __name__=='__main__':main()