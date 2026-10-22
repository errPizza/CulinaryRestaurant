"""Entrega a central por mTLS; nunca borra operaciones ante fallo o ACK ambiguo."""
import http.client
import json
import ssl
import time
from urllib.parse import urlsplit
from securedata import canonical

class HTTPSCentral:
    def __init__(self,config):
        url=urlsplit(config["url"])
        if url.scheme!="https" or not url.hostname or url.username or url.password or url.query or url.fragment:
            raise ValueError("Central requiere URL HTTPS sin credenciales/query/fragment")
        self.url=url
        self.context=ssl.create_default_context(cafile=config["ca_file"])
        self.context.minimum_version=ssl.TLSVersion.TLSv1_2
        self.context.load_cert_chain(config["client_certificate"],config["client_key"])
    def send(self,event,digest):
        connection=http.client.HTTPSConnection(self.url.hostname,self.url.port or 443,timeout=5,context=self.context)
        try:
            connection.request("POST",self.url.path or "/",body=canonical(event),
                headers={"Content-Type":"application/json","Idempotency-Key":event["event_id"],
                         "X-CulinaryOs-SHA256":digest})
            response=connection.getresponse()
            if response.status not in (200,201):raise ValueError("Central no confirmó persistencia")
            raw=response.read(8193)
            if len(raw)>8192:raise ValueError("Respuesta central excede límite")
            return json.loads(raw)
        finally:connection.close()

def deliver(store,transport,now=None):
    sent=0
    started=time.monotonic()
    for event,digest in store.pending(now=now,limit=64):
        if time.monotonic()-started>=20:break
        try:
            ack=transport.send(event,digest)
            if not isinstance(ack,dict) or ack.get("site_id")!=store.site or ack.get("event_id")!=event["event_id"] or ack.get("sha256")!=digest or ack.get("status")!="committed":
                raise ValueError("ACK no válido")
            store.acknowledge(event["event_id"],digest)
            sent+=1
        except Exception as error:
            store.retry(event["event_id"],now)
            reason="tls" if isinstance(error,ssl.SSLError) else "network" if isinstance(error,OSError) else "ack" if isinstance(error,(ValueError,TypeError)) else "protocol"
            return {"sent":sent,"state":"pending","reason":reason}
    return {"sent":sent,"state":"idle" if not sent else "delivered"}
