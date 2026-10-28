import React from 'react';
import { createPortal } from 'react-dom';
import { label as statusLabel } from './labels';
const paths: Record<string, string> = {
    grid: 'M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z', table: 'M3 9h18M5 9v10M19 9v10M8 9V5h8v4',
    food: 'M3 13h18a9 9 0 0 1-18 0zM12 4v4M8 6l1 3M16 6l-1 3', bowl: 'M3 11h18a9 9 0 0 1-18 0zM9 6c0-2 2-2 2-4M14 6c0-2 2-2 2-4',
    chef: 'M6 13.9A4 4 0 0 1 7.5 6a5 5 0 0 1 9 0A4 4 0 0 1 18 13.9V20H6zM6 17h12', flame: 'M12 22c4 0 7-2.7 7-7 0-4-3-6-4-9-1 2-2 3-4 3 0-2-1-4-3-6 0 4-3 6-3 11 0 4.3 3 8 7 8z',
    drink: 'M6 7h12l-1.5 14h-9zM5 7h14M14 7l2-5h4', dessert: 'M4 11h16v9H4zM4 15h16M8 11c-3-6 6-7 4-1M12 10c0-6 8-5 6 1',
    bell: 'M6 8a6 6 0 1 1 12 0c0 7 3 9 3 9H3s3-2 3-9M10.3 21a1.9 1.9 0 0 0 3.4 0', chart: 'M4 20V10M10 20V4M16 20v-7M22 20H2',
    users: 'M9 4a4 4 0 1 0 0 8 4 4 0 0 0 0-8M2 21a7 7 0 0 1 14 0M16 4a4 4 0 0 1 0 8M22 21a7 7 0 0 0-4-6.3', user: 'M12 4a4 4 0 1 0 0 8 4 4 0 0 0 0-8M4 21a8 8 0 0 1 16 0',
    calendar: 'M3 5h18v16H3zM3 10h18M8 3v4M16 3v4', calcheck: 'M3 5h18v16H3zM3 10h18M8 3v4M16 3v4M9 15l2 2 4-4',
    settings: 'M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6M12 2v3M12 19v3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M2 12h3M19 12h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1',
    sliders: 'M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6', store: 'M3 9l1.5-5h15L21 9M3 9v11h18V9M3 9h18M9 20v-6h6v6',
    wallet: 'M3 7h16a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h12M16 14h.01', cash: 'M2 6h20v12H2zM12 9.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5M6 12h.01M18 12h.01',
    cloud: 'M7 18a5 5 0 1 1 1-9.9A6 6 0 0 1 19 10a4 4 0 0 1 0 8z', exit: 'M9 3H4v18h5M15 8l4 4-4 4M19 12H9',
    check: 'm5 12 5 5L20 7', book: 'M4 19V5a2 2 0 0 1 2-2h14v16H6a2 2 0 0 0-2 2 2 2 0 0 0 2 2h14',
    help: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18M9.5 9a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.7M12 17h.01', info: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18M12 11v6M12 7.5h.01',
    shield: 'M12 3 4 6v6c0 5 8 9 8 9s8-4 8-9V6zM9 12l2 2 4-4', lock: 'M5 11h14v10H5zM8 11V7a4 4 0 0 1 8 0v4', message: 'M4 4h16v12H8l-4 4zM8 9h8M8 12h5',
    clock: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18M12 7v5l3 2', history: 'M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5M12 7v5l4 2',
    plus: 'M12 5v14M5 12h14', minus: 'M5 12h14', arrow: 'M5 12h14M13 6l6 6-6 6', right: 'm9 6 6 6-6 6', down: 'm6 9 6 6 6-6',
    search: 'M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14m9 16-4-4', filter: 'M4 5h16l-6 8v6l-4 2v-8z',
    box: 'M21 8 12 3 3 8v8l9 5 9-5zM3 8l9 5 9-5M12 13v8', truck: 'M3 5h11v12H3zM14 10h4l3 4v3h-7M7 16.5a2 2 0 1 0 0 4 2 2 0 0 0 0-4m10.5 0a2 2 0 1 0 0 4 2 2 0 0 0 0-4',
    print: 'M6 8V3h12v5M6 17H3V8h18v9h-3M6 14h12v7H6z', file: 'M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9zM14 3v6h6M8 13h8M8 17h5',
    refresh: 'M20 11a8 8 0 0 0-14.9-3M4 4v4h4M4 13a8 8 0 0 0 14.9 3M20 20v-4h-4', edit: 'm4 16 12-12 4 4L8 20H4z', close: 'm6 6 12 12M18 6 6 18',
    alert: 'M12 3 2 20h20zM12 10v4M12 17h.01', eye: 'M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Zm7 0a3 3 0 1 0 6 0 3 3 0 0 0-6 0',
    split: 'M6 3v6c0 3 12 3 12 6v6M6 9v12', save: 'M4 3h13l3 3v15H4V3m4 0v6h8V3M8 21v-8h8v8', download: 'M12 3v12M7 10l5 5 5-5M4 19h16', upload: 'M12 15V3M7 8l5-5 5 5M4 19h16',
    receipt: 'M5 3v18l2.5-1.5L10 21l2-1.5 2 1.5 2.5-1.5L19 21V3l-2.5 1.5L14 3l-2 1.5L10 3 7.5 4.5zM9 9h6M9 13h6',
    spark: 'M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M5.6 18.4l2.1-2.1M16.3 7.7l2.1-2.1',
    swap: 'M4 8h14l-3-3M20 16H6l3 3', percent: 'M19 5 5 19M7 4.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5M17 14.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5',
    layers: 'm12 3 9 5-9 5-9-5zM3 13l9 5 9-5', key: 'M8 11a4 4 0 1 0 0 8 4 4 0 0 0 0-8m3 1 9-9M17 6l3 3M15 8l2 2',
    db: 'M4 5c0-1.7 3.6-3 8-3s8 1.3 8 3-3.6 3-8 3-8-1.3-8-3m0 0v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3',
    monitor: 'M2 4h20v13H2zM8 21h8M12 17v4', device: 'M5 2h14v20H5zM11 18h2', pin: 'M12 21s7-6 7-12a7 7 0 0 0-14 0c0 6 7 12 7 12zm0-14.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5',
    phone: 'M5 3h4l2 5-2.5 1.5a11 11 0 0 0 6 6L16 13l5 2v4a2 2 0 0 1-2 2A17 17 0 0 1 3 5a2 2 0 0 1 2-2',
    moon: 'M20 14.5A8 8 0 0 1 9.5 4 8 8 0 1 0 20 14.5z', sun: 'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4',
    motion: 'M3 12h4M5 7h6M5 17h6M16 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10', star: 'm12 3 2.8 5.7 6.2.9-4.5 4.4 1 6.2L12 17.3 6.5 20.2l1-6.2L3 9.6l6.2-.9z',
    home: 'M3 10.5 12 3l9 7.5V21H3z', power: 'M12 3v9M6.3 6.3a8 8 0 1 0 11.4 0',wifi: 'M5 12.5a10 10 0 0 1 14 0M8.5 16a5 5 0 0 1 7 0M2 9a15 15 0 0 1 20 0M12 20h.01',
    dots: 'M4 12a1 1 0 1 0 2 0 1 1 0 1 0-2 0M11 12a1 1 0 1 0 2 0 1 1 0 1 0-2 0M18 12a1 1 0 1 0 2 0 1 1 0 1 0-2 0',
    plug: 'M9 2v5M15 2v5M6 7h12v4a6 6 0 0 1-12 0zM12 17v5', card: 'M2 6h20v12H2zM2 10h20M6 15h4',
};
export function UiIcon({ name = 'grid', className = '' }: { name?: string; className?: string }) { return <svg className={'ui-icon ' + className} aria-hidden="true" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round"><path d={paths[name] || paths.file} /></svg>; }
export const pageIcons: Record<string, string> = { overview: 'chart', tables: 'table', counter: 'cash', notifications: 'bell', kitchen: 'flame', history: 'history', catalog: 'food', cash: 'wallet', blind: 'wallet', inventory: 'box', purchases: 'truck', loyalty: 'star', expenses: 'receipt', delivery: 'truck', printing: 'print', fiscal: 'file', integrations: 'plug', reports: 'chart', team: 'users', promotions: 'percent', settings: 'settings', reservations: 'calendar', reviews: 'star', support: 'help', analytics: 'chart', modules: 'layers', commercial: 'key', releases: 'cloud', backups: 'db', audit: 'history' };
function text(node: React.ReactNode): string { return React.Children.toArray(node).map(n => typeof n === 'string' || typeof n === 'number' ? String(n) : React.isValidElement<{ children?: React.ReactNode }>(n) ? text(n.props.children) : '').join(' '); }
function hasIcon(node: React.ReactNode): boolean { return React.Children.toArray(node).some(n => React.isValidElement<{ children?: React.ReactNode }>(n) && (n.type === UiIcon || n.type === 'svg' || hasIcon(n.props.children))); }
export function actionIcon(value: string) {

    const v = value.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
    for (const [pattern, icon] of [[/soporte/, 'help'], [/contrasena|seguridad|sesion/, 'lock'], [/revocar|cancelar|rechazar|no disponible|retirar/, 'close'], [/imprim|impres/, 'print'], [/descargar|exportar|respaldo/, 'download'], [/cargar paquete|publicar/, 'upload'], [/autorizar|aprobar|validar|verificar/, 'shield'], [/confirmar|listo para|limpieza|atender|entregar|entrega/, 'check'], [/guardar/, 'save'], [/editar|modificar|administrar/, 'edit'], [/configura|politica|semaforo|prioridad/, 'settings'], [/actualiz|reconcil|reintentar/, 'refresh'], [/dividir|separar|unir/, 'split'], [/pago|caja|arqueo|cobro|cobrar|retiro|gasto|ingreso/, 'wallet'], [/cliente|repartidor|mesero|equipo|empleado/, 'users'], [/receta|insumo|almacen|merma|inventario|traspasar|entrada|devolucion/, 'box'], [/solicitud|responder|mensaje/, 'message'], [/mesa|cuenta/, 'table'], [/producto|categoria|catalogo/, 'book'], [/nuevo|nueva|crear|abrir|^\+/, 'plus'], [/consultar|ver |mostrar|vista/, 'eye'], [/volver|siguiente|detalle/, 'arrow']] as const) if (pattern.test(v)) return icon;
    return 'arrow';

}
export function Button({ children, icon, className = '', ...props }: React.ComponentPropsWithRef<'button'> & { icon?: string | false }) {

    const content = text(children), simple = /^[×+−–→↗✕]$/.test(content.trim()), graphic = hasIcon(children);
    return <button {...props} className={'ui-button ' + className}>{icon !== false && !graphic && !simple && <UiIcon name={icon || actionIcon(content)} />}{children}</button>;

}
// Every dropdown goes through here so an empty list still opens and shows one row that cannot be chosen.
export function Select({ children, value, ...props }: React.ComponentPropsWithRef<'select'>) {

    const empty = !React.Children.toArray(children).some(child => React.isValidElement(child) && (child.type === 'option' || child.type === 'optgroup'));
    return <select {...props} value={empty && value !== undefined ? '' : value}>{empty ? <option value="" disabled>{'< Sin opciones >'}</option> : children}</select>;

}
function sectionIcon(value: string) { const v = value.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase(); for (const [pattern, icon] of [[/soporte|ayuda/, 'help'], [/autoriz|consent|cobertura|seguridad|auditor/, 'shield'], [/preparacion|cocina|comanda|consumo|estacion/, 'chef'], [/existencia|reserva|receta|insumo|almacen|inventario|traspas/, 'box'], [/pago|caja|arqueo|comercial/, 'wallet'], [/mesa/, 'table'], [/equipo|usuario|dispositivo|cliente/, 'users'], [/delivery|repart/, 'truck'], [/impres/, 'print'], [/producto|categoria|catalog/, 'book'], [/solicitud|evaluacion|mensaje/, 'message'], [/restaurante|identidad|regla|configura/, 'settings'], [/historial|vigencia/, 'history'], [/reporte|resultado|venta/, 'chart'], [/plan|modulo/, 'layers'], [/respaldo/, 'db']] as const) if (pattern.test(v)) return icon; return 'file'; }
export function SectionTitle({ children, icon }: { children: React.ReactNode; icon?: string }) { return <div className="card-h"><div className="ph"><span className="ic"><UiIcon name={icon || sectionIcon(text(children))} /></span><div><h3>{children}</h3></div></div></div>; }
export function PanelHeading({ title, note, action, icon }: { title: React.ReactNode; note?: React.ReactNode; action?: React.ReactNode; icon?: string }) { return <div className="card-h panel-title"><div className="ph"><span className="ic"><UiIcon name={icon || sectionIcon(text(title))} /></span><div><h3>{title}</h3>{note && <p>{note}</p>}</div></div>{action && <div className="ph-act">{action}</div>}</div>; }
export function Card({ title, note, icon, action, children, className = '' }: { title?: React.ReactNode; note?: React.ReactNode; icon?: string; action?: React.ReactNode; children?: React.ReactNode; className?: string }) { return <section className={'card panel ' + className}>{title && <PanelHeading title={title} note={note} icon={icon} action={action} />}{children}</section>; }
const statusInfo: Record<string, [string, string]> = { free: ['green', 'check'], occupied: ['blue', 'clock'], reserved: ['purple', 'calendar'], cleaning: ['purple', 'spark'], held: ['neutral', 'clock'], received: ['purple', 'clock'], preparing: ['amber', 'flame'], ready: ['green', 'chef'], delivered: ['green', 'check'], closed: ['green', 'check'], cancelled: ['red', 'close'], unavailable: ['red', 'alert'], delayed: ['red', 'alert'], pending: ['amber', 'clock'], open: ['blue', 'receipt'], confirmed: ['green', 'check'], proposed: ['purple', 'calendar'], rejected: ['red', 'close'], approved: ['green', 'shield'], attended: ['green', 'check'], no_show: ['red', 'close'], active: ['green', 'check'], suspended: ['red', 'close'], revoked: ['neutral', 'close'], expired: ['amber', 'clock'], answered: ['green', 'message'], unknown: ['neutral', 'shield'], failed: ['red', 'alert'], uncertain: ['amber', 'alert'], submitted: ['blue', 'clock'], submitting: ['blue', 'clock'], printed: ['green', 'check'], 'pending-review': ['amber', 'cloud'], 'pending-branch': ['amber', 'clock'], 'validation-blocked': ['amber', 'shield'], 'sandbox-stamped': ['purple', 'file'], 'sandbox-cancelled': ['neutral', 'close'], 'pending-reconciliation': ['purple', 'wallet'], 'accepted-local': ['blue', 'check'], 'rejected-local': ['red', 'close'], assigned: ['purple', 'users'], 'out-for-delivery': ['amber', 'truck'], 'picked-up': ['green', 'check'], reconciled: ['green', 'check'], bill: ['blue', 'receipt'], merged: ['neutral', 'split'], 'sender-approved': ['purple', 'swap'], simulated: ['blue', 'spark'], 'simulated-stamped': ['purple', 'file'], 'simulated-rejected': ['red', 'alert'], 'simulated-cancelled': ['neutral', 'close'], 'simulated-sent': ['green', 'check'], declined: ['red', 'close'], reversed: ['amber', 'refresh'], paid: ['green', 'check'], refunded: ['amber', 'refresh'], 'handed-off': ['green', 'truck'], posted: ['green', 'check'], accepted: ['blue', 'check'] };
export function statusTone(value: string) { return (statusInfo[value] || ['neutral'])[0]; }
export function StatusBadge({ value, label, className = '' }: { value: string; label?: string; className?: string }) { const [tone, icon] = statusInfo[value] || ['neutral', 'file']; return <span className={'badge ui-status tone-' + tone + ' ' + value + ' ' + className}><UiIcon name={icon} />{label || statusLabel(value)}</span>; }
export function Pill({ tone = 'neutral', icon, children, className = '' }: { tone?: string; icon?: string; children: React.ReactNode; className?: string }) { return <span className={'ui-status tone-' + tone + ' ' + className}>{icon && <UiIcon name={icon} />}{children}</span>; }
export function Sq({ icon, tone }: { icon: string; tone?: string }) { return <span className={'sq' + (tone ? ' tone-' + tone : '')}><UiIcon name={icon} /></span>; }
export function Kpis({ children, count }: { children: React.ReactNode; count?: number }) { const n = count || React.Children.toArray(children).length; return <section className="kpis" style={{ '--n': n } as React.CSSProperties}>{children}</section>; }
export function MetricCard({ label, value, note, icon, tone = 'blue', className = '' }: { label: string; value: React.ReactNode; note?: React.ReactNode; icon: string; tone?: string; className?: string }) { return <div className={'kpi stat ui-metric ' + className} data-tone={tone}><span className="kpi-top ui-metric-top"><span>{label}</span><span className={'sq tone-' + tone}><UiIcon name={icon} /></span></span><strong>{value}</strong>{note !== undefined && <span className="kpi-note">{note}</span>}</div>; }
export function Empty({ title, text, icon = 'check', action }: { title: string; text?: React.ReactNode; icon?: string; action?: React.ReactNode }) { return <div className="empty"><Sq icon={icon} /><h3>{title}</h3>{text && <p>{text}</p>}{action}</div>; }
export function Notice({ tone = 'info', icon, children }: { tone?: 'info' | 'ok' | 'warn'; icon?: string; children: React.ReactNode }) { return <div className={'notice ' + tone}><Sq icon={icon || (tone === 'warn' ? 'alert' : tone === 'ok' ? 'shield' : 'info')} /><span>{children}</span></div>; }
export function Switch({ checked, label, onChange, disabled, hideLabel }: { checked: boolean; label?: string; onChange?: () => void; disabled?: boolean; hideLabel?: boolean }) { return <button type="button" role="switch" aria-checked={checked} aria-label={label} className="switch" disabled={disabled} onClick={onChange}><i />{label && !hideLabel && <span>{label}</span>}</button>; }
// Columns that start with ">" are numeric: right-aligned with tabular figures.
export function DataTable({ columns, rows, foot, empty = 'Sin registros todavía. Las operaciones guardadas aparecerán aquí.' }: { columns: string[]; rows: React.ReactNode[][]; foot?: React.ReactNode[]; empty?: string }) {

    const right = columns.map(c => c.startsWith('>'));
    return <div className="table-scroll"><table><thead><tr>{columns.map((c, i) => <th key={i} scope="col" className={right[i] ? 'r' : ''}>{c.replace(/^>/, '')}</th>)}</tr></thead><tbody>{rows.map((r, i) => <tr key={i}>{r.map((c, j) => <td key={j} className={right[j] ? 'r num' : ''}>{c}</td>)}</tr>)}</tbody>{foot && <tfoot><tr>{foot.map((c, j) => <td key={j} className={right[j] ? 'r num' : ''}>{c}</td>)}</tr></tfoot>}</table>{!rows.length && <p className="empty-table">{empty}</p>}</div>;

}
export function Bar({ value, max = 1, tone = '', marker }: { value: number; max?: number; tone?: string; marker?: number }) { const pct = max > 0 ? Math.max(0, Math.min(100, value / max * 100)) : 0; return <div className="bar"><i className={tone} style={{ width: pct + '%' }} />{marker !== undefined && <s style={{ left: Math.min(100, marker / max * 100) + '%' }} />}</div>; }
// Page components place their header actions here; main.tsx provides the element next to the title.
export const ToolsSlot = React.createContext<HTMLElement | null>(null);
export function PageTools({ children }: { children: React.ReactNode }) { const el = React.useContext(ToolsSlot); return el ? createPortal(children, el) : null; }
function niceMax(max: number) { if (max <= 0) return 4; const raw = max / 4, mag = 10 ** Math.floor(Math.log10(raw)); const step = [1, 2, 2.5, 5, 10].map(m => m * mag).find(s => s >= raw) || 10 * mag; let top = Math.ceil(max / step) * step; if (top <= max) top += step; return top; }
export function BarChart({ labels, values, highlight, format = (v: number) => String(v), tick, height = 220 }: { labels: string[]; values: number[]; highlight?: number; format?: (v: number) => string; tick?: (v: number) => string; height?: number }) {

    const top = niceMax(Math.max(0, ...values)), ticks = [4, 3, 2, 1, 0].map(i => top * i / 4);
    return <div className="barchart" style={{ height }} role="img" aria-label={labels.map((l, i) => l + ': ' + format(values[i])).join(', ')}>
        <div className="bc-grid">{ticks.map(t => <div className="bc-line" key={t}><span>{(tick || format)(t)}</span></div>)}</div>
        <div className="bc-bars">{values.map((v, i) => <div className="bc-col" key={i} title={labels[i] + ': ' + format(v)}><div className="bc-track"><div className={'bc-bar' + (i === highlight ? ' on' : '')} style={{ height: (v / top * 100) + '%', animationDelay: i * 40 + 'ms' }} /></div><small>{labels[i]}</small></div>)}</div>
    </div>;

}
