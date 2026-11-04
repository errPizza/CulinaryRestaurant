import { Button, PanelHeading, Pill, Empty, DataTable } from './ui';
import { money } from './backoffice';

type Row = { id: string;[key: string]: any };
type Act = (type: string, payload: any) => Promise<any>;
const list = (s: any, kind: string): Row[] => s[kind + 's'] || [];
const time = (s: string) => s ? new Date(s).toLocaleString('es-MX', { weekday: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '—';
const local = (s: string) => { const d = s ? new Date(s) : new Date(); return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16); };
const minutes = (a: Row) => Math.max(0, Math.floor(((a.outAt ? Date.parse(a.outAt) : Date.now()) - Date.parse(a.inAt)) / 60000));
const hours = (m: number) => `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, '0')} min`;

// The time clock as the manager sees it: who is in now, the shifts of the last week and the corrections.
export function AttendancePanel({ state: s, form, action, run }: { state: any; form: any; action: Act; run: any }) {

    const users = list(s, 'user'), me = s.user?.id;
    const name = (id: string) => users.find(w => w.id === id)?.name || '—';
    const wage = (id: string) => users.find(w => w.id === id)?.hourlyWage || 0;
    const week = Date.now() - 7 * 86400000;
    const shifts = list(s, 'attendance').filter(a => !a.outAt || Date.parse(a.inAt) >= week).sort((a, b) => (b.inAt || '').localeCompare(a.inAt || ''));
    const open = shifts.filter(a => !a.outAt);
    const off = users.filter(w => w.active && w.role !== 'dueno' && !open.some(a => a.userId === w.id));
    const fix = (a?: Row) => form(a ? 'Corregir turno de ' + name(a.userId) : 'Registrar turno olvidado', [...(a ? [] : [{ key: 'userId', label: 'Empleado', type: 'select', value: users.find(w => w.active && w.id !== me)?.id, options: users.filter(w => w.active && w.id !== me).map(w => ({ value: w.id, label: w.name })) }]), { key: 'inAt', label: 'Entrada', type: 'datetime-local', value: local(a?.inAt || ''), required: true }, { key: 'outAt', label: 'Salida', type: 'datetime-local', value: local(a?.outAt || ''), required: true }, { key: 'reason', label: 'Motivo de la corrección', required: true, placeholder: 'Ej. Olvidó registrar su salida' }], 'clock.fix', a ? { id: a.id } : {}, 'Tus propias horas las corrige otro gerente.', (x: any) => ({ ...x, inAt: new Date(x.inAt).toISOString(), outAt: new Date(x.outAt).toISOString() }));
    const clockIn = () => form('Registrar entrada', [{ key: 'userId', label: 'Empleado', type: 'select', value: off[0]?.id, options: off.map(w => ({ value: w.id, label: w.name })) }], 'clock.in', {}, 'Para quien no pudo registrarla desde su dispositivo.');
    const cost = (a: Row) => Math.round(minutes(a) * wage(a.userId) / 60);

    return <section className="card panel"><PanelHeading title="Reloj checador" note={open.length ? `${open.length} en turno ahora · ${money(open.reduce((t, a) => t + wage(a.userId), 0))} por hora` : 'Nadie en turno ahora'} icon="clock" action={<span className="row-actions"><Button className="ghost sm" icon="edit" onClick={() => fix()}>Turno olvidado</Button>{off.length > 0 && <Button className="ghost sm" icon="plus" onClick={clockIn}>Registrar entrada</Button>}</span>} />
        {shifts.length ? <DataTable columns={['Empleado', 'Entrada', 'Salida', '>Tiempo', '>Costo', '']} rows={shifts.map(a => [<b>{name(a.userId)}</b>, time(a.inAt), a.outAt ? <span>{time(a.outAt)}{a.fixedBy && <Pill tone="amber">Corregido</Pill>}</span> : <Pill tone="green">En turno</Pill>, hours(minutes(a)), wage(a.userId) ? money(cost(a)) : <span className="no">Sin sueldo</span>, <span className="row-actions">{!a.outAt && <Button className="ghost xs" icon="exit" onClick={() => run(() => action('clock.out', { userId: a.userId }))}>Salida</Button>}{a.userId !== me && <Button className="ghost xs" icon="edit" onClick={() => fix(a)}>Corregir</Button>}</span>])} />
            : <Empty icon="clock" title="Sin turnos esta semana" text="Cada empleado registra su entrada y salida desde su menú de perfil." />}
    </section>;

}
