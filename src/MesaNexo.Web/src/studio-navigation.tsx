import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { motionReduced, hold } from './motion';

type Group = { id: string; name: string; icon: string; keys: string[]; sections?: [string, string[]][] };
const restaurantGroups: Group[] = [
    { id: 'service', name: 'Servicio', icon: 'grid', keys: ['overview', 'tables', 'counter', 'notifications'] },
    {
        id: 'management', name: 'Administración', icon: 'sliders', keys: ['kitchen', 'history', 'cash', 'delivery', 'catalog', 'inventory', 'purchases', 'promotions', 'expenses', 'reports', 'team', 'fiscal', 'printing'],
        sections: [['Operación del turno', ['kitchen', 'history', 'cash', 'delivery']], ['Menú e insumos', ['catalog', 'inventory', 'purchases', 'promotions']], ['Control y equipo', ['expenses', 'reports', 'team', 'fiscal', 'printing']]]
    },
    { id: 'restaurant', name: 'Restaurante', icon: 'store', keys: ['reservations', 'reviews', 'loyalty', 'integrations', 'settings', 'support'] },
];
const preparationGroups: Group[] = [{ id: 'preparation', name: 'Preparación', icon: 'flame', keys: ['kitchen', 'history'] }];
const descriptions: Record<string, string> = { kitchen: 'Comandas por estación', history: 'Tiempos por comanda', cash: 'Pagos, movimientos y arqueo', delivery: 'Pedidos para llevar y reparto', catalog: 'Productos y categorías', inventory: 'Existencias, recetas y conteos', purchases: 'Órdenes, proveedores y por pagar', loyalty: 'Monedero, cashback y clientes', integrations: 'Proveedores simulados, listos para activar', expenses: 'Cuentas de gasto y caja chica', promotions: 'Horarios y combos', reports: 'Resultados por periodo', team: 'Permisos y sesiones', fiscal: 'Solicitudes CFDI', printing: 'Trabajos y conciliación', reservations: 'Agenda y solicitudes', reviews: 'Opiniones privadas', settings: 'Identidad, mesas y reglas', support: 'Solicitudes y permisos', backups: 'Copias cifradas del central', audit: 'Operaciones del proveedor' };
function groups(role: string) { if (role === 'dueno') return [{ id: 'analytics', name: 'Analítica', icon: 'chart', keys: ['analytics'] }]; if (role === 'cajero') return [{ id: 'cashier', name: 'Caja', icon: 'wallet', keys: ['blind'] }]; return role === 'cocina' ? preparationGroups : restaurantGroups; }
export function navigationGroup(role: string, page: string) { if (role === 'dueno') return 'Dirección'; if (role === 'cajero') return 'Caja'; return groups(role).find(group => group.keys.includes(page))?.name || 'Servicio'; }

export function StudioNavigation({ items, page, role, pending, counts = {}, onNavigate, icon }: {
    items: string[][]; page: string; role: string; pending: number; counts?: Record<string, number>; onNavigate: (page: string) => void; icon: (name: string) => React.ReactNode;
}) {

    const available = groups(role).map(group => ({ ...group, items: items.filter(item => group.keys.includes(item[0])) })).filter(group => group.items.length);
    const [expanded, setExpanded] = useState<string | null>(null);
    const root = useRef<HTMLElement>(null);
    useEffect(() => {

        if (!expanded) return;
        const outside = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node)) setExpanded(null); };
        const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.preventDefault(); root.current?.querySelector<HTMLElement>('details[open] summary')?.focus(); setExpanded(null); } };
        document.addEventListener('pointerdown', outside);
        document.addEventListener('keydown', escape);
        return () => { document.removeEventListener('pointerdown', outside); document.removeEventListener('keydown', escape); };

    }, [expanded]);
    // The tab answers at once; the next page is built two frames later, with the slide already under way.
    const [chosen, setChosen] = useState(page), leaving = useRef(0);
    useEffect(() => setChosen(page), [page]);
    useEffect(() => () => cancelAnimationFrame(leaving.current), []);
    // One pill slides between the tabs, moving only its transform so the compositor keeps it running while
    // React works. Offsets, not rectangles: the pressed button is mid-scale when this runs.
    const pill = useRef<HTMLSpanElement>(null), spot = useRef('');
    const place = (animate: boolean) => {

        const nav = root.current!, marker = pill.current!, target = nav.querySelector<HTMLElement>('.nav-quick>button.selected,.nav-section.is-current>summary');
        marker.style.opacity = target ? '1' : '0';
        if (!target) { spot.current = ''; return; }
        let x = 0, y = 0;
        for (let element: HTMLElement | null = target; element && element !== nav; element = element.offsetParent as HTMLElement | null) { x += element.offsetLeft; y += element.offsetTop; }
        const width = target.offsetWidth, height = target.offsetHeight, next = [x, y, width, height].join();
        // The observer below also fires for the resize this very move can cause: restarting would cut the slide.
        if (next === spot.current) return;
        const seen = animate && spot.current && !motionReduced() ? marker.getBoundingClientRect() : null, frame = nav.getBoundingClientRect();
        spot.current = next;
        marker.style.transform = `translate(${x}px,${y}px)`;
        marker.style.width = width + 'px';
        marker.style.height = height + 'px';
        if (seen) hold(marker.animate({ transform: [`translate(${seen.left - frame.left}px,${seen.top - frame.top}px) scale(${seen.width / width},${seen.height / height})`, marker.style.transform], easing: 'cubic-bezier(.22,1,.36,1)' }, { duration: 340 }));

    };
    useLayoutEffect(() => place(true), [chosen, items.length, role]);
    useEffect(() => { const observer = new ResizeObserver(() => place(false)); observer.observe(root.current!); return () => observer.disconnect(); }, []);
    const go = (key: string) => { setExpanded(null); setChosen(key); cancelAnimationFrame(leaving.current); leaving.current = requestAnimationFrame(() => { leaving.current = requestAnimationFrame(() => onNavigate(key)); }); };
    const quick = ([key, label, name]: string[]) => <button type="button" key={key} aria-label={label} className={chosen === key ? 'selected' : ''} aria-current={chosen === key ? 'page' : undefined} onClick={() => go(key)}>{icon(name)}<span>{label}</span>{key === 'notifications' && pending > 0 && <i key={pending}>{pending}</i>}</button>;
    const entry = ([key, label, name]: string[]) => <button type="button" key={key} aria-label={label} className={'nav-item ' + (chosen === key ? 'selected' : '')} aria-current={chosen === key ? 'page' : undefined} onClick={() => go(key)}><span className="mi-ic">{icon(name)}</span><span className="mi-t"><b>{label}</b>{descriptions[key] && <small>{descriptions[key]}</small>}</span>{counts[key] > 0 && <span className="mi-c" aria-hidden="true">{counts[key]}</span>}</button>;
    return <nav ref={root} aria-label="Navegación principal" className="studio-navigation"><span ref={pill} className="nav-pill" aria-hidden="true" />{available.map((group, index) => index === 0 ? <div className="nav-quick" key={group.id}>{group.items.map(quick)}</div> : <details className={'nav-section ' + (group.keys.includes(chosen) ? 'is-current' : '')} key={group.id} open={expanded === group.id}>
        <summary onClick={event => { event.preventDefault(); setExpanded(previous => previous === group.id ? null : group.id); }}>{icon(group.icon)}<span>{group.name}</span><svg className="nav-chevron" aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="m6 9 6 6 6-6" /></svg></summary>
        {group.sections && group.items.length > 4
            ? <div className="nav-section-items mega">{group.sections.map(([title, keys]) => { const rows = keys.map(key => group.items.find(item => item[0] === key)).filter((item): item is string[] => !!item); return rows.length ? <div className="nav-group" key={title}><h6>{title}</h6>{rows.map(entry)}</div> : null; })}</div>
            : <div className="nav-section-items">{group.items.map(entry)}</div>}
    </details>)}</nav>;

}
