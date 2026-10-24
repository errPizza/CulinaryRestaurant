import React from 'react';
import { UiIcon, Button, MetricCard, Kpis, PanelHeading, StatusBadge, Pill, Sq, Empty, Bar } from './ui';
import { AttentionQueue } from './service-board';
import { servicePriorities } from './service-priorities';
import { visitLabel } from './visit-label';
type Row = { id: string;[key: string]: any };
const money = (n: number) => new Intl.NumberFormat('es-MX', { style: 'currency', currency: 'MXN' }).format((n || 0) / 100);
const clock = (at: string) => at ? new Date(at).toLocaleTimeString('es-MX', { hour: '2-digit', minute: '2-digit', hour12: false }) : '—';
const minutes = (at: string) => Number.isFinite(Date.parse(at)) ? Math.max(0, Math.floor((Date.now() - Date.parse(at)) / 60000)) : 0;
const sameDay = (at: string, day: string) => !!at && new Date(at).toDateString() === day;
export function OverviewPage({ state: s, branch, connected, onNavigate, onOpen, authorizations, navigation }: { state: any; branch: string; connected: boolean; onNavigate: (key: string) => void; onOpen: (visit: Row) => void; authorizations: React.ReactNode; navigation: string[][] }) {

    const tables: Row[] = (s.tables || []).filter((t: Row) => t.branchId === branch), visits: Row[] = (s.visits || []).filter((v: Row) => v.branchId === branch && v.status === 'open');
    const units: Row[] = (s.units || []).filter((u: Row) => visits.some(v => v.id === u.visitId));
    const priorities = servicePriorities(visits.filter(v => s.user.role === 'manager' || v.waiterId === s.user.id), units, s.notifications || [], s.restaurant.settings.delayMinutes);
    const waiter = (v: Row) => (s.users || []).find((u: Row) => u.id === v.waiterId)?.name || 'Mesero';
    const table = (v: Row) => visitLabel(s, v);
    const today = new Date().toDateString(), canSee = s.user.role === 'manager' || s.user.permissions?.shiftReports;
    const payments: Row[] = (s.payments || []).filter((p: Row) => sameDay(p.at, today));
    const closedToday = (s.visits || []).filter((v: Row) => v.branchId === branch && v.status === 'closed' && sameDay(v.closedAt, today));
    const collected = payments.reduce((n, p) => n + p.amount, 0);
    const ready = units.filter(u => u.status === 'ready');
    const people = visits.reduce((n, v) => n + (v.people || 0), 0);
    const delay = s.restaurant.settings.delayMinutes || 15;
    // Every open command still being prepared, slowest first.
    const kitchen = (s.commands || []).map((c: Row) => { const cu = units.filter(u => c.unitIds?.includes(u.id)); const visit = visits.find(v => v.id === cu[0]?.visitId); return { c, cu, visit, active: cu.filter(u => ['received', 'preparing', 'ready'].includes(u.status)) }; }).filter((k: any) => k.visit && k.active.length).sort((a: any, b: any) => Date.parse(a.c.at) - Date.parse(b.c.at)).slice(0, 4);
    const shortcuts = [['cash', 'Caja del turno', 'wallet'], ['inventory', 'Inventario', 'box'], ['team', 'Equipo', 'users'], ['support', 'Soporte', 'help']].filter(([key]) => navigation.some(n => n[0] === key));
    return <>
        {!connected && <div className="notice warn ui-service-notice is-offline"><Sq icon="alert" /><span><strong>Conexión pendiente.</strong> Los borradores se conservan mientras se recupera el servidor.</span></div>}
        <Kpis>
            <MetricCard className="lead" label="Cobrado hoy" value={canSee ? money(collected) : 'Sin permiso'} note={canSee ? closedToday.length + ' cuentas cerradas' + (closedToday.length ? ' · ticket promedio ' + money(Math.round(collected / closedToday.length)) : '') : 'Consulta con gerencia'} icon="wallet" tone="amber" />
            <MetricCard label="Mesas ocupadas" value={<>{tables.filter(t => t.status === 'occupied').length} <small>/ {tables.length}</small></>} note={people + ' comensales sentados'} icon="table" tone="blue" />
            <MetricCard label="En mesas abiertas" value={money(visits.reduce((n, v) => n + (v.balance || 0), 0))} note={visits.length + ' cuenta' + (visits.length === 1 ? '' : 's') + ' sin cobrar'} icon="receipt" tone="purple" />
            <MetricCard label="Listos para recoger" value={ready.length} note={ready.length ? [...new Set(ready.map(u => table(visits.find(v => v.id === u.visitId) || { id: '' })))].join(', ') : 'Nada pendiente de entrega'} icon="chef" tone="green" />
        </Kpis>
        <div className="g g-main overview-panels">
            <AttentionQueue priorities={priorities} visits={visits} tables={tables} onOpen={onOpen} limit={5} nameOf={table} />
            <section className="card panel"><PanelHeading title="Cocina ahora" note="Minutos desde que se recibió" icon="flame" action={<Pill>Objetivo {delay} min</Pill>} />
                {kitchen.length ? <div className="rows stagger">{kitchen.map(({ c, cu, visit, active }: any) => { const m = minutes(c.at), done = active.every((u: Row) => u.status === 'ready'); return <div className="r" key={c.id}><div className="w" style={{ flex: '0 0 150px' }}><b>{table(visit)}</b><span>{cu.length} plato{cu.length === 1 ? '' : 's'} · {done ? 'listo' : cu[0]?.station || 'Cocina'}</span></div><div style={{ flex: 1 }}><Bar value={Math.min(m, delay * 4 / 3)} max={delay * 4 / 3} marker={delay} tone={m >= delay ? 'r' : m < delay / 2 ? 'g' : ''} /></div><span className="amt num" style={{ width: 56, color: m >= delay ? 'var(--red)' : undefined }}>{m} min</span></div>; })}</div>
                    : <Empty icon="flame" title="Cocina lista para el servicio" text="Las comandas confirmadas aparecerán aquí." />}
            </section>
        </div>
        <div className="g g2">
            <section className="card panel"><PanelHeading title="Mesas en servicio" note="Toca para abrir la cuenta" icon="table" action={<Button className="text-button" icon="arrow" onClick={() => onNavigate('tables')}>Ver todas</Button>} />
                {visits.length ? <div className="rows">{visits.map(v => { const signal = priorities.find(p => p.visitId === v.id); return <button type="button" className="r service-row" key={v.id} onClick={() => onOpen(v)} style={{ width: '100%', textAlign: 'left' }}><Sq icon="table" tone={signal?.kind === 'ready' ? 'green' : signal ? 'amber' : 'blue'} /><span className="w"><b>{table(v)}{tables.find(t => t.id === v.tableId)?.zone ? ' · ' + tables.find(t => t.id === v.tableId)?.zone : ''}</b><span>{v.people} comensales · {waiter(v)} · abierta {clock(v.openedAt)}</span></span><StatusBadge value={signal?.kind === 'ready' ? 'ready' : signal?.kind === 'bill' ? 'bill' : signal ? 'pending' : 'occupied'} label={signal?.kind === 'ready' ? 'Listo para entregar' : signal?.label || 'En servicio'} /><span className="amt num">{money(v.balance)}</span></button>; })}</div>
                    : <Empty icon="table" title="Todo listo para recibir" text="Abre una mesa para iniciar la primera cuenta." action={<Button className="primary sm" icon="table" onClick={() => onNavigate('tables')}>Ir a mesas</Button>} />}
            </section>
            {s.user.role === 'manager' ? <section className="card panel"><PanelHeading title="Solicitudes de autorización" note="Revisa el motivo antes de decidir" icon="shield" action={<Pill tone={(s.requests || []).some((r: Row) => r.status === 'pending') ? 'amber' : 'green'}>{(s.requests || []).filter((r: Row) => r.status === 'pending').length} pendiente{(s.requests || []).filter((r: Row) => r.status === 'pending').length === 1 ? '' : 's'}</Pill>} /><div className="tiles">{authorizations}</div><p style={{ margin: '12px 2px 0', color: 'var(--faint)', fontSize: 12 }}>El descuento máximo permitido es {s.restaurant.settings.discountLimit}%. Las decisiones quedan en el historial.</p></section>
                : <section className="card panel"><PanelHeading title="Tu turno" note="Cuentas a tu cargo" icon="users" /><div className="rows"><div className="r"><span className="w"><b>Cuentas abiertas</b></span><span className="amt num">{visits.filter(v => v.waiterId === s.user.id).length}</span></div><div className="r"><span className="w"><b>Avisos por atender</b></span><span className="amt num">{priorities.length}</span></div></div></section>}
        </div>
        {shortcuts.length > 0 && <section className="card panel" style={{ padding: '18px 24px' }}><div style={{ display: 'flex', alignItems: 'center', gap: 22, flexWrap: 'wrap' }}><div className="ph"><span className="ic"><UiIcon name="grid" /></span><div><h3>Accesos del turno</h3><p>Herramientas de tu cuenta</p></div></div><div className="ecosystem-actions ui-shortcuts" style={{ display: 'flex', gap: 10, marginLeft: 'auto', flexWrap: 'wrap' }}>{shortcuts.map(([key, name, icon]) => <Button className="ghost" icon={icon} key={key} onClick={() => onNavigate(key)}>{name}</Button>)}</div></div></section>}
    </>;

}
