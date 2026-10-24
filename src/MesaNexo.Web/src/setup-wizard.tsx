import React, { useState } from 'react';
import { native } from './bridge';
import { SecretInput } from './secret-input';
import { UiIcon as Icon, Button } from './ui';

// Setup mode: shown once on a fresh CulinaryOS while the local branch has no restaurant yet.
type Status = { needsSetup: boolean; codeAvailable: boolean };
type Props = { status: Status; onSignedIn: (result: any) => Promise<void>; onManual: (username: string, code: string) => void };
const steps = [['welcome', 'Bienvenida'], ['restaurant', 'Tu restaurante'], ['manager', 'Cuenta del gerente'], ['confirm', 'Confirmar']];
export function SetupWizard({ status, onSignedIn, onManual }: Props) {

    const [step, setStep] = useState(0), [busy, setBusy] = useState(false), [error, setError] = useState(''), [pending, setPending] = useState<any>(null);
    const [v, setV] = useState({ name: '', managerName: '', managerUsername: '', managerPassword: '', managerPasswordConfirm: '', setupCode: '' });
    const set = (key: keyof typeof v) => (value: string) => setV(current => ({ ...current, [key]: value }));
    function validate(index: number) {

        if (index === 1 && !v.name.trim()) return 'Escribe el nombre del restaurante.';
        if (index === 2) {

            if (!v.managerName.trim()) return 'Escribe el nombre del gerente.';
            if (!/^\S{3,100}$/.test(v.managerUsername.trim())) return 'El usuario debe tener al menos 3 caracteres y no llevar espacios.';
            if (v.managerPassword.length < 12) return 'La contraseña debe tener al menos 12 caracteres.';
            if (v.managerPassword !== v.managerPasswordConfirm) return 'Las contraseñas no coinciden.';

        }
        if (index === 3 && !status.codeAvailable && v.setupCode.trim().length < 32) return 'Escribe el código de configuración de este equipo.';
        return '';

    }
    function next(e: React.FormEvent) { e.preventDefault(); const problem = validate(step); setError(problem); if (!problem) setStep(step + 1); }
    async function finish(e: React.FormEvent) {

        e.preventDefault();
        const problem = validate(3);
        setError(problem);
        if (problem || busy) return;
        setBusy(true);
        try {

            const result = await native('setup.complete', { ...v, name: v.name.trim(), managerName: v.managerName.trim(), managerUsername: v.managerUsername.trim() });
            setV(current => ({ ...current, managerPassword: '', managerPasswordConfirm: '', setupCode: '' }));
            if (result.signedIn === false) setPending(result); else await onSignedIn(result);

        } catch (err: any) { setError(err.message); } finally { setBusy(false); }

    }
    const errorBar = error && <div className="alert error" role="alert"><span>{error}</span><Button onClick={() => setError('')} aria-label="Cerrar error">×</Button></div>;
    const back = step > 0 && !busy && <Button className="text-button" icon={false} type="button" onClick={() => { setError(''); setStep(step - 1); }}>← Volver</Button>;
    let form: React.ReactNode;
    if (pending) form = <><span className="eyebrow">RESTAURANTE CREADO</span><h2>Guarda tu código de alta</h2><p>No pudimos abrir la sesión automáticamente. Usa este código una sola vez para autorizar este equipo con la cuenta <b>{pending.username}</b>.</p><code className="setup-code" aria-label="Código de alta del dispositivo">{pending.enrollmentCode}</code><Button className="primary wide" icon="arrow" onClick={() => onManual(pending.username, pending.enrollmentCode)}>Ir al inicio de sesión <span>→</span></Button></>;
    else if (step === 0) form = <form onSubmit={next}><span className="eyebrow">MODO SETUP</span><h2>Configura tu restaurante</h2><p>Este equipo ya tiene CulinaryOS y Culinary Restaurant instalados. En tres pasos crearás el restaurante y la cuenta del gerente. Funciona sin Internet.</p><Button className="primary wide" icon="arrow">Comenzar <span>→</span></Button></form>;
    else if (step === 1) form = <form onSubmit={next}><span className="eyebrow">PASO 1 DE 3</span><h2>Tu restaurante</h2><p>El nombre aparece en la operación y en los tickets. Puedes cambiarlo después en Configuración.</p>{errorBar}<label>Nombre del restaurante<input value={v.name} onChange={e => set('name')(e.target.value)} maxLength={150} required autoFocus autoComplete="organization" /></label><Button className="primary wide" icon="arrow">Continuar <span>→</span></Button>{back}</form>;
    else if (step === 2) form = <form onSubmit={next}><span className="eyebrow">PASO 2 DE 3</span><h2>Cuenta del gerente</h2><p>El gerente administra el equipo, el menú y la caja. Usa una contraseña propia de al menos 12 caracteres.</p>{errorBar}<label>Nombre completo<input value={v.managerName} onChange={e => set('managerName')(e.target.value)} maxLength={100} required autoFocus autoComplete="name" /></label><label>Usuario<input value={v.managerUsername} onChange={e => set('managerUsername')(e.target.value)} maxLength={100} required autoComplete="username" spellCheck={false} autoCapitalize="none" /></label><SecretInput id="setup-password" label="Contraseña" name="contraseña" value={v.managerPassword} onChange={set('managerPassword')} required autoComplete="new-password" /><SecretInput id="setup-password-confirm" label="Repite la contraseña" name="confirmación de contraseña" value={v.managerPasswordConfirm} onChange={set('managerPasswordConfirm')} required autoComplete="new-password" /><Button className="primary wide" icon="arrow">Continuar <span>→</span></Button>{back}</form>;
    else form = <form onSubmit={finish}><span className="eyebrow">PASO 3 DE 3</span><h2>Confirma y crea</h2><p>Se creará la sucursal local con estos datos y entrarás directamente a la operación.</p>{errorBar}<dl className="setup-summary"><div><dt>Restaurante</dt><dd>{v.name.trim()}</dd></div><div><dt>Gerente</dt><dd>{v.managerName.trim()}</dd></div><div><dt>Usuario</dt><dd>{v.managerUsername.trim()}</dd></div></dl>{!status.codeAvailable && <SecretInput id="setup-code" label="Código de configuración del equipo" name="código de configuración" value={v.setupCode} onChange={set('setupCode')} required disabled={busy} />}<Button className="primary wide" icon="arrow" disabled={busy}>{busy ? 'Creando restaurante…' : 'Crear restaurante y entrar'} <span>→</span></Button>{back}</form>;
    return <div className="login setup-mode"><section className="login-story"><div className="brand"><img className="brand-symbol" src="./branding/symbol.png" alt="" />Culinary Restaurant</div><div><span className="eyebrow">PRIMER INICIO</span><h1>Prepara tu restaurante.<br />Empieza a operar hoy.</h1><p>Todo queda guardado en este equipo,<br />cifrado y bajo tu control.</p><ol className="setup-steps">{steps.map(([key, title], i) => <li key={key} className={pending || i < step ? 'done' : i === step ? 'current' : ''} aria-current={!pending && i === step ? 'step' : undefined}><span className="setup-step-mark">{pending || i < step ? <Icon name="check" /> : i + 1}</span>{title}</li>)}</ol></div><small>Operación local · Conexión HTTPS de confianza</small></section><section className="login-form"><div className="login-box setup-box">{form}</div></section></div>;

}
