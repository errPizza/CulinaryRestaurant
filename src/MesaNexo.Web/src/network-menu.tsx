import React, { useCallback, useEffect, useRef, useState } from 'react';
import { native } from './bridge';
import { UiIcon, Button } from './ui';
import { SecretInput } from './secret-input';

type Status = { available: boolean; kind?: 'ethernet' | 'wifi' | 'none'; name?: string; signal?: number | null; wifi?: boolean };
type Network = { ssid: string; signal: number; secure: boolean; active: boolean };
const strength = (signal: number) => signal >= 70 ? 'Señal excelente' : signal >= 45 ? 'Señal buena' : signal >= 25 ? 'Señal regular' : 'Señal débil';

// Wired or Wi-Fi state of the CulinaryOS kiosk and a picker for nearby networks. Renders nothing elsewhere.
export function NetworkMenu({ corner = false }: { corner?: boolean }) {

    const [status, setStatus] = useState<Status>({ available: false }), [open, setOpen] = useState(false);
    const [networks, setNetworks] = useState<Network[] | null>(null), [scanning, setScanning] = useState(false);
    const [chosen, setChosen] = useState<Network | null>(null), [password, setPassword] = useState('');
    const [connecting, setConnecting] = useState(''), [error, setError] = useState('');
    const root = useRef<HTMLDivElement>(null);
    const refresh = useCallback(() => native<Status>('network.status').then(setStatus).catch(() => {}), []);
    useEffect(() => { void refresh(); const timer = window.setInterval(refresh, 15000); return () => window.clearInterval(timer); }, [refresh]);
    const scan = useCallback(async () => {

        setScanning(true); setError('');
        try { setNetworks((await native<{ networks: Network[] }>('network.scan')).networks); }
        catch (e: any) { setError(String(e?.message || e)); }
        finally { setScanning(false); }

    }, []);
    useEffect(() => {

        if (!open) return;
        if (status.wifi) void scan();
        const outside = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false); };
        const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.preventDefault(); setOpen(false); root.current?.querySelector<HTMLElement>('.network-trigger')?.focus(); } };
        document.addEventListener('pointerdown', outside);
        document.addEventListener('keydown', escape);
        return () => { document.removeEventListener('pointerdown', outside); document.removeEventListener('keydown', escape); };

    }, [open, status.wifi, scan]);
    const connect = async (network: Network, secret = '') => {

        setConnecting(network.ssid); setError('');
        try { await native('network.connect', { ssid: network.ssid, password: secret }); setChosen(null); setPassword(''); await refresh(); await scan(); }
        catch (e: any) { setError(String(e?.message || e)); }
        finally { setConnecting(''); }

    };
    // Secured networks ask for the key first; open ones join at once.
    const pick = (network: Network) => { if (network.active || connecting) return; setError(''); if (network.secure) { setChosen(network); setPassword(''); } else void connect(network); };
    if (!status.available) return null;
    const icon = status.kind === 'ethernet' ? 'ethernet' : status.kind === 'wifi' ? 'wifi' : 'wifi-off';
    const label = status.kind === 'ethernet' ? 'Conectado por cable' : status.kind === 'wifi' ? 'Wi‑Fi · ' + status.name : 'Sin conexión de red';
    return <div className={'network-menu' + (corner ? ' corner' : '') + (status.kind === 'none' ? ' offline' : '')} ref={root}>
        <button type="button" className="network-trigger" aria-haspopup="dialog" aria-expanded={open} aria-label={'Red del equipo: ' + label} title={label} onClick={() => { setOpen(!open); setChosen(null); setError(''); }}><UiIcon name={icon} /></button>
        {open && <div className="menu-panel right network-panel" role="dialog" aria-label="Red del equipo">
            <div className="network-current"><span className="mi-ic"><UiIcon name={icon} /></span><span className="mi-t"><b>{label}</b><small>{status.kind === 'ethernet' ? status.name || 'Cable de red' : status.kind === 'wifi' ? (status.signal != null ? strength(status.signal) : 'Conectado') : 'Conecta un cable o elige una red Wi‑Fi'}</small></span></div>
            {status.wifi ? <>
                <div className="network-head"><h6>Redes Wi‑Fi cercanas</h6><Button type="button" className="text-button" icon="refresh" onClick={scan} disabled={scanning || !!connecting}>{scanning ? 'Buscando…' : 'Buscar de nuevo'}</Button></div>
                <div className="network-list" aria-busy={scanning}>
                    {networks?.map(network => <React.Fragment key={network.ssid}>
                        <button type="button" className={'menu-item network-item' + (network.active ? ' active' : '')} aria-pressed={network.active} disabled={!!connecting} onClick={() => pick(network)}>
                            <span className="network-bars" data-level={Math.min(4, Math.ceil(network.signal / 25))} aria-hidden="true"><i /><i /><i /><i /></span>
                            <span className="mi-t"><b>{network.ssid}</b><small>{connecting === network.ssid ? 'Conectando…' : network.active ? 'Conectado' : (network.secure ? 'Protegida · ' : 'Abierta · ') + strength(network.signal).toLowerCase()}</small></span>
                            {network.secure && <UiIcon name="lock" className="network-lock" />}
                        </button>
                        {chosen?.ssid === network.ssid && <form className="network-password" onSubmit={e => { e.preventDefault(); void connect(network, password); }}>
                            <SecretInput id="network-password" label={'Contraseña de ' + network.ssid} name="contraseña de la red" value={password} onChange={setPassword} required autoComplete="off" disabled={!!connecting} />
                            <div className="network-actions"><Button type="button" className="text-button" icon={false} onClick={() => setChosen(null)} disabled={!!connecting}>Cancelar</Button><Button type="submit" className="primary" icon="wifi" disabled={!!connecting || password.length < 8}>{connecting ? 'Conectando…' : 'Conectar'}</Button></div>
                        </form>}
                    </React.Fragment>)}
                    {networks && !networks.length && !scanning && <p className="network-empty">No se encontraron redes. Acércate al módem o busca de nuevo.</p>}
                    {!networks && scanning && <p className="network-empty">Buscando redes cercanas…</p>}
                </div>
            </> : <p className="network-empty">Este equipo no tiene Wi‑Fi. Usa un cable de red.</p>}
            {error && <p className="power-error" role="alert">{error}</p>}
        </div>}
    </div>;

}
