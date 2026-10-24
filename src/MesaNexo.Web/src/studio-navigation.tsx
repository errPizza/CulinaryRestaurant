import React, { useEffect, useRef, useState } from 'react';

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
    const go = (key: string) => { setExpanded(null); onNavigate(key); };
    const quick = ([key, label, name]: string[]) => <button type="button" key={key} aria-label={label} title={label} className={page === key ? 'selected' : ''} aria-current={page === key ? 'page' : undefined} onClick={() => go(key)}>{icon(name)}<span>{label}</span>{key === 'notifications' && pending > 0 && <i key={pending}>{pending}</i>}</button>;
    const entry = ([key, label, name]: string[]) => <button type="button" key={key} aria-label={label} className={'nav-item ' + (page === key ? 'selected' : '')} aria-current={page === key ? 'page' : undefined} onClick={() => go(key)}><span className="mi-ic">{icon(name)}</span><span className="mi-t"><b>{label}</b>{descriptions[key] && <small>{descriptions[key]}</small>}</span>{counts[key] > 0 && <span className="mi-c" aria-hidden="true">{counts[key]}</span>}</button>;
    return <nav ref={root} aria-label="Navegación principal" className="studio-navigation">{available.map((group, index) => index === 0 ? <div className="nav-quick" key={group.id}>{group.items.map(quick)}</div> : <details className={'nav-section ' + (group.keys.includes(page) ? 'is-current' : '')} key={group.id} open={expanded === group.id}>
        <summary title={group.name} onClick={event => { event.preventDefault(); setExpanded(previous => previous === group.id ? null : group.id); }}>{icon(group.icon)}<span>{group.name}</span><svg className="nav-chevron" aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="m6 9 6 6 6-6" /></svg></summary>
        {group.sections && group.items.length > 4
            ? <div className="nav-section-items mega">{group.sections.map(([title, keys]) => { const rows = keys.map(key => group.items.find(item => item[0] === key)).filter((item): item is string[] => !!item); return rows.length ? <div className="nav-group" key={title}><h6>{title}</h6>{rows.map(entry)}</div> : null; })}</div>
            : <div className="nav-section-items">{group.items.map(entry)}</div>}
    </details>)}</nav>;

}
