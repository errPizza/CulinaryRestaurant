import React, { useState } from 'react';
import { Button } from './ui';
import { native } from './bridge';
type License = { state: string; plan?: string; expiresAt?: string | null; validatedAt?: string; customer?: string; branchName?: string };
const PLANS: Record<string, string> = { trial: 'Prueba', monthly: 'Mensual', annual: 'Anual', permanent: 'Permanente' };
const day = (v?: string | null) => v ? new Date(v).toLocaleDateString('es-MX', { day: 'numeric', month: 'long', year: 'numeric' }) : '';
// Same wording as the native lock screen (LicenseScreen.cs).
function copy(l: License): [string, string] {

    switch (l.state) {
        case 'expired': return l.plan === 'trial' ? ['Tu periodo de prueba terminó', 'Tus datos siguen guardados en este equipo. Para entrar de nuevo al panel de administración y a la operación, contrata un plan con tu proveedor. Al activarlo, el acceso vuelve solo.'] : ['Tu licencia venció', 'Tus datos siguen guardados en este equipo. Para entrar de nuevo al panel de administración y a la operación, contrata otra vez tu plan con tu proveedor. Al renovarlo, el acceso vuelve solo.'];
        case 'revoked': return ['Tu licencia fue cancelada', 'Tus datos siguen guardados en este equipo. Comunícate con tu proveedor para contratar una licencia nueva y activarla aquí.'];
        case 'offline': return ['No pudimos validar tu licencia', 'Culinary Restaurant necesita conectarse a internet al menos una vez cada 3 días. Revisa la conexión del servidor de la sucursal y reintenta.'];
        case 'clock': return ['La fecha del equipo está atrasada', 'Corrige la fecha y la hora del servidor de la sucursal y reintenta.'];
        case 'mismatch': return ['Esta licencia es de otro equipo', 'Pide a tu proveedor que libere la licencia y actívala de nuevo en este servidor.'];
        case 'missing': return ['Activa Culinary Restaurant', 'Escribe el identificador de licencia y el código de activación que te dio tu proveedor.'];
        default: return ['No se pudo consultar la licencia', 'Revisa la conexión con el servidor de la sucursal y reintenta.'];
    }

}
// Replaces the whole application while the branch server answers 423. It holds no business data.
export function LicenseLock({ license, onRetry }: { license: License; onRetry: () => Promise<void> }) {

    const [title, body] = copy(license), [busy, setBusy] = useState(false), [error, setError] = useState(''), [activating, setActivating] = useState(license.state === 'missing');
    const [licenseId, setLicenseId] = useState(''), [activationCode, setActivationCode] = useState('');
    async function perform(fn: () => Promise<any>) { if (busy) return; setBusy(true); setError(''); try { await fn(); } catch (e: any) { setError(e.message); } finally { setBusy(false); } }
    function activate(e: React.FormEvent) { e.preventDefault(); void perform(async () => { await native('license.activate', { licenseId: licenseId.trim(), activationCode: activationCode.trim() }); setActivationCode(''); await onRetry(); }); }
    const expired = license.state === 'expired';
    return <div className="login license-lock">
        <section className="login-story"><div className="brand"><span className="brand-mark"><img className="brand-symbol" src="./branding/symbol.png" alt="" /></span><div><b>Culinary Restaurant</b><small><i />Licencia de uso</small></div></div><div><span className="eyebrow">{license.customer || 'Tu restaurante'}{license.branchName ? ' · ' + license.branchName : ''}</span><h1>{title}</h1></div><small>Ningún dato del restaurante sale de este equipo mientras la licencia no esté vigente.</small></section>
        <section className="login-form"><div className="login-box" role="alert" aria-live="assertive">
            <div><h2>{title}</h2>{license.plan && <p className="license-plan"><b>Plan {PLANS[license.plan] || license.plan}</b>{license.expiresAt ? (expired ? ' · venció el ' : ' · vence el ') + day(license.expiresAt) : ''}</p>}<p style={{ marginTop: 8, color: 'var(--muted)' }}>{body}</p></div>
            {error && <div className="alert error" role="alert">{error}</div>}
            {activating ? <form onSubmit={activate} className="license-activate"><label>Identificador de licencia<input value={licenseId} onChange={e => setLicenseId(e.target.value)} required autoComplete="off" spellCheck={false} placeholder="0b6c5c1e-3f0a-4d39-9f5e-1d2a8f3c7b10" /></label><label>Código de activación<input value={activationCode} onChange={e => setActivationCode(e.target.value)} required autoComplete="off" spellCheck={false} placeholder="K7Q2M-9XWZT-4HPLA-R3VNE" /></label><Button className="primary wide lg" icon="key" disabled={busy}>{busy ? 'Activando…' : 'Activar licencia'}</Button></form>
                : <Button className="primary wide lg" icon="refresh" disabled={busy} onClick={() => perform(onRetry)}>{busy ? 'Comprobando…' : 'Reintentar'}</Button>}
            <div className="login-links">{activating ? license.state !== 'missing' && <Button className="text-button" icon="arrow" onClick={() => setActivating(false)}>Volver</Button> : <Button className="text-button" icon="key" onClick={() => setActivating(true)}>Activar otra licencia</Button>}</div>
        </div></section>
    </div>;

}
