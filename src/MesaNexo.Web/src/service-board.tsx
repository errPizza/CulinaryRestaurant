import React, { useState } from 'react';
import { UiIcon, Button, StatusBadge, PanelHeading, Pill, Sq, Empty } from './ui';
import { ContextMenu } from './context-menu';
import { PreparationNotes } from './preparation-notes';
import { servicePriorities, ServicePriority } from './service-priorities';

type Entity = { id: string;[key: string]: any };
type Props = {
    tables: Entity[]; visits: Entity[]; units: Entity[]; notifications: Entity[]; users: Entity[]; reservations: Entity[]; delayMinutes: number; userId: string; manager: boolean;
    onOpen: (visit: Entity) => void; onStart: (table: Entity) => void; onClean: (table: Entity) => void; onMove: (visit: Entity) => void; onTransfer: (visit: Entity) => void; onAdd?: (visit: Entity) => void; nameOf?: (visit: Entity) => string; onReservations?: () => void;
};
const money = (amount: number) => new Intl.NumberFormat('es-MX', { style: 'currency', currency: 'MXN' }).format((amount || 0) / 100);
const clock = (at: string) => at ? new Date(at).toLocaleTimeString('es-MX', { hour: '2-digit', minute: '2-digit', hour12: false }) : '—';
const minutes = (at: string) => Number.isFinite(Date.parse(at)) ? Math.max(0, Math.floor((Date.now() - Date.parse(at)) / 60000)) : 0;
const reservationTime = (at: string) => at ? new Date(at).toLocaleString('es-MX', { dateStyle: 'short', timeStyle: 'short' }) : 'Horario por confirmar';
const statuses: Record<string, string> = { free: 'Disponible', occupied: 'En servicio', reserved: 'Reservada', cleaning: 'Por limpiar' };
const priorityClass = (kind: string) => ({ delayed: 'delayed', unavailable: 'unavailable', bill: 'bill', attention: 'pending', ready: 'ready' }[kind] || 'occupied');
const priorityLook: Record<string, [string, string, string]> = { ready: ['green', 'chef', 'Entregar'], delayed: ['red', 'alert', 'Ver comanda'], unavailable: ['red', 'alert', 'Ver cuenta'], bill: ['blue', 'receipt', 'Cobrar'], attention: ['amber', 'bell', 'Atender'] };

export function AttentionQueue({ priorities, visits, tables, onOpen, limit, nameOf }: { priorities: ServicePriority[]; visits: Entity[]; tables: Entity[]; onOpen: (visit: Entity) => void; limit?: number; nameOf?: (visit: Entity) => string }) {

    const shown = limit ? priorities.slice(0, limit) : priorities;
    return <section className="card panel attention-queue" aria-label="Atender ahora"><PanelHeading title="Requiere tu atención" note="Ordenado por urgencia" icon="bell" action={<Pill tone={priorities.length ? 'amber' : 'green'}>{priorities.length + ' pendiente' + (priorities.length === 1 ? '' : 's')}</Pill>} />
        <div className="tiles attention-list stagger">{shown.length ? shown.map(priority => {

            const visit = visits.find(v => v.id === priority.visitId);
            if (!visit) return null;
            const [tone, icon, action] = priorityLook[priority.kind] || ['blue', 'bell', 'Ver cuenta'];
            return <div className={'r attention-row ' + (priority.kind === 'delayed' || priority.kind === 'unavailable' ? 'urgent' : priority.kind === 'bill' ? 'warn' : '')} key={priority.id}>
                <Sq icon={icon} tone={tone} /><div className="w"><b>{nameOf ? nameOf(visit) : tables.find(t => t.id === visit.tableId)?.name || 'Servicio rápido'} · {priority.label}</b><span>{priority.detail}{priority.kind === 'bill' ? ' · ' + money(visit.balance) : ''}</span></div>
                <Button type="button" className={(priority.kind === 'bill' ? 'primary' : 'ghost') + ' sm'} icon={priority.kind === 'bill' ? 'wallet' : priority.kind === 'ready' ? 'check' : 'eye'} onClick={() => onOpen(visit)}>{action}</Button>
            </div>;

        }) : <div className="attention-clear empty"><Sq icon="check" tone="green" /><h3>Servicio al día</h3><p>Aquí aparecerán las entregas listas, los retrasos y las solicitudes de atención.</p></div>}</div>
    </section>;

}

export function ServiceBoard({ tables, visits, units, notifications, users, reservations, delayMinutes, userId, manager, onOpen, onStart, onClean, onMove, onTransfer, onAdd, onReservations, nameOf }: Props) {

    const zones = [...new Set(tables.map(t => t.zone || ''))];
    const sortedTables = [...tables].sort((a, b) => zones.indexOf(a.zone || '') - zones.indexOf(b.zone || '') || a.name.localeCompare(b.name, 'es', { numeric: true }));
    const open = visits.filter(v => v.status === 'open');
    const [selectedId, setSelectedId] = useState(() => open.find(v => manager || v.waiterId === userId)?.tableId || sortedTables[0]?.id || '');
    const openAccount = (visit: Entity) => { setSelectedId(visit.tableId); onOpen(visit); };
    const startTable = (table: Entity) => { setSelectedId(table.id); onStart(table); };
    const [filter, setFilter] = useState('all'), [search, setSearch] = useState('');
    const isReady = (table: Entity) => units.some(unit => unit.status === 'ready' && open.some(v => v.tableId === table.id && v.id === unit.visitId));
    const filtered = sortedTables.filter(table => (filter === 'all' || (filter === 'ready' ? isReady(table) : table.status === filter)) && (table.name + ' ' + (table.zone || '')).toLowerCase().includes(search.toLowerCase()));
    const selected = tables.find(t => t.id === selectedId) || sortedTables[0];
    const reservationOf = (table?: Entity) => table && reservations.find(r => r.tableId === table.id && r.status === 'confirmed');
    const reserved = reservationOf(selected);
    const selectedVisits = open.filter(v => v.tableId === selected?.id);
    const selectedUnits = units.filter(unit => selectedVisits.some(v => v.id === unit.visitId) && !['cancelled', 'unavailable'].includes(unit.status));
    const priorities = servicePriorities(open, units, notifications, delayMinutes);
    const ownPriorities = priorities.filter(p => manager || open.find(v => v.id === p.visitId)?.waiterId === userId);
    const own = (visit: Entity) => manager || visit.waiterId === userId;
    const waiter = (visit: Entity) => users.find(user => user.id === visit.waiterId)?.name || 'Mesero';
    const count = (key: string) => key === 'all' ? tables.length : tables.filter(t => key === 'ready' ? isReady(t) : t.status === key).length;
    const openTotal = open.reduce((n, v) => n + (v.balance || 0), 0);
    const unitsOf = (visit: Entity) => units.filter(unit => unit.visitId === visit.id && !['cancelled', 'unavailable'].includes(unit.status));
    return <div className="service-board">
        <div className="filterbar ui-table-filters"><div className="seg ui-filter-tabs" role="group" aria-label="Filtrar mesas">{[['all', 'Todas'], ['occupied', 'Ocupadas'], ['free', 'Libres'], ['ready', 'Por entregar'], ['cleaning', 'Por limpiar'], ['reserved', 'Reservadas']].map(([key, label]) => <Button type="button" icon={false} aria-pressed={filter === key} className={filter === key ? 'active' : ''} key={key} onClick={() => setFilter(key)}>{label}<span className="ui-count">{count(key)}</span></Button>)}</div><label className="ui-search"><UiIcon name="search" /><input aria-label="Buscar mesa" placeholder="Buscar mesa o zona…" value={search} onChange={e => setSearch(e.target.value)} /></label></div>
        <div className="summary service-board-top"><div className="facts"><span><UiIcon name="receipt" /><b>{open.length}</b> cuentas en servicio</span><span><UiIcon name="check" /><b>{tables.filter(t => t.status === 'free').length}</b> mesas disponibles</span>{open.length > 0 && <span><UiIcon name="wallet" /><b>{money(openTotal)}</b> en mesas abiertas</span>}</div>
            <div className="legend"><span><i style={{ background: 'var(--green)' }} />Disponible / listo</span><span><i style={{ background: 'var(--blue)' }} />En servicio</span><span><i style={{ background: 'var(--amber-bar)' }} />En preparación</span><span><i style={{ background: 'var(--purple)' }} />Reservada / por limpiar</span></div></div>
        <section aria-label="Mesas del restaurante" className="board-tables"><div className="sec board-section-title"><h2>Selecciona una mesa</h2><span>{tables.length} mesas{zones.filter(Boolean).length > 1 ? ' · ' + zones.filter(Boolean).length + ' zonas' : ''}</span></div><div className="tables-grid stagger">
            {filtered.map((table, index) => {

                const zone = table.zone || '';
                const zoneTables = tables.filter(t => (t.zone || '') === zone);
                const zoneRow = zones.length > 1 && (index === 0 || (filtered[index - 1].zone || '') !== zone) && <div className="table-zone-row" key={'zone-' + zone}>{zone || 'Sin zona'} <small>{zoneTables.length} mesa{zoneTables.length === 1 ? '' : 's'} · {zoneTables.reduce((n, t) => n + (t.capacity || 0), 0)} lugares</small></div>;
                const accounts = open.filter(v => v.tableId === table.id);
                const master = !accounts.length ? open.find(v => (v.linkedTableIds || []).includes(table.id)) : undefined;
                const masterName = master ? (nameOf ? nameOf(master) : tables.find(t => t.id === master.tableId)?.name || 'otra mesa') : '';
                const signals = priorities.filter(p => accounts.some(v => v.id === p.visitId));
                const signal = signals[0];
                const preparing = units.some(unit => accounts.some(v => v.id === unit.visitId) && ['received', 'preparing'].includes(unit.status));
                const state = signal ? priorityClass(signal.kind) : preparing ? 'preparing' : table.status;
                const status = signal ? (signal.kind === 'ready' ? 'Listo para entregar' : signal.kind === 'delayed' ? 'Retraso · ' + signal.minutes + ' min' : signal.label) : preparing ? 'En preparación' : statuses[table.status] || table.status;
                const people = accounts.reduce((n, v) => n + v.people, 0);
                const reservation = reservationOf(table);
                const isSelected = selected?.id === table.id;
                const seatText = master ? 'Unida a ' + masterName : accounts.length ? (people <= table.capacity ? people + ' de ' + table.capacity : people) + ' comensales · ' + minutes(accounts[0].openedAt) + ' min' : table.status === 'reserved' && reservation ? 'Reserva para ' + reservation.people + ' · ' + clock(reservation.start) : table.capacity + ' lugares disponibles';
                return <React.Fragment key={table.id}>{zoneRow}<article className={'table-card ' + table.status + (isSelected ? ' selected' : '')} data-service-state={state}>
                    <div className="top"><div className="row"><button type="button" className="table-select" aria-pressed={isSelected} aria-label={'Seleccionar ' + table.name + ' · ' + status} onClick={() => setSelectedId(table.id)}><h3 aria-label={table.name}>{table.name}</h3></button>
                        <span className="tright"><StatusBadge value={state} label={status} />{accounts.length > 0 && <ContextMenu label={'Acciones de ' + table.name}>{accounts.map(visit => <React.Fragment key={visit.id}><Button icon="receipt" onClick={() => openAccount(visit)}>Ver cuenta{accounts.length > 1 ? ' · ' + visit.id.slice(0, 8) : ''}</Button>{own(visit) && <><Button icon="swap" onClick={() => onMove(visit)}>Mover mesa</Button><Button icon="users" onClick={() => onTransfer(visit)}>Transferir mesero</Button></>}</React.Fragment>)}</ContextMenu>}</span></div>
                        <div className="seats"><span className="seat-dots" aria-hidden="true">{Array.from({ length: Math.min(Math.max(table.capacity || 0, people), 12) }, (_, i) => <i key={i} className={i < people ? 'on' : ''} />)}</span><span className="table-capacity num">{seatText}</span></div>
                        {signal?.kind === 'ready' && signals.some(p => p.kind === 'delayed') && <span className="table-warning">También hay preparación con retraso</span>}</div>
                    {master ? <div className="mid"><div className="k"><b>Unida a {masterName}</b><span>Cuenta maestra · {waiter(master)}</span></div><div className="v num">{money(master.balance)}<small>Saldo</small></div></div> : accounts.length ? <div className="mid"><div className="k"><b>{accounts.length > 1 ? accounts.length + ' cuentas abiertas' : 'Cuenta abierta'}</b><span>{[...new Set(accounts.map(waiter))].join(', ')} · {accounts.reduce((n, v) => n + unitsOf(v).length, 0)} producto{accounts.reduce((n, v) => n + unitsOf(v).length, 0) === 1 ? '' : 's'}</span></div><div className="v num">{money(accounts.reduce((n, v) => n + v.balance, 0))}<small>Saldo</small></div></div>
                        : table.status === 'cleaning' ? <div className="mid"><div className="k"><b>Pendiente de limpieza</b><span>Confirma para liberarla</span></div><span className="dash">—</span></div>
                            : table.status === 'reserved' ? <div className="mid reserved-note"><div className="k"><b>{reservation ? 'Llega a las ' + clock(reservation.start) : 'Reservación confirmada'}</b><span>{reservation ? reservation.name : 'Mesa apartada'}</span></div><span className="dash">—</span></div>
                                : <div className="mid ui-table-balance"><div className="k"><b>Sin cuenta abierta</b><span>Lista para recibir</span></div><span className="dash">—</span></div>}
                    <div className="foot">
                        {accounts.map(visit => <Button type="button" className={(isSelected ? 'primary' : 'ghost') + ' wide'} icon="receipt" key={visit.id} data-open-account={visit.id} onClick={() => openAccount(visit)}>Ver cuenta{accounts.length > 1 ? ' · ' + visit.id.slice(0, 8) : ''}</Button>)}
                        {master && <Button type="button" className="ghost wide" icon="receipt" onClick={() => openAccount(master)}>Ver cuenta maestra</Button>}{table.status === 'free' && <Button type="button" className="ghost wide" icon="table" onClick={() => startTable(table)}>Abrir mesa</Button>}
                        {table.status === 'cleaning' && <Button type="button" className="ghost wide" icon="check" onClick={() => onClean(table)}>Confirmar limpieza</Button>}
                        {table.status === 'reserved' && onReservations && <Button type="button" className="ghost wide" icon="calendar" onClick={onReservations}>Ver reservación</Button>}
                    </div>
                </article></React.Fragment>;

            })}
        </div>{!filtered.length && <p className="empty-table">No hay mesas con este filtro.</p>}</section>
        <div className="g g-dock">
            <AttentionQueue priorities={ownPriorities} visits={open} tables={tables} onOpen={openAccount} nameOf={nameOf} />
            {selected && <section className="card dock selected-table-dock" aria-label="Resumen de mesa seleccionada" aria-live="polite">
                <div className="dock-l"><span className="dock-label">Mesa seleccionada</span><h3>{selected.name}</h3>
                    <p>{[selected.zone, selectedVisits.length ? selectedVisits.reduce((t, v) => t + v.people, 0) + ' comensales' : selected.capacity + ' lugares', selectedVisits.length ? 'abierta ' + clock(selectedVisits[0].openedAt) : statuses[selected.status]].filter(Boolean).join(' · ')}</p>
                    {selectedVisits.length ? selectedVisits.map(visit => <div className="dock-account" key={visit.id}><div className="dock-balance"><small>{selectedVisits.length > 1 ? 'Cuenta ' + visit.id.slice(0, 8) : 'Saldo pendiente'}</small><strong className="num">{money(visit.balance)}</strong></div><div className="dock-actions"><Button type="button" className="primary sm" icon="eye" onClick={() => openAccount(visit)}>Ver detalle</Button></div></div>)
                        : <div className="dock-actions" style={{ marginTop: 20 }}>{selected.status === 'free' && <Button type="button" className="primary sm" icon="table" onClick={() => startTable(selected)}>Abrir {selected.name}</Button>}{selected.status === 'cleaning' && <Button type="button" className="primary sm" icon="check" onClick={() => onClean(selected)}>Confirmar limpieza</Button>}</div>}
                </div>
                <div className="dock-r dock-preparation">{selectedUnits.length ? <><div className="table-scroll"><table><thead><tr><th>Comanda</th><th>Estado</th><th className="r">Importe</th></tr></thead><tbody>{selectedUnits.slice(0, 4).map(unit => <tr key={unit.id}><td><b>1 × {unit.name}</b><PreparationNotes notes={unit.notes} cooking={unit.cooking} extras={unit.extras} modifiers={unit.modifiers} /></td><td><StatusBadge value={unit.status} /></td><td className="r num">{money(unit.price)}</td></tr>)}</tbody><tfoot><tr><td>Total de la cuenta{selectedUnits.length > 4 ? ' · ' + selectedUnits.length + ' productos' : ''}</td><td /><td className="r num">{money(selectedVisits.reduce((n, v) => n + (v.total ?? v.balance), 0))}</td></tr></tfoot></table></div>
                    <div className="meta-row"><div><small>Mesero</small><b>{[...new Set(selectedVisits.map(waiter))].join(', ')}</b></div><div><small>Abierta</small><b className="num">{clock(selectedVisits[0].openedAt)}</b></div><div><small>Tiempo en mesa</small><b className="num">{minutes(selectedVisits[0].openedAt)} min</b></div>{onAdd && own(selectedVisits[0]) && <Button type="button" className="ghost sm" icon="plus" onClick={() => onAdd(selectedVisits[0])}>Agregar producto</Button>}</div></>
                    : <Empty icon={selectedVisits.length ? 'receipt' : selected.status === 'free' ? 'table' : selected.status === 'reserved' ? 'calendar' : 'spark'} title={selectedVisits.length ? 'Lista para una nueva comanda' : selected.status === 'free' ? 'Lista para recibir comensales' : statuses[selected.status] || 'Mesa'} text={selectedVisits.length ? 'Agrega productos desde el detalle de la cuenta.' : selected.status === 'free' ? 'Abre la mesa para comenzar su cuenta.' : reserved ? 'Reservación de ' + reserved.name + ' · ' + reservationTime(reserved.start) : 'Consulta su estado antes de iniciar una nueva visita.'} />}</div>
            </section>}
        </div>
    </div>;

}
