import React, { useEffect, useRef, useState } from 'react';
import { native } from './bridge';
import { UiIcon } from './ui';

type Action = 'suspend' | 'reboot' | 'poweroff';
const actions: [Action, string, string, string][] = [
    ['suspend', 'Suspender', 'moon', 'Despierta con una tecla o el botón de encendido'],
    ['reboot', 'Reiniciar', 'refresh', 'Cierra todo y vuelve a encender el equipo'],
    ['poweroff', 'Apagar', 'power', 'Apaga el equipo de forma ordenada'],
];

// Power controls of the CulinaryOS kiosk. Renders nothing on Windows or outside the appliance.
export function PowerMenu({ corner = false }: { corner?: boolean }) {

    const [available, setAvailable] = useState(false), [open, setOpen] = useState(false), [confirm, setConfirm] = useState<Action | null>(null);
    const [busy, setBusy] = useState(false), [error, setError] = useState('');
    const root = useRef<HTMLDivElement>(null);
    useEffect(() => { native('power.status').then(r => setAvailable(!!r?.available)).catch(() => {}); }, []);
    useEffect(() => {

        if (!open) return;
        const outside = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false); };
        const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.preventDefault(); setOpen(false); root.current?.querySelector<HTMLElement>('.power-trigger')?.focus(); } };
        document.addEventListener('pointerdown', outside);
        document.addEventListener('keydown', escape);
        return () => { document.removeEventListener('pointerdown', outside); document.removeEventListener('keydown', escape); };

    }, [open]);
    // Restart and power-off ask for a second tap; suspend loses nothing.
    const request = async (action: Action) => {

        if (action !== 'suspend' && confirm !== action) { setConfirm(action); return; }
        setBusy(true); setError('');
        try { await native('power.request', { action }); setOpen(false); setConfirm(null); }
        catch (e: any) { setError(String(e?.message || e)); }
        finally { setBusy(false); }

    };
    if (!available) return null;
    return <div className={'power-menu' + (corner ? ' corner' : '')} ref={root}>
        <button type="button" className="power-trigger" aria-haspopup="menu" aria-expanded={open} aria-label="Energía del equipo" title="Energía del equipo" onClick={() => { setOpen(!open); setConfirm(null); setError(''); }}><UiIcon name="power" /></button>
        {open && <div className="menu-panel right" role="menu" aria-label="Energía del equipo">
            <h6>Energía del equipo</h6>
            {actions.map(([key, label, icon, detail]) => <button type="button" role="menuitem" key={key} className={'menu-item' + (key === 'poweroff' ? ' danger' : '')} disabled={busy} onClick={() => request(key)}><span className="mi-ic"><UiIcon name={icon} /></span><span className="mi-t"><b>{confirm === key ? label + ' ahora' : label}</b><small>{confirm === key ? 'Toca otra vez para confirmar' : detail}</small></span></button>)}
            {error && <p className="power-error" role="alert">{error}</p>}
        </div>}
    </div>;

}
