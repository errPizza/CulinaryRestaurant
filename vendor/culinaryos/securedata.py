"""CulinaryOs crypto v1: libsodium XChaCha20-Poly1305, sin primitivas propias."""
import base64
import json
import os
import stat
import sys
from pathlib import Path
import uuid
from nacl import bindings, exceptions, pwhash

ALGORITHM="xchacha20poly1305-ietf"
MAX_PAYLOAD=64*1024
def canonical(value):
    return json.dumps(value,ensure_ascii=False,sort_keys=True,separators=(",",":"),allow_nan=False).encode("utf-8")
def encode(value):return base64.b64encode(value).decode("ascii")
def decode(value):return base64.b64decode(value,validate=True)

class Keyring:
    def __init__(self,value):
        self.active=value["active"]
        self.keys={k:decode(v) for k,v in value["keys"].items()}
        if self.active not in self.keys or not 1<=len(self.keys)<=32 or any(len(k)!=32 for k in self.keys.values()):
            raise ValueError("Anillo de claves no válido")
        if any(not isinstance(k,str) or len(k)>64 for k in self.keys):raise ValueError("ID de clave inválido")
    @staticmethod
    def generate():
        kid=str(uuid.uuid4())
        return {"active":kid,"keys":{kid:encode(os.urandom(32))}}
    @classmethod
    def load(cls,path):
        p=Path(path)
        if p.is_symlink():raise ValueError("No usar enlace para claves")
        if os.name=="posix" and p.stat().st_mode & 0o077:
            raise PermissionError("Las claves requieren permisos 0600")
        return cls(json.loads(p.read_text(encoding="utf-8")))
    @classmethod
    def load_systemd_credential(cls):
        # systemd concede lectura al UID del servicio mediante ACL. Su máscara
        # puede verse como 0440 en stat, sin conceder lectura al grupo propietario.
        if sys.platform!="linux":raise PermissionError("Credenciales systemd sólo en Linux")
        directory=os.environ.get("CREDENTIALS_DIRECTORY","")
        allowed={"/run/credentials/culinaryos-"+name+".service"
                 for name in ("data","sync","rekey","snapshot","credential-check")}
        if directory not in allowed:raise PermissionError("Directorio de credenciales no reconocido")
        uid=os.geteuid()
        for parent in ("/","/run","/run/credentials"):
            meta=os.lstat(parent)
            if not stat.S_ISDIR(meta.st_mode) or meta.st_uid!=0 or meta.st_mode & 0o022:
                raise PermissionError("Ruta de credenciales no protegida por root")
        meta=os.lstat(directory)
        if (not stat.S_ISDIR(meta.st_mode) or meta.st_uid not in (0,uid)
                or meta.st_mode & 0o227):
            raise PermissionError("Directorio de credenciales no protegido")
        path=directory+"/keyring"
        meta=os.lstat(path)
        if (not stat.S_ISREG(meta.st_mode) or meta.st_uid not in (0,uid)
                or meta.st_mode & 0o337):
            raise PermissionError("Credencial de claves no protegida")
        if not os.statvfs(directory).f_flag & os.ST_RDONLY:
            raise PermissionError("El almacén de credenciales requiere montaje de sólo lectura")
        return cls(json.loads(Path(path).read_text(encoding="utf-8")))
    def seal(self,data,context):
        raw=canonical(data)
        if len(raw)>MAX_PAYLOAD:raise ValueError("Carga mayor que 64 KiB")
        header={"v":1,"alg":ALGORITHM,"kid":self.active}
        aad=canonical({"header":header,"context":context})
        nonce=os.urandom(24)
        ciphertext=bindings.crypto_aead_xchacha20poly1305_ietf_encrypt(raw,aad,nonce,self.keys[self.active])
        return {**header,"nonce":encode(nonce),"ciphertext":encode(ciphertext)}
    def open(self,envelope,context):
        if set(envelope)!={"v","alg","kid","nonce","ciphertext"} or envelope["v"]!=1 or envelope["alg"]!=ALGORITHM:
            raise ValueError("Formato de cifrado no compatible")
        header={k:envelope[k] for k in ("v","alg","kid")}
        nonce=decode(envelope["nonce"]);ciphertext=decode(envelope["ciphertext"])
        if len(nonce)!=24 or not 16<=len(ciphertext)<=MAX_PAYLOAD+16:
            raise ValueError("Tamaño de sobre inválido")
        key=self.keys.get(envelope["kid"])
        if key is None:raise ValueError("Clave no disponible")
        raw=bindings.crypto_aead_xchacha20poly1305_ietf_decrypt(ciphertext,canonical({"header":header,"context":context}),nonce,key)
        return json.loads(raw.decode("utf-8"))

def hash_password(password):
    if not isinstance(password,str) or not 12<=len(password)<=1024:
        raise ValueError("Contraseña de 12 a 1024 caracteres")
    return pwhash.argon2id.str(password.encode("utf-8"),opslimit=pwhash.argon2id.OPSLIMIT_INTERACTIVE,
                              memlimit=pwhash.argon2id.MEMLIMIT_INTERACTIVE).decode("ascii")
def verify_password(encoded,password):
    try:return pwhash.argon2id.verify(encoded.encode("ascii"),password.encode("utf-8"))
    except (exceptions.InvalidkeyError,ValueError):return False

def protect_keyring(value,password,site_id):
    Keyring(value)
    if not isinstance(password,str) or not 16<=len(password)<=1024:raise ValueError("Contraseña de copia de 16 a 1024 caracteres")
    salt=os.urandom(pwhash.argon2id.SALTBYTES)
    key=pwhash.argon2id.kdf(32,password.encode(),salt,
        opslimit=pwhash.argon2id.OPSLIMIT_INTERACTIVE,memlimit=pwhash.argon2id.MEMLIMIT_INTERACTIVE)
    ring=Keyring({"active":"backup","keys":{"backup":encode(key)}})
    return {"v":1,"kdf":"argon2id-interactive","site_id":site_id,"salt":encode(salt),
            "envelope":ring.seal(value,{"purpose":"key-backup-v1","site_id":site_id})}

def unprotect_keyring(bundle,password,site_id):
    if bundle.get("v")!=1 or bundle.get("kdf")!="argon2id-interactive" or bundle.get("site_id")!=site_id:
        raise ValueError("Copia incompatible o de otra sucursal")
    salt=decode(bundle["salt"])
    if len(salt)!=pwhash.argon2id.SALTBYTES or not isinstance(password,str) or len(password)>1024:
        raise ValueError("Parámetros de copia no válidos")
    key=pwhash.argon2id.kdf(32,password.encode(),salt,
        opslimit=pwhash.argon2id.OPSLIMIT_INTERACTIVE,memlimit=pwhash.argon2id.MEMLIMIT_INTERACTIVE)
    ring=Keyring({"active":"backup","keys":{"backup":encode(key)}})
    value=ring.open(bundle["envelope"],{"purpose":"key-backup-v1","site_id":site_id})
    Keyring(value)
    return value
