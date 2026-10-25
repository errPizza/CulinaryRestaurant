import { Button } from './ui';
import { useState } from 'react';
import { DialogFrame, Presence } from './motion';
import { native } from './bridge';
export function ClientUpdate() {

    const [open, setOpen] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState(''), [manifest, setManifest] = useState<any>(null);
    async function perform(fn: () => Promise<any>) { if (busy) return; setBusy(true); setError(''); try { await fn(); } catch (e: any) { setError(e.message); } finally { setBusy(false); } }
    const close = () => { setOpen(false); setManifest(null); setError(''); };
    return <><Button className="text-button" onClick={() => setOpen(true)}>Actualizar aplicación…</Button><Presence>{open && <DialogFrame label="Actualización de Culinary Restaurant" close={close} busy={busy}><div className="section-line"><h2>Actualización de la aplicación</h2><Button className="ghost" disabled={busy} onClick={close} aria-label="Cerrar actualización">×</Button></div><p>La sucursal busca la versión que Culinary publicó para tu licencia. Antes de instalar se comprueban la firma, la sucursal, la versión, el tamaño y la integridad del instalador. Cierra la sesión y todas las cuentas primero.</p>{error && <div className="alert error" role="alert">{error}</div>}
        {manifest?.update ? <><h3>Instalar versión {manifest.version}</h3>{manifest.notes && <p>{manifest.notes}</p>}<p>Al confirmar se comprueban de nuevo el instalador y la autorización del servidor; la aplicación se cerrará para abrir el instalador.</p><Button className="primary" disabled={busy} onClick={() => perform(() => native('updates.install'))}>Confirmar e instalar</Button></>
            : manifest ? <p role="status">Ya tienes la versión más reciente publicada para tu licencia.</p>
                : <div className="dialog-actions"><Button className="primary" disabled={busy} onClick={() => perform(async () => setManifest(await native('updates.prepare')))}>{busy ? 'Buscando…' : 'Buscar actualización'}</Button></div>}
    </DialogFrame>}</Presence></>;

}
