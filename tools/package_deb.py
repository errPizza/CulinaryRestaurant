import gzip
import hashlib
import io
from pathlib import Path
import tarfile
import sys
ROOT=Path(__file__).resolve().parents[1]

def tarball(entries):
    raw=io.BytesIO()
    with gzip.GzipFile(fileobj=raw,mode='wb',mtime=0) as gz:
        with tarfile.open(fileobj=gz,mode='w') as tar:
            for path,data,mode in sorted(entries):
                info=tarfile.TarInfo(path);info.size=len(data);info.mode=mode;info.uid=info.gid=0;info.uname=info.gname='root';info.mtime=0;tar.addfile(info,io.BytesIO(data))
    return raw.getvalue()
def ar(entries):
    result=bytearray(b'!<arch>\n')
    for name,data in entries:
        header=f'{name+"/":<16}{0:<12}{0:<6}{0:<6}{"100644":<8}{len(data):<10}`\n'.encode('ascii')
        if len(header)!=60:raise ValueError('invalid ar header')
        result.extend(header);result.extend(data)
        if len(data)%2:result.extend(b'\n')
    return bytes(result)
def main():
    entries=[]
    for area in ('client','backend'):
        directory=ROOT/'artifacts'/'0.2.0'/'linux-x64'/area
        if not directory.exists():raise ValueError('Publish Linux first')
        for file in directory.rglob('*'):
            if file.is_file():
                target='opt/culinaryos-app/'+area+'/'+file.relative_to(directory).as_posix();mode=0o755 if file.name in ('CulinaryRestaurant','MesaNexo.Api') or '.so' in file.name else 0o644;entries.append((target,file.read_bytes(),mode))
    for name in ('deploy/install-runtime-extension.py','deploy/application.server.example.json','deploy/application.terminal.example.json','deploy/application.policy.example.json','deploy/branch-init.example.json','runtime/mesanexo_ipc.py','vendor/culinaryos/source-hashes.json'):
        entries.append(('opt/culinaryos-app/integration/'+name,(ROOT/name).read_bytes(),0o644))
    entries.append(('usr/share/applications/culinary-restaurant.desktop',(ROOT/'deploy/culinary-restaurant.desktop').read_bytes(),0o644))
    for size in (16,24,32,48,64,128,256):
        entries.append((f'usr/share/icons/hicolor/{size}x{size}/apps/culinary-restaurant.png',(ROOT/f'branding/icons/icon-{size}.png').read_bytes(),0o644))
    entries.append(('usr/share/icons/hicolor/512x512/apps/culinary-restaurant.png',(ROOT/'branding/culinary-restaurant.png').read_bytes(),0o644))
    control='Package: mesanexo-native\nVersion: 0.2.0\nArchitecture: amd64\nMaintainer: Culinary Restaurant\nSection: misc\nPriority: optional\nDepends: libc6 (>= 2.36), libgcc-s1, libstdc++6, zlib1g, libssl3t64, libicu76, libx11-6, libice6, libsm6, libfontconfig1, libsecret-tools, gnome-keyring, dbus-user-session\nDescription: Culinary Restaurant native development candidate for CulinaryOS\n Does not provision keys, reset data, register services or change networking.\n'
    md5=''.join(hashlib.md5(data).hexdigest()+'  '+name+'\n' for name,data,_ in sorted(entries))
    target=ROOT/'artifacts'/'CulinaryRestaurant-0.2.0-dev-amd64.deb';target.write_bytes(ar([('debian-binary',b'2.0\n'),('control.tar.gz',tarball([('control',control.encode(),0o644),('md5sums',md5.encode(),0o644)])),('data.tar.gz',tarball(entries))]));print(target.name,target.stat().st_size,'bytes')
    # Parse the generated container and verify every payload entry and digest independently.
    raw=target.read_bytes();position=8;members={}
    while position<len(raw):
        header=raw[position:position+60];length=int(header[48:58]);name=header[:16].decode().strip().rstrip('/');members[name]=raw[position+60:position+60+length];position+=60+length+(length%2)
    with tarfile.open(fileobj=io.BytesIO(members['data.tar.gz']),mode='r:gz') as tar:
        actual={info.name:hashlib.md5(tar.extractfile(info).read()).hexdigest() for info in tar.getmembers()}
    if any(actual[name]!=hashlib.md5(data).hexdigest() for name,data,_ in entries):raise ValueError('Deb payload verification failed')
    print('Deb ar/tar structure, payload and checksums verified; dpkg installation not tested on Windows.')
if __name__=='__main__':main()