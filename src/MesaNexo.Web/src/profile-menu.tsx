import React, { useEffect, useRef, useState } from 'react';
import { UiIcon, Switch, Button } from './ui';
import { soundModes, getSoundMode, setSoundMode, playAllSounds, playSound, type SoundMode } from './sounds';

type Props = {
    name: string; role: string; scope: string; theme: 'light' | 'dark'; reduced: boolean; version: string; api: string; busy: boolean;
    onTheme: (theme: 'light' | 'dark') => void; onReduced: () => void; onVersion: () => void; onConnection: () => void; onLogout: () => void; onPin?: () => void; clockedIn?: boolean; onClock?: () => void
};

// Account menu in the top bar: appearance, motion, version, connection and sign-out.
export function ProfileMenu({ name, role, scope, theme, reduced, version, api, busy, onTheme, onReduced, onVersion, onConnection, onLogout, onPin, clockedIn, onClock }: Props) {

    const [open, setOpen] = useState(false);
    const root = useRef<HTMLDivElement>(null);
    useEffect(() => {

        if (!open) return;
        const outside = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false); };
        const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.preventDefault(); setOpen(false); root.current?.querySelector<HTMLElement>('.profile-trigger')?.focus(); } };
        document.addEventListener('pointerdown', outside);
        document.addEventListener('keydown', escape);
        return () => { document.removeEventListener('pointerdown', outside); document.removeEventListener('keydown', escape); };

    }, [open]);
    const choose = (fn: () => void) => () => { setOpen(false); fn(); };
    const [sound, setSound] = useState<SoundMode>(getSoundMode), [testing, setTesting] = useState('');
    const chooseSound = (mode: SoundMode) => { setSoundMode(mode); setSound(mode); if (mode !== 'silent') playSound('alert', mode); };
    return <div className="profile-menu profile" ref={root}>
        <button type="button" className="profile-trigger" aria-haspopup="menu" aria-expanded={open} aria-label={'Menú de ' + name} onClick={() => setOpen(!open)}><span className="avatar">{name.slice(0, 1)}</span><div><b>{name}</b><small>{role}</small></div><UiIcon name="down" className="nav-chevron" /></button>
        {open && <div className="menu-panel right" role="menu" aria-label={'Menú de ' + name} style={{ minWidth: 310 }}>
            <div className="profile-head"><span className="avatar">{name.slice(0, 1)}</span><span className="mi-t"><b>{name}</b><small>{role} · {scope}</small></span></div><hr />
            <div className="menu-row"><span className="mi-ic"><UiIcon name={theme === 'dark' ? 'moon' : 'sun'} /></span><span className="mi-t"><b>Apariencia</b></span><span className="theme-switch" role="group" aria-label="Apariencia"><button type="button" aria-pressed={theme === 'light'} onClick={() => onTheme('light')}><UiIcon name="sun" />Claro</button><button type="button" aria-pressed={theme === 'dark'} onClick={() => onTheme('dark')}><UiIcon name="moon" />Oscuro</button></span></div>
            <div className="menu-row"><span className="mi-ic"><UiIcon name="motion" /></span><span className="mi-t" style={{ flex: 1 }}><b>Reducir animaciones</b><small>Menos movimiento en pantalla</small></span><Switch checked={reduced} label="Reducir animaciones" hideLabel onChange={onReduced} disabled={busy} /></div>
            <div className="menu-row sound-row" style={{ flexWrap: 'wrap', rowGap: 8 }}><span className="mi-ic"><UiIcon name="bell" /></span><span className="mi-t" style={{ flex: 1 }}><b>Sonidos</b><small>{testing ? 'Sonando: ' + testing : 'Avisos de este equipo'}</small></span><Button className="text-button" icon={false} disabled={!!testing || sound === 'silent'} onClick={() => void playAllSounds(setTesting)}>Probar</Button><span className="theme-switch" role="group" aria-label="Sonidos" style={{ marginLeft: 0, width: '100%' }}>{soundModes().map(m => <button type="button" key={m.value} style={{ flex: 1, justifyContent: 'center' }} aria-pressed={sound === m.value} onClick={() => chooseSound(m.value)}>{m.label}</button>)}</span></div>
            <button type="button" role="menuitem" className="menu-item version-button" onClick={choose(onVersion)}><span className="mi-ic"><UiIcon name="cloud" /></span><span className="mi-t"><b>Versión {version}</b><small>Acerca de la aplicación</small></span></button>
            <button type="button" role="menuitem" className="menu-item" onClick={choose(onConnection)}><span className="mi-ic"><UiIcon name="wifi" /></span><span className="mi-t"><b>Conexión segura</b><small>{api || 'Sin configurar'}</small></span></button>
            {onClock && <button type="button" role="menuitem" className="menu-item" disabled={busy} onClick={choose(onClock)}><span className="mi-ic"><UiIcon name="clock" /></span><span className="mi-t"><b>{clockedIn ? 'Registrar salida' : 'Registrar entrada'}</b><small>{clockedIn ? 'Estás en turno' : 'Reloj checador'}</small></span></button>}
            {onPin && <button type="button" role="menuitem" className="menu-item" onClick={choose(onPin)}><span className="mi-ic"><UiIcon name="key" /></span><span className="mi-t"><b>Mi PIN de autorización</b><small>Para autorizar en el dispositivo de otro</small></span></button>}<hr />
            <button type="button" role="menuitem" className="menu-item danger" aria-label={'Cerrar sesión de ' + name} disabled={busy} onClick={choose(onLogout)}><span className="mi-ic"><UiIcon name="exit" /></span><span className="mi-t"><b>Cerrar sesión</b><small>Los borradores se conservan</small></span></button>
        </div>}
    </div>;

}
