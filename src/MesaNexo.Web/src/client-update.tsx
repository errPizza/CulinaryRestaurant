import { Button } from './ui';
import { useState } from 'react';
import { DialogFrame, Presence } from './motion';
import { native } from './bridge';
export function ClientUpdate() {

    const [open, setOpen] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState(''), [manifest, setManifest] = useState<any>(null);
    async function perform(fn: () => Promise<any>) { if (busy) return; setBusy(true); setError(''); try { await fn(); } catch (e: any) { setError(e.message); } finally { setBusy(false); } }
    const close = () => { setOpen(false); setManifest(null); setError(''); };
    return <><Button className="text-button" onClick={() => setOpen(true)}>Actualizar aplicación…</Button><Presence>{open && <DialogFrame label="Actualización de Culinary Restaurant" close={close} busy={busy}><div className="section-line"><h2>Actualización de la aplicación</h2><Button className="ghost" disabled={busy} onClick={close} aria-label="Cerrar actualización">×</Button></div><p>La sucursal busca la versión que Culinary publicó para tu licencia. Antes de instalar se comprueban la firma, la sucursal, la versión, el tamaño y la integridad del instalador. Cierra la sesión, todas las cuentas y la caja primero.</p>{error && <div className="alert error" role="alert">{error}</div>}
        {manifest?.update ? <><h3>Instalar versión {manifest.version}</h3>{manifest.notes && <p>{manifest.notes}</p>}<p>Al confirmar se comprueban de nuevo el instalador y la autorización del servidor; la aplicación se cerrará para instalarla.</p><Button className="primary" disabled={busy} onClick={() => perform(() => native('updates.install'))}>Confirmar e instalar</Button></>
            : manifest ? <p role="status">Ya tienes la versión más reciente publicada para tu licencia.</p>
                : <div className="dialog-actions"><Button className="primary" disabled={busy} onClick={() => perform(async () => setManifest(await native('updates.prepare')))}>{busy ? 'Buscando…' : 'Buscar actualización'}</Button></div>}
    </DialogFrame>}</Presence></>;

}
// Inside the app: a manager looks, sees what would stop it and who would be signed out, and confirms once. The polls
// wait meanwhile (hold): the transport answers one call at a time and a download would pile them up behind it.
export function VersionDialog({ version, manager, close, hold, signedOut }: { version: string; manager: boolean; close: () => void; hold: (held: boolean) => void; signedOut: (reason: string) => void }) {

    const [busy, setBusy] = useState(''), [error, setError] = useState(''), [offer, setOffer] = useState<any>(null);
    async function perform(label: string, fn: () => Promise<any>) { if (busy) return; setBusy(label); setError(''); hold(true); try { await fn(); } catch (e: any) { setError(e.message); } finally { setBusy(''); hold(false); } }
    const look = () => perform('Buscando…', async () => setOffer(await native('updates.look')));
    // Everyone was signed out before the installer failed to start: the window goes with them, and says why.
    const install = () => perform('Descargando y comprobando…', async () => { try { await native('updates.prepare'); await native('updates.install'); } catch (e: any) { if (!(await native('status')).authenticated) signedOut(e.message); else throw e; } });
    const others = offer?.otherSessions || 0;
    return <DialogFrame label={'Culinary Restaurant ' + version} close={close} busy={!!busy}><div className="section-line"><h2>Culinary Restaurant {version}</h2><Button className="ghost" disabled={!!busy} onClick={close} aria-label="Cerrar versión">×</Button></div><p>Interfaz React y CSS · Transporte .NET de confianza. Los adaptadores externos permanecen aislados.</p>{error && <div className="alert error" role="alert">{error}</div>}
        {!manager ? <p>Un gerente puede buscar actualizaciones desde aquí.</p>
            : offer?.update ? <><h3>Versión {offer.version} disponible</h3>{offer.notes && <p>{offer.notes}</p>}
                {offer.blocked ? <><div className="alert warn" role="status">{offer.blocked}</div><div className="dialog-actions"><Button disabled={!!busy} onClick={look}>{busy || 'Comprobar de nuevo'}</Button></div></>
                    : <><p>Al confirmar se descarga y se comprueba el instalador. Después se cierra tu sesión{others > 0 ? ' y la de ' + (others === 1 ? 'otro dispositivo' : others + ' dispositivos más') : ''}, y la aplicación se cierra para instalarla. Los borradores se conservan.</p><div className="dialog-actions"><Button className="primary" disabled={!!busy} onClick={install}>{busy || 'Actualizar ahora'}</Button></div></>}</>
                : <>{offer && <p role="status">Ya tienes la versión más reciente publicada para tu licencia.</p>}<div className="dialog-actions"><Button className="primary" disabled={!!busy} onClick={look}>{busy || 'Buscar actualización'}</Button></div></>}
    </DialogFrame>;

}
