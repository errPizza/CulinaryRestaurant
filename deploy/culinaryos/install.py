#!/usr/bin/python3
"""First installation on CulinaryOS. Preserve OS login policy, data keys and existing accounts."""
import argparse, datetime, getpass, grp, hashlib, ipaddress, json, os, re, secrets, shutil, ssl, struct, subprocess, sys, tempfile, urllib.parse
from pathlib import Path
try:
    import pwd
except ImportError:
    pwd = None
RUNTIME = Path('/usr/local/lib/culinaryos')
CONFIG = Path('/etc/culinaryos')
APP = Path('/opt/culinaryos-app')
ENTRY = '/opt/culinaryos-app/client/CulinaryRestaurant'
HOOK = 'import mesanexo_ipc\nmesanexo_ipc.install(sys.modules[__name__])\n'
def run(*args, **kwargs): return subprocess.run([str(a) for a in args], check=True, **kwargs)
def digest(path): return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def validate_site(site):
    if not re.fullmatch(r'[a-z0-9-]{1,32}', site.get('site_id', '')):
        raise ValueError('Identidad de sitio no valida')
    name = site.get('server_name', '')
    if len(name) > 253 or not name or not all(re.fullmatch(r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?', p) for p in name.split('.')):
        raise ValueError('Nombre TLS no valido')
    address = ipaddress.IPv4Address(site['server_address'])
    if site.get('role') != 'servidor':
        raise ValueError('Este CD integra servidor + interfaz; usar el perfil sucursal')
    if address not in ipaddress.IPv4Network(site['network']):
        raise ValueError('Servidor fuera de la red de sucursal')
    return site

def known_extensions(bundle):
    # Earlier MesaNexo extensions this package may replace. Without the list nothing is replaced.
    path = bundle / 'deploy/culinaryos/extension-history.json'
    if not path.is_file():
        return set()
    return {value.lower() for release in json.loads(path.read_text())['releases'] for value in release['sha256']}

def validate_runtime(bundle, runtime=RUNTIME):
    """Returns what the extension needs: 'absent', 'current' or 'upgrade' (a known earlier version)."""
    for row in json.loads((bundle / 'vendor/culinaryos/source-hashes.json').read_text()):
        p = runtime / row['file']
        if p.is_symlink() or not p.is_file():
            raise ValueError('Runtime no compatible: ' + row['file'])
        data = p.read_bytes()
        if row['file'] == 'dataio.py':
            data = data.replace(HOOK.encode(), b'')
        actual = hashlib.sha256(data).hexdigest().lower()
        accepted = {value.lower() for value in [row['sha256'], *row.get('compatibleSha256', [])]}
        if actual not in accepted:
            raise ValueError('Runtime distinto; revisar antes de instalar: ' + row['file'] + ' (SHA256 ' + actual + ')')
    extension = runtime / 'mesanexo_ipc.py'
    hooked = HOOK in (runtime / 'dataio.py').read_text()
    if extension.exists():
        installed = digest(extension)
        package = digest(bundle / 'runtime/mesanexo_ipc.py')
        if not extension.is_symlink() and hooked and installed == package:
            return 'current'
        if not extension.is_symlink() and hooked and installed in known_extensions(bundle):
            return 'upgrade'
        raise ValueError('Extension previa distinta o incompleta; no se sobrescribe (SHA256 instalada ' + installed + ', paquete ' + package + ', hook ' + str(hooked) + ')')
    elif hooked:
        raise ValueError('Falta la extension del runtime')
    return 'absent'

def preflight(bundle):
    if sys.platform != 'linux' or os.geteuid() != 0:
        raise PermissionError('Solo root dentro de CulinaryOS')
    os.umask(0o077)
    sys.path.insert(0, str(RUNTIME))
    from dataio import persistent_root, Client
    persistent_root()
    if not (CONFIG / 'profile').is_file() or not Path('/etc/lightdm').is_dir():
        raise ValueError('Se requiere el escritorio CulinaryOS instalado')
    if (CONFIG / 'application.json').exists():
        raise ValueError('Ya existe una aplicacion registrada; no se reemplaza ni se cambian cuentas')
    if APP.exists() and not (APP / 'culinary-restaurant-installing.json').is_file():
        raise ValueError('/opt/culinaryos-app ya existe; revisar')
    site = validate_site(json.loads((CONFIG / 'site.json').read_text()))
    if json.loads((CONFIG / 'branch.json').read_text()).get('site_id') != site['site_id']:
        raise ValueError('Identidad de almacen diferente; no se cambia')
    if validate_runtime(bundle) == 'upgrade':
        print('La extension instalada es una version anterior conocida: se reemplazara tras un snapshot y una copia del codigo.')
    status = Client().call('health')
    if status.get('ready') is not True or status.get('site_id') != site['site_id']:
        raise ValueError('Almacen CulinaryOS no disponible')
    pwd.getpwnam('operador')
    pwd.getpwnam('culinaryos-app')
    rules = Path('/etc/nftables.conf').read_text()
    if 'set application_ports' not in rules or 'tcp dport @application_ports' not in rules:
        raise ValueError('Primero configurar la red local de CulinaryOS')
    print('Comprobado: sucursal ' + site['site_id'] + ', almacen existente, runtime compatible. Se conservan las claves y el login de Linux.')
    return site

def write_new(path, data, mode=0o644):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o755)
    if path.is_symlink():
        raise ValueError('Destino es enlace: ' + str(path))
    if path.exists():
        if path.read_bytes() == data:
            return
        raise FileExistsError('No se reemplaza: ' + str(path))
    with path.open('xb') as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    os.chmod(path, mode)
    os.chown(path, 0, 0)

def set_application_permissions():
    # Only public application code. Never change data, vault or TLS/key directories.
    directories = (APP, APP / 'backend', APP / 'client')
    executables = (APP / 'backend/MesaNexo.Api', APP / 'client/CulinaryRestaurant', APP / 'client/culinary-restaurant', APP / 'client/culinary-bridge')
    for directory in directories:
        if directory.is_symlink() or not directory.is_dir() or directory.stat().st_uid != 0:
            raise PermissionError('Directorio de codigo no valido: ' + str(directory))
    for executable in executables:
        if executable.is_symlink() or not executable.is_file() or executable.stat().st_uid != 0:
            raise PermissionError('Ejecutable de codigo no valido: ' + str(executable))
    for path in (*directories, *executables):
        os.chmod(path, 0o755)

def verify_application_access():
    run('runuser', '-u', 'culinaryos-app', '--', '/usr/bin/test', '-x', APP / 'backend/MesaNexo.Api')
    for name in ('CulinaryRestaurant', 'culinary-restaurant', 'culinary-bridge'):
        run('runuser', '-u', 'operador', '--', '/usr/bin/test', '-x', APP / 'client' / name)

def set_public_icon_directory(directory=Path('/usr/share/icons/hicolor/256x256/apps')):
    for path in (directory.parent, directory):
        if path.is_symlink():
            raise PermissionError('Directorio de iconos no puede ser enlace')
        path.mkdir(mode=0o755, exist_ok=True)
        if path.stat().st_uid != 0:
            raise PermissionError('Directorio de iconos debe pertenecer a root')
        os.chmod(path, 0o755)
    return directory

def copy_files(source, dest):
    for p in sorted(source.rglob('*')):
        if p.is_symlink():
            raise ValueError('El paquete no puede contener enlaces')
        if p.is_file():
            write_new(dest / p.relative_to(source), p.read_bytes(), 0o755 if p.name in ('MesaNexo.Api', 'culinary-bridge') else 0o644)

def check_elf(path):
    if path.is_symlink():
        raise ValueError('Binario no puede ser enlace')
    data = path.read_bytes()
    if len(data) < 64 or data[:6] != b'\x7fELF\x02\x01' or struct.unpack_from('<H', data, 18)[0] != 62:
        raise ValueError('Se necesita ELF Linux x86-64 real')
    return data

def connection(site, ca):
    pem = ca.read_text(encoding='ascii')
    if pem.count('-----BEGIN CERTIFICATE-----') != 1:
        raise ValueError('Se requiere una unica CA')
    return {'ServerUrl': 'https://' + site['server_name'] + ':8443', 'SiteId': site['site_id'], 'CaFile': str(ca), 'CaSha256': hashlib.sha256(ssl.PEM_cert_to_DER_cert(pem)).hexdigest(), 'ConnectAddress': site['server_address'], 'TenantId': '', 'BranchId': ''}

def integrations_directory():
    # Credentials of the live integrations, one JSON per provider (examples in deploy/integrations): only root writes and the
    # backend reads. Files placed here should be root:culinaryos-app 0640.
    account = pwd.getpwnam('culinaryos-app')
    directory = CONFIG / 'integrations'
    if directory.is_symlink():
        raise ValueError('/etc/culinaryos/integrations no puede ser un enlace')
    directory.mkdir(mode=0o750, exist_ok=True)
    os.chown(directory, 0, account.pw_gid)
    os.chmod(directory, 0o750)
    return directory

def certificates(site, temporary_root="/run"):
    directory = CONFIG / 'secrets'
    directory.mkdir(mode=0o700, exist_ok=True)
    ca = CONFIG / 'restaurant-ca.crt'
    cakey = directory / 'restaurant-ca.key'
    cert = CONFIG / 'restaurant-tls.crt'
    key = directory / 'restaurant-tls.key'
    files = (ca, cakey, cert, key)
    if any(p.is_symlink() for p in files):
        raise ValueError('Certificado no puede ser enlace')
    if any(p.exists() for p in files) and not all(p.is_file() for p in files):
        raise ValueError('Certificados parciales: revisar, no se reemplazan')
    if not ca.exists():
        # Application TLS material only; never replaces data keys or an existing site CA.
        with tempfile.TemporaryDirectory(prefix='culinary-tls-', dir=temporary_root) as tmp:
            tmp = Path(tmp)
            def openssl(*args): run('openssl', *args, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
            openssl('req', '-x509', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-noenc', '-days', '3650', '-sha256', '-subj', '/CN=Culinary Restaurant ' + site['site_id'], '-addext', 'basicConstraints=critical,CA:TRUE,pathlen:0', '-addext', 'keyUsage=critical,keyCertSign,cRLSign', '-keyout', tmp / 'ca.key', '-out', tmp / 'ca.crt')
            openssl('req', '-new', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-noenc', '-subj', '/CN=' + site['server_name'], '-keyout', tmp / 'server.key', '-out', tmp / 'server.csr')
            (tmp / 'extensions').write_text('basicConstraints=critical,CA:false\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=serverAuth\nsubjectAltName=DNS:' + site['server_name'] + ',IP:' + site['server_address'] + '\n')
            openssl('x509', '-req', '-in', tmp / 'server.csr', '-CA', tmp / 'ca.crt', '-CAkey', tmp / 'ca.key', '-set_serial', str(secrets.randbits(120) + 1), '-days', '365', '-sha256', '-extfile', tmp / 'extensions', '-out', tmp / 'server.crt')
            for dest, src, mode in ((ca, 'ca.crt', 0o644), (cakey, 'ca.key', 0o600), (cert, 'server.crt', 0o600), (key, 'server.key', 0o600)):
                write_new(dest, (tmp / src).read_bytes(), mode)
    run('openssl', 'verify', '-purpose', 'sslserver', '-verify_hostname', site['server_name'], '-CAfile', ca, cert)
    run('openssl', 'x509', '-in', cert, '-noout', '-checkend', '604800', stdout=subprocess.DEVNULL)
    return ca, cert, key

def initialize_branch(site):
    from dataio import Client
    meta = Client().call('get', record_id='mn_meta_site')
    if meta:
        if meta.get('data', {}).get('siteId') != site['site_id']:
            raise ValueError('Datos de otra sucursal')
        if meta['data'].get('schemaVersion', 1) < 2:
            raise ValueError('Migrar esquema en mantenimiento antes de instalar')
        print('Cuentas y datos de restaurante existentes conservados.')
        return
    print('Primera instalacion del restaurante: crea tu cuenta; no hay cuentas de demostracion.')
    def required(label):
        value = input(label).strip()
        if not value:
            raise ValueError('Campo requerido')
        return value
    name = required('Nombre del restaurante: ')
    manager = required('Nombre del gerente: ')
    username = required('Usuario del gerente: ')
    password = getpass.getpass('Contrasena del gerente (minimo 12 caracteres): ')
    if len(password) < 12 or password != getpass.getpass('Repite la contrasena: '):
        raise ValueError('Contrasenas diferentes o demasiado cortas')
    enrollment = secrets.token_urlsafe(24)
    config = {'siteId': site['site_id'], 'name': name, 'mode': 'licencia', 'managerName': manager, 'managerUsername': username, 'managerPassword': password, 'enrollmentCode': enrollment, 'modules': [], 'maxUsers': 15, 'maxTables': 30}
    account = pwd.getpwnam('culinaryos-app')
    state = Path('/var/lib/culinaryos-app')
    if not state.exists():
        state.mkdir(mode=0o700)
        os.chown(state, account.pw_uid, account.pw_gid)
    if state.is_symlink() or state.stat().st_uid != account.pw_uid:
        raise ValueError('Directorio de estado no valido')
    with tempfile.TemporaryDirectory(prefix='culinary-initialize-', dir='/run') as tmp:
        tmp = Path(tmp)
        os.chown(tmp, account.pw_uid, account.pw_gid)
        file = tmp / 'initial.json'
        file.write_text(json.dumps(config))
        file.chmod(0o600)
        os.chown(file, account.pw_uid, account.pw_gid)
        try:
            run('runuser', '-u', 'culinaryos-app', '--', 'env', 'DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/culinaryos-app/dotnet-bundle', APP / 'backend/MesaNexo.Api', '--site', site['site_id'], '--initialize', file)
        finally:
            file.unlink(missing_ok=True)
    print('\nUsuario: ' + username + '\nCODIGO DE ALTA DEL DISPOSITIVO: ' + enrollment + '\nGuardalo ahora. No se escribe en logs ni se muestra otra vez.\n')

def backup_configuration(backup, source_root=Path('/')):
    backup.mkdir(parents=True, mode=0o700)
    for relative in ('etc/xdg/autostart/culinaryos.desktop', 'etc/culinaryos/site.json', 'etc/nftables.conf'):
        source = source_root / relative
        if source.is_symlink():
            raise ValueError('Revisar enlace: ' + str(source))
        if source.is_file():
            dest = backup / relative
            dest.parent.mkdir(parents=True, mode=0o700, exist_ok=True)
            shutil.copy2(source, dest)

def find_compiled_client(bundle, build_root=Path('/var/tmp'), owner=None):
    if owner is None:
        owner = int(os.environ.get('SUDO_UID', '0'))
        if owner <= 0:
            raise ValueError('Reanudar desde tu cuenta administradora usando sudo')
    expected = bundle / 'client-source'
    candidates = sorted(build_root.glob('culinary-restaurant-build.*/client-source/src-tauri/target/release/culinary-restaurant'), key=lambda p: p.stat().st_mtime, reverse=True)
    for binary in candidates:
        work = binary.parents[4]
        if work.is_symlink() or work.stat().st_uid != owner or binary.resolve() != binary.absolute():
            continue
        matching = True
        for source in expected.rglob('*'):
            if not source.is_file():
                continue
            built = work / 'client-source' / source.relative_to(expected)
            if not built.is_file() or built.resolve() != built.absolute() or digest(built) != digest(source):
                matching = False
                break
        bridge = work / 'client-source/src-tauri/binaries/culinary-bridge-x86_64-unknown-linux-gnu'
        if not matching or not bridge.is_file() or bridge.resolve() != bridge.absolute() or digest(bridge) != digest(bundle / 'payload/bridge/culinary-bridge'):
            continue
        check_elf(binary)
        print('Se reutiliza el cliente Linux ya compilado: ' + str(binary))
        return binary
    raise ValueError('No se encontro una compilacion previa compatible. Se conserva la existente; no se recompila automaticamente.')

def license_server(url):
    # CulinaryServer: the branch activates and renews its license there over HTTPS with its own key.
    parsed = urllib.parse.urlsplit(url or '')
    if parsed.scheme != 'https' or not parsed.hostname or parsed.query or parsed.fragment or parsed.username:
        raise ValueError('--license-server debe ser la direccion HTTPS de CulinaryServer')
    return url.rstrip('/')

def backend_dropin():
    # Root step that reads the machine identifiers, and the TPM 2.0 when the machine has one and tpm2-tools.
    lines = ['[Service]', 'Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/culinaryos-app/dotnet-bundle', 'ExecStartPre=+/usr/bin/python3 ' + str(APP / 'hardware-identity.py')]
    try:
        grp.getgrnam('tss')
        tpm = Path('/dev/tpmrm0').exists() and Path('/usr/bin/tpm2_sign').exists()
    except KeyError:
        tpm = False
    if tpm:
        lines += ['BindPaths=/dev/tpmrm0', 'DeviceAllow=/dev/tpmrm0 rw', 'SupplementaryGroups=tss']
    return ('\n'.join(lines) + '\n').encode()

def install(bundle, client, server):
    server = license_server(server)
    site = preflight(bundle)
    binary = check_elf(client)
    check_elf(bundle / 'payload/backend/MesaNexo.Api')
    check_elf(bundle / 'payload/bridge/culinary-bridge')
    backup = Path('/var/backups/culinaryos') / ('restaurant-' + datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ'))
    backup_configuration(backup)
    print('Copia de configuracion: ' + str(backup))
    APP.mkdir(mode=0o755, exist_ok=True)
    write_new(APP / 'culinary-restaurant-installing.json', b'{"version":"0.3.0"}\n')
    write_new(APP / 'hardware-identity.py', (bundle / 'deploy/culinaryos/hardware-identity.py').read_bytes(), 0o755)
    copy_files(bundle / 'payload/backend', APP / 'backend')
    copy_files(bundle / 'payload/bridge', APP / 'client')
    write_new(APP / 'client/culinary-restaurant', binary, 0o755)
    write_new(Path(ENTRY), (bundle / 'deploy/culinaryos/CulinaryRestaurant').read_bytes(), 0o755)
    set_application_permissions()
    verify_application_access()
    extension = validate_runtime(bundle)
    if extension == 'absent':
        run('/usr/bin/python3', bundle / 'deploy/install-runtime-extension.py')
    elif extension == 'upgrade':
        run('/usr/bin/python3', bundle / 'deploy/install-runtime-extension.py', '--upgrade')
    from dataio import Client
    if 'transaction_v1' not in Client().call('capabilities_v1')['extensions']:
        raise ValueError('Falta extension transaccional')
    initialize_branch(site)
    ca, cert, key = certificates(site)
    write_new(CONFIG / 'restaurant-connection.json', (json.dumps(connection(site, ca), indent=2) + '\n').encode())
    integrations_directory()
    manifest = {'version': 1, 'mode': 'server', 'ui_argv': [ENTRY], 'backend_argv': [str(APP / 'backend/MesaNexo.Api'), '--site', site['site_id'], '--port', '8443', '--license-server', server], 'port': 8443, 'readiness_socket': '/run/culinaryos-app/ready.sock', 'tls_ca_file': str(ca), 'credentials': {'tls-key': str(key), 'tls-cert': str(cert)}}
    file = APP / 'application.json'
    write_new(file, (json.dumps(manifest, indent=2) + '\n').encode())
    write_new(Path('/etc/systemd/system/culinaryos-backend.service.d/restaurant.conf'), backend_dropin())
    run('culinaryos-runtime', 'register-app', file)
    # Reuse the existing autostart ID. OS authentication policy remains unchanged.
    autostart = Path('/etc/xdg/autostart/culinaryos.desktop')
    with tempfile.NamedTemporaryFile(dir=autostart.parent, delete=False) as stream:
        stream.write((bundle / 'deploy/culinaryos/culinaryos.desktop').read_bytes())
        stream.flush()
        os.fsync(stream.fileno())
        temp = stream.name
    os.chmod(temp, 0o644)
    os.chown(temp, 0, 0)
    os.replace(temp, autostart)
    write_new(Path('/usr/share/applications/culinary-restaurant.desktop'), (bundle / 'deploy/culinaryos/culinary-restaurant.desktop').read_bytes())
    icon_directory = set_public_icon_directory()
    write_new(icon_directory / 'culinary-restaurant.png', (bundle / 'branding/icon-256.png').read_bytes())
    write_new(APP / 'culinary-restaurant-installed.json', json.dumps({'version': '0.3.0', 'site_id': site['site_id'], 'backendReady': True, 'autostart': True, 'osLoginPolicyChanged': False}).encode())
    print('Backend registrado, TLS verificado y arranque de la app al entrar en la sesion configurado.')
    print('Linux puede pedir crear/desbloquear el llavero. Se conserva la politica de login del sistema.')
    print('Integraciones reales: coloca sus credenciales en /etc/culinaryos/integrations (ver deploy/integrations) y elige Real en la app; el central no se configura aqui.')

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('preflight', 'install', 'resume'))
    parser.add_argument('--bundle', type=Path, required=True)
    parser.add_argument('--client', type=Path)
    parser.add_argument('--license-server', help='Direccion HTTPS de CulinaryServer')
    args = parser.parse_args()
    try:
        if args.command == 'preflight':
            preflight(args.bundle.resolve())
        elif args.command == 'resume':
            install(args.bundle.resolve(), find_compiled_client(args.bundle.resolve()), args.license_server)
        else:
            if args.client is None:
                parser.error('--client requerido')
            install(args.bundle.resolve(), args.client.absolute(), args.license_server)
    except Exception as error:
        print('No se completo la instalacion: ' + str(error), file=sys.stderr)
        print('No recrees la sucursal ni sus claves. Conserva cualquier cuenta ya creada y revisa el error.', file=sys.stderr)
        raise SystemExit(1)
if __name__ == '__main__':
    main()
