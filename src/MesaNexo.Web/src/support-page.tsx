import { useState } from 'react';
import { UiIcon, Button, PanelHeading, StatusBadge, Sq, Empty } from './ui';
type Row = { id: string;[key: string]: any };
const date = (v: string) => v ? new Date(v).toLocaleString('es-MX', { dateStyle: 'medium', timeStyle: 'short' }) : 'Sin fecha registrada';
export function SupportPage({ state: s, form, confirm, action }: { state: any; form: any; confirm: any; action: any }) {

    const [filter, setFilter] = useState('all'), [expanded, setExpanded] = useState<string | null>(null);
    const tickets: Row[] = s.tickets || [], license = s.restaurant.license || {};
    const plan = ({ trial: 'Prueba', monthly: 'Mensual', annual: 'Anual', permanent: 'Permanente' } as Record<string, string>)[license.plan] || 'Sin licencia';
    const selected = tickets.filter(t => filter === 'all' || t.status === filter).slice().reverse();
    const openCount = tickets.filter(t => t.status !== 'closed').length;
    return <div className="support-page stack">
        <div className="g g2 support-top">
            <section className="card coverage-card"><div style={{ display: 'flex', alignItems: 'center', gap: 16 }}><Sq icon="key" tone="green" /><div className="ph" style={{ flex: 1 }}><div><h2>Licencia {plan}</h2><p>{license.expiresAt ? 'Vigente hasta ' + date(license.expiresAt) : 'Sin fecha de vencimiento'}{license.validatedAt ? ' · validada ' + date(license.validatedAt) : ''}</p></div></div><StatusBadge value="active" label="Vigente" /></div></section>
            <section className="card coverage-card"><div style={{ display: 'flex', alignItems: 'center', gap: 16 }}><Sq icon="message" tone="blue" /><div className="ph" style={{ flex: 1 }}><div><h2>{openCount} solicitud{openCount === 1 ? '' : 'es'} abierta{openCount === 1 ? '' : 's'}</h2><p>El historial y las respuestas quedan registrados</p></div></div></div></section>
        </div>
        <div className="g g-main support-layout">
            <section className="card panel support-tickets"><PanelHeading title="Tus solicitudes" note="Consulta el seguimiento y agrega información" icon="message" />
                <div className="utabs ui-list-tabs" role="group" aria-label="Filtrar solicitudes">{[['all', 'Todas'], ['open', 'En espera'], ['answered', 'Respondidas'], ['closed', 'Cerradas']].map(([key, name]) => <Button key={key} icon={false} className={filter === key ? 'active' : ''} aria-pressed={filter === key} onClick={() => setFilter(key)}>{name}<span className="ui-count">{key === 'all' ? tickets.length : tickets.filter(t => t.status === key).length}</span></Button>)}</div>
                <div className="rows">{selected.map(t => <article className="ticket r" key={t.id} style={{ alignItems: 'flex-start' }}><Sq icon={t.status === 'closed' ? 'check' : 'message'} tone={t.status === 'closed' ? 'green' : 'blue'} /><div className="w"><b>{t.subject}</b><span>{date(t.at)} · {t.remoteId ? 'Enviada a Culinary' : 'Pendiente de enviar'} · {(t.messages || []).length} mensaje{(t.messages || []).length === 1 ? '' : 's'}</span>
                    <p style={{ margin: '8px 0 10px', color: 'var(--text)', whiteSpace: 'normal' }}>{t.messages?.[0]?.message || 'Sin descripción registrada.'}</p>
                    {expanded === t.id && (t.messages || []).slice(1).map((m: any, i: number) => <div className="ticket-message" key={m.id || i} style={{ background: 'var(--paper-2)', borderRadius: 10, padding: '10px 12px', boxShadow: 'inset 0 0 0 1px var(--line-soft)', marginBottom: 8 }}><b style={{ fontSize: 12 }}>{m.name} · {date(m.at)}</b><p style={{ color: 'var(--text)', whiteSpace: 'normal' }}>{m.message}</p></div>)}
                    <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}><Button className="text-button" icon="message" onClick={() => { setExpanded(t.id); form('Responder solicitud', [{ key: 'message', label: 'Respuesta', type: 'textarea', required: true }], 'ticket.reply', { id: t.id }); }}>Responder</Button>{(t.messages || []).length > 1 && <Button className="text-button" icon="eye" aria-expanded={expanded === t.id} onClick={() => setExpanded(expanded === t.id ? null : t.id)}>{expanded === t.id ? 'Ocultar conversación' : 'Ver conversación'}</Button>}</div></div><StatusBadge value={t.status} /></article>)}</div>
                {!selected.length && <Empty icon="message" title={tickets.length ? 'No hay solicitudes con este filtro' : 'Sin solicitudes de soporte'} text={tickets.length ? 'Prueba con otra pestaña.' : 'Crea una solicitud para describir lo que necesitas.'} />}
            </section>
            <aside className="stack support-aside">
                <section className="card panel support-advice"><PanelHeading title="Antes de enviar" note="Ayúdanos a entender lo que sucede" icon="file" /><div className="rows"><div className="r"><Sq icon="message" /><div className="w"><b>Describe el problema</b><span>Indica la pantalla y el paso que realizaste</span></div></div><div className="r"><Sq icon="shield" /><div className="w"><b>Cuida tus datos</b><span>No incluyas contraseñas ni códigos de alta</span></div></div></div></section>
            </aside>
        </div>
    </div>;

}
