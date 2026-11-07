import { Button } from './ui';
import { useState } from 'react';
import { DialogFrame, Presence } from './motion';
import { UpdateScreen } from './update-screen';
import { native } from './bridge';
// Versions travel as x.y.z (0.3.4) and are shown with two-digit patches (0.3.04); both mean the same number.
export const shownVersion = (v: string) => { const [a, b, c] = String(v || '').split('.').map(Number); return [a, b, c].every(Number.isFinite) ? `${a}.${b}.${String(c).padStart(2, '0')}` : v; };
const parts = (v: string) => String(v || '').split('.').map(n => Number(n) || 0);
export function compareVersions(a: string, b: string) { const x = parts(a), y = parts(b); for (let i = 0; i < 3; i++) if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) > (y[i] || 0) ? 1 : -1; return 0; }
// What to say when nothing is offered: up to date, newer than what is published, or nothing published at all.
export function NoUpdate({ offer }: { offer: any }) {

    const current = offer?.currentVersion, latest = offer?.latestVersion;
    if (!latest) return <p role="status" className="update-verdict">No hay versiones publicadas para tu licencia{current ? ' · tienes la ' + shownVersion(current) : ''}.</p>;
    const order = current ? compareVersions(current, latest) : 0;
    if (order > 0) return <p role="status" className="update-verdict newer"><b>Tu equipo tiene una versión más nueva ({shownVersion(current)}) que la publicada ({shownVersion(latest)}).</b> No se instalará la publicada.</p>;
    return <p role="status" className="update-verdict ok"><b>Ya estás al día.</b> Tienes la versión {shownVersion(current || latest)}, la más reciente publicada para tu licencia.</p>;

}
export function ClientUpdate() {

    const [open, setOpen] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState(''), [manifest, setManifest] = useState<any>(null);
    async function perform(fn: () => Promise<any>) { if (busy) return; setBusy(true); setError(''); try { await fn(); } catch (e: any) { setError(e.message); } finally { setBusy(false); } }
    const close = () => { setOpen(false); setManifest(null); setError(''); };
    return <><Button className="text-button" onClick={() => setOpen(true)}>Actualizar aplicación…</Button><Presence>{open && <DialogFrame label="Actualización de Culinary Restaurant" close={close} busy={busy}><div className="section-line"><h2>Actualización de la aplicación</h2><Button className="ghost" disabled={busy} onClick={close} aria-label="Cerrar actualización">×</Button></div><p>La sucursal busca la versión que Culinary publicó para tu licencia. Antes de instalar se comprueban la firma, la sucursal, la versión, el tamaño y la integridad del instalador. Cierra la sesión, todas las cuentas y la caja primero.</p>{error && <div className="alert error" role="alert">{error}</div>}
        {manifest?.update ? <><h3>Instalar versión {shownVersion(manifest.version)}</h3>{manifest.notes && <p>{manifest.notes}</p>}<p>Al confirmar se comprueban de nuevo el instalador y la autorización del servidor; la aplicación se cerrará para instalarla.</p><Button className="primary" disabled={busy} onClick={() => perform(() => native('updates.install'))}>Confirmar e instalar</Button></>
            : manifest ? <NoUpdate offer={manifest} />
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
    const [phase, setPhase] = useState('');
    const install = () => perform('Descargando y comprobando…', async () => { try { setPhase('downloading'); await native('updates.prepare'); setPhase('closing'); await native('updates.install'); setPhase('installing'); window.dispatchEvent(new Event('culinary-update-started')); } catch (e: any) { setPhase(''); if (!(await native('status')).authenticated) signedOut(e.message); else throw e; } });
    const others = offer?.otherSessions || 0;
    if (phase) return <UpdateScreen mode="local" progress={{ stage: phase, version: offer?.version }} />;
    return <DialogFrame label={'Culinary Restaurant ' + version} close={close} busy={!!busy}><div className="section-line"><h2>Culinary Restaurant {version}</h2><Button className="ghost" disabled={!!busy} onClick={close} aria-label="Cerrar versión">×</Button></div><p>Interfaz React y CSS · Transporte .NET de confianza. Los adaptadores externos permanecen aislados.</p>{error && <div className="alert error" role="alert">{error}</div>}
        {!manager ? <p>Un gerente puede buscar actualizaciones desde aquí.</p>
            : offer?.update ? <><h3>Versión {shownVersion(offer.version)} disponible</h3>{offer.notes && <p>{offer.notes}</p>}
                {offer.blocked ? <><div className="alert warn" role="status">{offer.blocked}</div><div className="dialog-actions"><Button disabled={!!busy} onClick={look}>{busy || 'Comprobar de nuevo'}</Button></div></>
                    : <><p>Al confirmar se descarga y se comprueba el instalador. Después se cierra tu sesión{others > 0 ? ' y la de ' + (others === 1 ? 'otro dispositivo' : others + ' dispositivos más') : ''}, y la aplicación se cierra para instalarla. Los borradores se conservan.</p><div className="dialog-actions"><Button className="primary" disabled={!!busy} onClick={install}>{busy || 'Actualizar ahora'}</Button></div></>}</>
                : <>{offer && <NoUpdate offer={offer} />}<div className="dialog-actions"><Button className="primary" disabled={!!busy} onClick={look}>{busy || 'Buscar actualización'}</Button></div></>}
    </DialogFrame>;

}
