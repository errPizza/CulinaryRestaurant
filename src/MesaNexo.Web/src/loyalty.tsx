import React, { useState } from 'react';
import { Button, PanelHeading, Kpis, MetricCard, Pill, Sq, Empty, Notice, DataTable, PageTools, Bar } from './ui';
import { DialogFrame } from './motion';
import { cents } from './bridge';
import { money, type Dialog } from './backoffice';

type Row = { id: string;[key: string]: any };
type Ctx = { close: () => void; submit: (fn: () => Promise<any>) => void; busy: boolean; error: string };
type Act = (type: string, payload: any) => Promise<any>;
const list = (s: any, kind: string): Row[] => s[kind + 's'] || [];
const date = (s: string) => s ? new Date(s).toLocaleString('es-MX', { dateStyle: 'medium', timeStyle: 'short' }) : '—';
const day = (s: string) => s ? new Date(s).toLocaleDateString('es-MX', { day: 'numeric', month: 'short' }) : '—';
const plural = (n: number, one: string, many: string) => n + ' ' + (n === 1 ? one : many);
const pct = (bps: number) => (bps / 100).toLocaleString('es-MX', { maximumFractionDigits: 2 }) + ' %';
const monthStart = () => { const d = new Date(); return new Date(d.getFullYear(), d.getMonth(), 1).getTime(); };
const monthKey = () => { const d = new Date(new Date().toLocaleString('en-US', { timeZone: 'America/Mexico_City' })); return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0'); };
export const walletBalance = (s: any, customerId: string) => list(s, 'walletmove').filter(m => m.customerId === customerId).reduce((t, m) => t + m.amount, 0);
export const methodLabel: Record<string, string> = { cash: 'Efectivo', card: 'Tarjeta', transfer: 'Transferencia', wallet: 'Monedero', payroll: 'Nómina', online: 'Pago en línea', room: 'Cargo a habitación', platform: 'Plataformas de reparto' };
const CHANNELS = [{ value: 'dine-in', label: 'Mesas' }, { value: 'quick', label: 'Mostrador' }, { value: 'pickup', label: 'Para llevar' }, { value: 'delivery', label: 'Domicilio' }];
const channelName = (c: string) => CHANNELS.find(x => x.value === c)?.label || c;
function Head({ title, note, close, busy }: { title: string; note?: string; close: () => void; busy: boolean }) {

    return <div className="dialog-head"><div><h2>{title}</h2>{note && <p>{note}</p>}</div><Button className="ghost dialog-close" icon="close" onClick={close} disabled={busy} aria-label="Cerrar diálogo">{''}</Button></div>;

}

// ---------- price lists ----------
export function priceListForm(s: any, form: any, l?: Row) {

    const products = list(s, 'product');
    form(l ? 'Editar lista de precios' : 'Lista de precios', [
        { key: 'name', label: 'Nombre', value: l?.name, required: true, full: true, placeholder: 'Ej. Hora feliz, Eventos, Domicilio' },
        { key: 'adjust', label: 'Ajuste general %', type: 'number', value: l ? l.adjustBps / 100 : 0, min: -90, max: 100, step: 'any', hint: 'Negativo descuenta, positivo aumenta. Se aplica a los productos sin precio fijo.' },
        { key: 'priority', label: 'Prioridad', type: 'number', value: l?.priority ?? 0, min: 0, max: 100, hint: 'Si varias aplican, gana el número menor.' },
        { key: 'auto', label: 'Aplicar automáticamente', type: 'checkbox', value: l ? l.auto : true, hint: 'Si no, sólo se aplica cuando gerencia la elige en una cuenta.' },
        { key: 'channels', label: 'Canales', type: 'multi', value: l?.channels || ['dine-in'], options: CHANNELS, showIf: (x: any) => x.auto },
        { key: 'startHour', label: 'Desde la hora', type: 'number', value: l?.startHour ?? 0, min: 0, max: 23, showIf: (x: any) => x.auto },
        { key: 'endHour', label: 'Hasta la hora', type: 'number', value: l?.endHour ?? 24, min: 1, max: 24, showIf: (x: any) => x.auto },
        { key: 'prices', label: 'Precios fijos', type: 'rows', value: (l?.prices || []).map((p: Row) => p.productId + '|' + p.price / 100).join('\n'), rows: { columns: [{ key: 'product', label: 'Producto', type: 'select' as const, options: products.map(p => ({ value: p.id, label: p.name + ' · ' + money(p.price) })) }, { key: 'price', label: 'Precio', type: 'number' as const, width: '130px', prefix: '$' }], split: (line: string) => line.split('|'), join: (c: string[]) => c.join('|'), add: 'Agregar precio fijo', empty: 'Sin precios fijos; sólo se aplica el ajuste general.' } },
        ...(l ? [{ key: 'active', label: 'Lista activa', type: 'checkbox', value: l.active !== false }] : [])
    ], 'pricelist.save', { id: l?.id }, 'Las listas cambian el precio de las comandas nuevas; lo ya enviado conserva su precio.', (x: any) => ({ name: x.name, adjustBps: Math.round(Number(x.adjust || 0) * 100), priority: Number(x.priority) || 0, auto: !!x.auto, channels: x.auto ? x.channels : [], startHour: x.auto ? Number(x.startHour) || 0 : 0, endHour: x.auto ? Number(x.endHour) || 24 : 24, active: x.active ?? true, prices: String(x.prices || '').split('\n').map((line: string) => line.split('|')).filter(([id, price]) => id && String(price ?? '').trim() !== '').map(([productId, price]) => ({ productId, price: cents(price) })) }));

}
export function PriceListsPanel({ state: s, form }: { state: any; form: any }) {

    const lists = list(s, 'pricelist');
    return <section className="card panel"><PanelHeading title="Listas de precios" note="Precios por canal, horario o evento" icon="percent" action={<Button className="ghost sm" icon="plus" onClick={() => priceListForm(s, form)}>Lista</Button>} />
        {lists.length ? <DataTable columns={['Lista', 'Ajuste', 'Precios fijos', 'Aplica en', 'Horario', 'Estado', '']} rows={lists.map(l => [<b>{l.name}</b>, l.adjustBps ? <span className={l.adjustBps < 0 ? 'pos' : 'neg'}>{(l.adjustBps > 0 ? '+' : '') + pct(l.adjustBps)}</span> : <span className="no">—</span>, (l.prices || []).length || <span className="no">—</span>, l.auto ? (l.channels || []).map(channelName).join(', ') : <Pill tone="purple">Manual</Pill>, l.auto ? (l.startHour === 0 && l.endHour === 24 ? 'Todo el día' : l.startHour + ':00 – ' + l.endHour + ':00') : '—', l.active ? <Pill tone="green">Activa</Pill> : <Pill>Inactiva</Pill>, <Button className="ghost xs" icon="edit" onClick={() => priceListForm(s, form, l)}>Editar</Button>])} />
            : <Empty icon="percent" title="Sin listas de precios" text="Crea una hora feliz, precios de domicilio o una lista para eventos." />}
    </section>;

}

// ---------- customers and wallet ----------
export function customerOptions(s: any) { return list(s, 'customer').map(c => ({ value: c.id, label: c.name + ' · ' + c.phone + ' · ' + money(walletBalance(s, c.id)) })); }
export function CustomersPage({ state: s, form, action, run }: { state: any; form: any; action: Act; run: any }) {

    const [term, setTerm] = useState('');
    const customers = list(s, 'customer'), moves = list(s, 'walletmove'), loyalty = s.restaurant?.loyalty || {};
    const month = moves.filter(m => Date.parse(m.at) >= monthStart());
    const totalBalance = customers.reduce((t, c) => t + walletBalance(s, c.id), 0);
    const visitsOf = (id: string) => list(s, 'visit').filter(v => v.customerId === id && v.status === 'closed');
    const shown = customers.filter(c => !term.trim() || (c.name + ' ' + c.phone).toLowerCase().includes(term.trim().toLowerCase())).sort((a, b) => walletBalance(s, b.id) - walletBalance(s, a.id));
    const configure = () => form('Programa de lealtad', [{ key: 'enabled', label: 'Programa activo', type: 'checkbox', value: !!loyalty.enabled, hint: 'Al cerrar la cuenta de un cliente registrado se abona el cashback a su monedero.' }, { key: 'cashback', label: 'Cashback %', type: 'number', value: (loyalty.cashbackBps || 0) / 100, min: 0, max: 20, step: 'any', hint: 'Sobre lo pagado; lo pagado con monedero o nómina no genera cashback.' }], 'loyalty.configure', {}, undefined, (x: any) => ({ enabled: !!x.enabled, cashbackBps: Math.round(Number(x.cashback || 0) * 100) }));
    const topup = (c: Row) => form('Recargar monedero de ' + c.name, [{ key: 'amount', label: 'Importe MXN', type: 'money', min: 0.01, required: true }, { key: 'method', label: 'Recibido en', type: 'select', value: 'cash', options: [{ value: 'cash', label: 'Efectivo (entra a la caja)' }, { value: 'card', label: 'Tarjeta' }, { value: 'transfer', label: 'Transferencia' }] }], 'wallet.topup', { customerId: c.id }, 'Saldo actual ' + money(walletBalance(s, c.id)), (x: any) => ({ ...x, amount: cents(x.amount) }));
    const adjust = (c: Row) => form('Ajustar monedero de ' + c.name, [{ key: 'direction', label: 'Movimiento', type: 'select', value: 'add', options: [{ value: 'add', label: 'Abonar' }, { value: 'remove', label: 'Descontar' }] }, { key: 'amount', label: 'Importe MXN', type: 'money', min: 0.01, required: true }, { key: 'reason', label: 'Motivo', required: true, full: true }], 'wallet.adjust', { customerId: c.id }, 'Queda registrado con tu usuario.', (x: any) => ({ reason: x.reason, amount: (x.direction === 'remove' ? -1 : 1) * cents(x.amount) }));
    // A person's access and deletion requests; see docs/PRIVACIDAD-DATOS.md.
    const exportData = (c: Row) => run(async () => { const data = await action('customer.export', { id: c.id }); const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' })); a.download = 'cliente-' + c.id + '.json'; a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 1000); }, 'Datos exportados');
    const erase = (c: Row) => form('Eliminar datos de ' + c.name, [{ key: 'reason', label: 'Motivo de la solicitud', required: true, full: true }], 'customer.erase', { id: c.id }, 'Sus ventas se conservan sin su nombre, y los CFDI emitidos se guardan por obligación fiscal. No se puede deshacer.');
    const customer = (c?: Row) => form(c ? 'Editar cliente' : 'Cliente', [{ key: 'name', label: 'Nombre', value: c?.name, required: true }, { key: 'phone', label: 'Teléfono', value: c?.phone, required: true }, { key: 'notes', label: 'Notas', type: 'textarea', value: c?.notes, full: true, placeholder: 'Preferencias, alergias…' }], 'customer.save', { id: c?.id });
    return <>
        <PageTools><Button className="ghost" icon="settings" onClick={configure}>Programa de lealtad</Button><Button className="primary" icon="plus" onClick={() => customer()}>Cliente</Button></PageTools>
        {!loyalty.enabled && <Notice>El programa de lealtad está apagado. Actívalo para abonar cashback al cerrar las cuentas de clientes registrados.</Notice>}
        <Kpis>
            <MetricCard className="lead" label="Saldo en monederos" value={money(totalBalance)} note={plural(customers.filter(c => walletBalance(s, c.id) > 0).length, 'cliente con saldo', 'clientes con saldo')} icon="wallet" tone="amber" />
            <MetricCard label="Cashback del mes" value={money(month.filter(m => m.type === 'earn').reduce((t, m) => t + m.amount, 0))} note={loyalty.enabled ? pct(loyalty.cashbackBps || 0) + ' sobre lo pagado' : 'Programa apagado'} icon="star" tone="green" />
            <MetricCard label="Canjeado del mes" value={money(-month.filter(m => m.type === 'redeem').reduce((t, m) => t + m.amount, 0))} note={plural(month.filter(m => m.type === 'redeem').length, 'pago', 'pagos') + ' con monedero'} icon="receipt" tone="blue" />
            <MetricCard label="Clientes" value={customers.length} note={plural(customers.filter(c => visitsOf(c.id).length > 0).length, 'con visitas', 'con visitas')} icon="users" tone="purple" />
        </Kpis>
        <section className="card panel"><PanelHeading title="Clientes" note="Ordenados por saldo" icon="users" action={<label className="ui-search sm"><input aria-label="Buscar cliente" placeholder="Buscar nombre o teléfono…" value={term} onChange={e => setTerm(e.target.value)} /></label>} />
            <DataTable columns={['Cliente', 'Teléfono', '>Visitas', 'Última visita', '>Saldo', '']} rows={shown.map(c => { const visits = visitsOf(c.id); const last = visits.map(v => v.closedAt).sort().at(-1); const balance = walletBalance(s, c.id); return [<span className="w"><b>{c.name}</b>{c.notes && <small>{c.notes}</small>}</span>, <code>{c.phone}</code>, visits.length, last ? day(last) : <span className="no">—</span>, <b className={balance > 0 ? 'pos' : ''}>{money(balance)}</b>, <span className="row-actions"><Button className="primary xs" icon="plus" onClick={() => topup(c)}>Recargar</Button><Button className="ghost xs" icon="sliders" onClick={() => adjust(c)}>Ajustar</Button><Button className="ghost xs icon-only" icon="edit" aria-label={'Editar ' + c.name} onClick={() => customer(c)}>{''}</Button><Button className="ghost xs icon-only" icon="download" aria-label={'Exportar datos de ' + c.name} onClick={() => exportData(c)}>{''}</Button>{!c.erasedAt && <Button className="ghost xs icon-only" icon="close" aria-label={'Eliminar datos de ' + c.name} onClick={() => erase(c)}>{''}</Button>}</span>]; })} empty="Registra clientes para acumular cashback y pagar con su monedero." />
        </section>
        <section className="card panel"><PanelHeading title="Movimientos del monedero" note="Abonos, recargas, canjes y ajustes" icon="history" />
            <DataTable columns={['Fecha', 'Cliente', 'Tipo', 'Detalle', '>Importe']} rows={moves.slice(-30).reverse().map(m => [date(m.at), list(s, 'customer').find(c => c.id === m.customerId)?.name || '—', <Pill tone={m.type === 'earn' ? 'green' : m.type === 'topup' ? 'blue' : m.type === 'redeem' ? 'amber' : 'purple'}>{({ earn: 'Cashback', topup: 'Recarga', redeem: 'Canje', adjust: 'Ajuste' } as any)[m.type]}</Pill>, m.reason || (m.method ? methodLabel[m.method] : m.visitId ? 'Cuenta ' + m.visitId.slice(0, 8) : ''), <b className={m.amount < 0 ? 'neg' : 'pos'}>{(m.amount > 0 ? '+' : '') + money(m.amount)}</b>])} empty="Sin movimientos todavía." />
        </section>
    </>;

}

// ---------- employee meals ----------
export function StaffPanel({ state: s, form, action, run }: { state: any; form: any; action: Act; run: any }) {

    const config = s.restaurant?.staff || {}, charges = list(s, 'staffcharge'), month = monthKey(), users = list(s, 'user').filter(w => w.active && w.role !== 'owner');
    const rows = users.map(w => { const mine = charges.filter(c => c.employeeId === w.id && c.month === month); return { w, total: mine.reduce((t, c) => t + c.amount, 0), open: mine.filter(c => c.status === 'open').reduce((t, c) => t + c.amount, 0), meals: mine.length }; }).filter(r => r.meals > 0 || config.monthlyLimit);
    const configure = () => form('Consumo de empleados', [{ key: 'discount', label: 'Descuento al empleado %', type: 'number', value: (config.discountBps || 0) / 100, min: 0, max: 100, step: 'any' }, { key: 'limit', label: 'Tope mensual por nómina MXN', type: 'money', value: config.monthlyLimit ? config.monthlyLimit / 100 : '', min: 0, hint: 'Vacío = sin tope.' }], 'staff.configure', {}, 'Se aplica en Mostrador › Consumo de empleado.', (x: any) => ({ discountBps: Math.round(Number(x.discount || 0) * 100), monthlyLimit: String(x.limit ?? '').trim() === '' ? 0 : cents(x.limit) }));
    return <section className="card panel"><PanelHeading title="Consumo de empleados" note={'Mes ' + month + ' · descuento ' + pct(config.discountBps || 0) + (config.monthlyLimit ? ' · tope ' + money(config.monthlyLimit) : ' · sin tope')} icon="users" action={<Button className="ghost sm" icon="settings" onClick={configure}>Configurar</Button>} />
        {rows.length ? <DataTable columns={['Empleado', '>Consumos', '>Este mes', 'Tope', '>Por descontar', '']} rows={rows.map(({ w, total, open, meals }) => [<b>{w.name}</b>, meals, money(total), config.monthlyLimit ? <div className="limit-cell"><Bar value={total} max={config.monthlyLimit} tone={total >= config.monthlyLimit ? 'r' : total > config.monthlyLimit * .8 ? '' : 'g'} /><small>{money(Math.max(0, config.monthlyLimit - total))} disponible</small></div> : <span className="no">Sin tope</span>, open ? <b>{money(open)}</b> : <span className="no">—</span>, open > 0 && <Button className="ghost xs" icon="check" onClick={() => run(() => action('staff.settle', { employeeId: w.id, month }), 'Consumos marcados como descontados')}>Marcar descontado</Button>])} />
            : <Empty icon="users" title="Sin consumos este mes" text="Registra comidas del personal desde Mostrador › Consumo de empleado." />}
    </section>;

}

// ---------- X / Z closings and ABC ----------
function Summary({ data }: { data: any }) {

    const methods = Object.entries(data.methods || {}) as [string, any][];
    return <div className="z-summary">
        <div className="variance-kpis"><div><small>Venta neta</small><b className="num">{money(data.net)}</b><span>{plural(data.tickets, 'cuenta', 'cuentas')} · ticket {money(data.average)}</span></div><div><small>Cobrado</small><b className="num">{money(data.payments)}</b><span>propinas {money(data.tips)}</span></div><div><small>Descuentos</small><b className="num">{money(data.discounts)}</b><span>{plural(data.comps, 'cortesía', 'cortesías')}</span></div><div className={data.voidedCount ? 'bad' : ''}><small>Cancelaciones</small><b className="num">{money(data.voidedAmount)}</b><span>{plural(data.voidedCount, 'producto', 'productos')}</span></div></div>
        <div className="g g2">
            <div><h4 className="sub-h">Formas de pago</h4><div className="rows">{methods.length ? methods.map(([m, v]) => <div className="r" key={m}><span className="w"><b>{methodLabel[m] || m}</b><span>{plural(v.count, 'pago', 'pagos')}{v.tips ? ' · propinas ' + money(v.tips) : ''}</span></span><span className="amt num">{money(v.amount)}</span></div>) : <p className="no">Sin pagos</p>}</div></div>
            <div><h4 className="sub-h">Por categoría</h4><div className="rows">{(data.categories || []).slice(0, 6).map((c: any) => <div className="r" key={c.name}><span className="w"><b>{c.name}</b><span>{plural(c.quantity, 'producto', 'productos')}</span></span><span className="amt num">{money(c.amount)}</span></div>)}</div></div>
        </div>
        <div className="g g2">
            <div><h4 className="sub-h">Por mesero</h4><div className="rows">{(data.waiters || []).map((w: any) => <div className="r" key={w.waiterId}><span className="w"><b>{w.name}</b><span>{plural(w.tickets, 'cuenta', 'cuentas')}</span></span><span className="amt num">{money(w.amount)}</span></div>)}</div></div>
            <div><h4 className="sub-h">Caja</h4><div className="rows"><div className="r"><span className="w">Ingresos</span><span className="amt num">{money(data.cash?.income)}</span></div><div className="r"><span className="w">Gastos</span><span className="amt num neg">−{money(data.cash?.expense)}</span></div><div className="r"><span className="w">Retiros</span><span className="amt num neg">−{money(data.cash?.withdrawal)}</span></div><div className="r"><span className="w">Diferencias de arqueo</span><span className={'amt num ' + ((data.cash?.difference || 0) < 0 ? 'neg' : '')}>{money(data.cash?.difference)}</span></div><div className="r"><span className="w">Impuesto incluido</span><span className="amt num">{money(data.tax)}</span></div></div></div>
        </div>
    </div>;

}
function ClosingDialog({ title, data, ctx }: { title: string; data: any; ctx: Ctx }) {

    return <DialogFrame label={title} close={ctx.close} className="po-dialog"><Head title={title} note={day(data.from) + ' ' + new Date(data.from).toLocaleTimeString('es-MX', { hour: '2-digit', minute: '2-digit' }) + ' → ' + day(data.to) + ' ' + new Date(data.to).toLocaleTimeString('es-MX', { hour: '2-digit', minute: '2-digit' })} close={ctx.close} busy={false} />
        {data.openAccounts > 0 && data.kind === 'X' && <Notice tone="warn">{plural(data.openAccounts, 'cuenta sigue abierta', 'cuentas siguen abiertas')}; no entran en este corte hasta cerrarse.</Notice>}
        <Summary data={data.summary || data} />
        <div className="dialog-actions"><Button className="primary" icon={false} onClick={ctx.close}>Listo</Button></div>
    </DialogFrame>;

}
export function ClosingsPanel({ state: s, action, run, dialog, confirm }: { state: any; action: Act; run: any; dialog: Dialog; confirm: (title: string, description: string, fn: () => Promise<any>, word?: string) => void }) {

    const zs = list(s, 'zreport').slice().reverse(), cashOpen = (s.cashs || s.cash || []).some((c: Row) => c.status === 'open');
    const show = (title: string, data: any) => dialog(ctx => <ClosingDialog title={title} data={data} ctx={ctx} />);
    return <section className="card panel"><PanelHeading title="Cortes X y Z" note="X consulta el turno sin cerrarlo · Z cierra el día con folio consecutivo" icon="receipt" action={<div className="row-actions"><Button className="ghost sm" icon="eye" onClick={() => run(async () => show('Corte X', await action('report.x', {})), 'Corte X generado')}>Corte X</Button><Button className="primary sm" icon="lock" disabled={cashOpen} title={cashOpen ? 'Cierra la caja primero' : undefined} onClick={() => confirm('Corte Z', 'Cierra el periodo desde el último corte. Requiere caja y cuentas cerradas; no se puede deshacer.', async () => { const z = await action('report.z', {}); show('Corte Z · folio ' + z.number, z); }, 'CORTE')}>Corte Z</Button></div>} />
        {cashOpen && <p className="hint-line">La caja está abierta: el corte Z se habilita al cerrarla.</p>}
        <DataTable columns={['Folio', 'Periodo', '>Cuentas', '>Venta neta', '>Cobrado', '']} rows={zs.map(z => [<b>Z-{String(z.number).padStart(4, '0')}</b>, day(z.from) + ' → ' + day(z.to), z.summary.tickets, money(z.summary.net), money(z.summary.payments), <Button className="ghost xs" icon="eye" onClick={() => show('Corte Z · folio ' + z.number, z)}>Ver</Button>])} empty="Aún no hay cortes Z." />
    </section>;

}
export function AbcPanel({ action, run }: { action: Act; run: any }) {

    const [period, setPeriod] = useState('30'), [data, setData] = useState<any>(null);
    const range = () => { const to = new Date(Date.now() + 60000); const from = period === 'month' ? new Date(monthStart()) : new Date(Date.now() - Number(period) * 86400000); return { from: from.toISOString(), to: to.toISOString() }; };
    const rows: Row[] = data?.rows || [];
    const count = (c: string) => rows.filter(r => r.class === c);
    const tone: Record<string, string> = { A: 'green', B: 'amber', C: 'red' };
    return <section className="card panel"><PanelHeading title="Análisis ABC" note="A = 80 % de la venta · B = siguiente 15 % · C = último 5 %" icon="chart" action={<div className="row-actions"><div className="seg sm" role="group" aria-label="Periodo ABC">{[['7', '7 días'], ['30', '30 días'], ['month', 'Este mes']].map(([v, t]) => <Button key={v} icon={false} className={period === v ? 'active' : ''} aria-pressed={period === v} onClick={() => { setPeriod(v); setData(null); }}>{t}</Button>)}</div><Button className="ghost sm" icon="refresh" onClick={() => run(async () => setData(await action('report.abc', range())), 'Análisis calculado')}>Calcular</Button></div>} />
        {data ? <>
            <div className="variance-kpis">{['A', 'B', 'C'].map(c => <div key={c}><small>Clase {c}</small><b className="num">{count(c).length} <span>productos</span></b><span>{money(count(c).reduce((t, r) => t + r.revenue, 0))} · {data.revenue ? Math.round(count(c).reduce((t, r) => t + r.revenue, 0) / data.revenue * 100) : 0} %</span></div>)}<div><small>Venta analizada</small><b className="num">{money(data.revenue)}</b><span>{plural(data.quantity, 'producto vendido', 'productos vendidos')}</span></div></div>
            <DataTable columns={['Producto', 'Clase', '>Vendidos', '>Venta', '>Participación', '>Costo unitario', '>Margen']} rows={rows.map(r => [<span className="w"><b>{r.name}</b><small>{r.category}</small></span>, <span className="abc-pills"><Pill tone={tone[r.class]}>{r.class}</Pill><small title="Clase por cantidad vendida">cant. {r.quantityClass}</small></span>, r.quantity, <b>{money(r.revenue)}</b>, <span className="share"><Bar value={r.share} max={100} tone={r.class === 'A' ? 'g' : r.class === 'B' ? '' : 'r'} />{r.share.toLocaleString('es-MX', { maximumFractionDigits: 1 })} %</span>, r.unitCost >= 0 ? money(r.unitCost) : <span className="no">Sin receta</span>, r.margin != null ? <span className={r.margin < 0 ? 'neg' : ''}>{money(r.margin)}</span> : <span className="no">—</span>])} empty="Sin ventas cerradas en el periodo." />
        </> : <Empty icon="chart" title="Elige un periodo y calcula" text="Descubre qué platillos sostienen la venta y cuáles conviene revisar." />}
    </section>;

}

// ---------- table QR ----------
function QrDialog({ result, ctx, rotate }: { result: any; ctx: Ctx; rotate: () => Promise<any> }) {

    const [data, setData] = useState(result), [working, setWorking] = useState(false), [note, setNote] = useState(''), [failure, setFailure] = useState('');
    const regenerate = async () => { setWorking(true); setFailure(''); try { setData(await rotate()); setNote('Código regenerado. Imprime el nuevo QR; el anterior ya no funciona.'); } catch (e: any) { setFailure(e.message); } finally { setWorking(false); } };
    return <DialogFrame label={'QR de ' + data.name} close={ctx.close} busy={ctx.busy} className="qr-dialog">
        <Head title={'QR de ' + data.name} note="El cliente escanea, ve el menú, llama al mesero, pide la cuenta o envía un pedido que el mesero confirma." close={ctx.close} busy={ctx.busy} />
        {failure && <div className="alert error" role="alert">{failure}</div>}{note && <Notice tone="ok">{note}</Notice>}
        <div className="qr-card">{data.qr ? <img src={data.qr} alt={'Código QR de ' + data.name} width={240} height={240} /> : <div className="qr-missing">Vista previa disponible en la aplicación de escritorio</div>}<b>{data.name}</b><small>Escanéame para ver el menú y pedir</small></div>
        <code className="qr-link">{data.link}</code>
        <p className="hint-line">Generado el {date(data.rotatedAt)} · Si el código se copia o se daña, regéneralo: el anterior deja de funcionar.</p>
        <div className="dialog-actions">{data.qr && <a className="ui-button ghost" href={data.qr} download={'qr-' + String(data.name).toLowerCase().replace(/\s+/g, '-') + '.png'}>Descargar imagen</a>}<Button className="ghost" icon="refresh" disabled={working} onClick={() => void regenerate()}>{working ? 'Regenerando…' : 'Regenerar código'}</Button><Button className="primary" icon={false} onClick={ctx.close}>Listo</Button></div>
    </DialogFrame>;

}
export function tableQr(s: any, table: Row, action: Act, run: any, dialog: Dialog, form: any) {

    if (!s.restaurant?.tablePortal) { form('Dirección pública para QR', [{ key: 'url', label: 'Dirección de tu página de pedidos', required: true, full: true, placeholder: 'https://pedidos.turestaurante.mx/s/tu-restaurante', hint: 'La entrega el panel central al activar los pedidos en línea.' }], 'qr.configure'); return; }
    run(async () => { const result = await action('table.qr', { id: table.id }); dialog(ctx => <QrDialog result={result} ctx={ctx} rotate={() => action('table.qr', { id: table.id, rotate: true })} />); }, 'Código QR listo');

}

// ---------- QR orders inside the account ----------
export function QrOrders({ state: s, visit, action, run, form }: { state: any; visit: Row; action: Act; run: any; form: any }) {

    const orders = list(s, 'cloudproposal').filter(p => p.command?.type === 'table' && p.status === 'pending-review' && p.visitId === visit.id);
    if (!orders.length) return null;
    const product = (id: string) => list(s, 'product').find(p => p.id === id);
    return <section className="qr-orders" aria-label="Pedidos desde el QR">{orders.map(o => { const items = o.command.payload.items || []; const total = items.reduce((t: number, i: Row) => t + i.quotedPrice * i.quantity, 0); return <div className="r" key={o.id}><Sq icon="receipt" tone="purple" /><div className="w"><b>Pedido desde el QR · {money(total)}</b><span>{items.map((i: Row) => i.quantity + ' × ' + (product(i.productId)?.name || 'Producto') + (i.notes ? ' (' + i.notes + ')' : '')).join(' · ')}</span></div><Button className="ghost sm" icon="close" onClick={() => form('Rechazar pedido del QR', [{ key: 'reason', label: 'Motivo para el cliente', required: true, full: true, placeholder: 'Ej. Se terminó el producto' }], 'qr.decide', { id: o.id, accept: false })}>Rechazar</Button><Button className="primary sm" icon="check" onClick={() => run(() => action('qr.decide', { id: o.id, accept: true }), 'Pedido enviado a cocina')}>Enviar a cocina</Button></div>; })}</section>;

}
