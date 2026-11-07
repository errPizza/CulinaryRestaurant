import React, { useState } from 'react';
import { Button, Select } from './ui';
import { DialogFrame } from './motion';
import { SecretInput } from './secret-input';

// What a manager needs to switch an integration on: what to contract, where each value comes from, the values, and the
// server's check. The values go once to the branch server, which seals them; no screen shows them again.
type Values = Record<string, any>;
type Field = { key: string; label: string; type?: 'text' | 'secret' | 'select' | 'number' | 'checkbox' | 'lines'; options?: { value: string; label: string }[]; hint?: string; placeholder?: string; required?: boolean; value?: any; when?: (v: Values) => boolean };
type Guide = { need: string[]; steps: string[]; fields: Field[]; build: (v: Values) => any; after?: string };
const trim = (v: any) => String(v ?? '').trim();
const slug = (text: string, i: number) => (trim(text).toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 36) || 'terminal') + '-' + (i + 1);
const PRODUCT = { productCode: '90101501', unitCode: 'E48', taxObject: '02' };
const TEMPLATES = { dispatch: 'pedido_en_camino', 'pickup-ready': 'pedido_listo', paylink: 'liga_de_pago', 'owner-notice': 'aviso_sucursal', test: 'mensaje_de_prueba' };

export const GUIDES: Record<string, Guide> = {
    pac: {
        need: ['Cuenta de Facturama con acceso a la API (plan Multiemisor o API).', 'El CSD (sellos digitales) del negocio, que el dueño carga desde Culinary Dueño.', 'Las claves SAT de tus productos revisadas con tu contador.'],
        steps: ['Entra a facturama.mx › Configuración › API y copia el usuario y la contraseña de API.', 'Empieza en «Pruebas (sandbox)»: los timbres no tienen validez fiscal. Cambia a «Producción» cuando tu contador lo apruebe.', 'Escribe el código postal del lugar de expedición tal como aparece en la Constancia de Situación Fiscal.'],
        fields: [
            { key: 'environment', label: 'Ambiente', type: 'select', value: 'sandbox', options: [{ value: 'sandbox', label: 'Pruebas (sandbox)' }, { value: 'production', label: 'Producción' }] },
            { key: 'username', label: 'Usuario de API', required: true },
            { key: 'password', label: 'Contraseña de API', type: 'secret', required: true },
            { key: 'expeditionPlace', label: 'Código postal de expedición', required: true, placeholder: '06600' },
            { key: 'emailReceiver', label: 'Enviar la factura por correo al cliente', type: 'checkbox', value: true },
        ],
        build: v => ({ provider: 'facturama', environment: v.environment, username: trim(v.username), password: v.password, emailReceiver: !!v.emailReceiver, issuer: { expeditionPlace: trim(v.expeditionPlace), product: PRODUCT, products: {}, delivery: PRODUCT, paymentForms: { card: '04' } } }),
        after: 'Después, en Ajustes, elige la serie y si se timbra al crear la solicitud.',
    },
    terminal: {
        need: ['Terminales Mercado Pago Point o Clip vinculadas a la cuenta del restaurante, o una terminal BBVA.', 'El token de producción de Mercado Pago, o la API key y la secret key de Clip.'],
        steps: ['Mercado Pago: entra a mercadopago.com.mx/developers › Tus integraciones › Credenciales de producción y copia el Access Token.', 'Clip: en el portal de Clip › Desarrolladores, genera la API key y la secret key.', 'Anota el número de serie de cada terminal (viene en la etiqueta trasera o en Ajustes de la terminal).', 'BBVA no se conecta: se cobra en la terminal y se registra el voucher; basta con darla de alta aquí.'],
        fields: [
            { key: 'provider', label: 'Proveedor', type: 'select', value: 'mercadopago', options: [{ value: 'mercadopago', label: 'Mercado Pago Point' }, { value: 'clip', label: 'Clip' }, { value: 'bbva', label: 'BBVA (registro de voucher)' }] },
            { key: 'accessToken', label: 'Access Token de producción', type: 'secret', required: true, when: v => v.provider === 'mercadopago' },
            { key: 'apiKey', label: 'API key', type: 'secret', required: true, when: v => v.provider === 'clip' },
            { key: 'secretKey', label: 'Secret key', type: 'secret', required: true, when: v => v.provider === 'clip' },
            { key: 'terminals', label: 'Terminales', type: 'lines', required: true, placeholder: 'Caja 1 | PAX_A910__SMARTPOS1234567890', hint: 'Una por renglón: nombre | número de serie. En BBVA basta el nombre.' },
        ],
        build: v => {
            const terminals = trim(v.terminals).split('\n').map(l => l.split('|').map(trim)).filter(([name]) => name).map(([name, deviceId], i) => ({ id: slug(name, i), name, provider: v.provider, ...(v.provider === 'bbva' ? {} : { deviceId: deviceId || '' }) }));
            return { ...(v.provider === 'mercadopago' ? { mercadopago: { accessToken: v.accessToken } } : v.provider === 'clip' ? { clip: { apiKey: v.apiKey, secretKey: v.secretKey } } : {}), terminals };
        },
    },
    onlinepay: {
        need: ['Cuenta de Stripe verificada a nombre del negocio.', 'Una página de agradecimiento a la que vuelve el cliente después de pagar.'],
        steps: ['Entra a dashboard.stripe.com › Desarrolladores › Claves de API.', 'Copia la clave secreta (empieza con sk_live_). Para probar puedes usar una sk_test_.', 'Escribe la dirección completa de tu página de agradecimiento.'],
        fields: [
            { key: 'secretKey', label: 'Clave secreta de Stripe', type: 'secret', required: true },
            { key: 'successUrl', label: 'Página después del pago', required: true, placeholder: 'https://turestaurante.mx/gracias' },
        ],
        build: v => ({ provider: 'stripe', secretKey: v.secretKey, successUrl: trim(v.successUrl) }),
    },
    messaging: {
        need: ['Un número de WhatsApp Business en Meta con las plantillas aprobadas (pedido en camino, pedido listo, liga de pago, aviso a la sucursal y mensaje de prueba).', 'Opcional: un buzón de correo con acceso SMTP para enviar facturas y avisos.'],
        steps: ['En business.facebook.com › WhatsApp Manager copia el identificador del número.', 'En Usuarios del sistema crea un token permanente con permiso whatsapp_business_messaging.', 'Para el correo, tu proveedor te da servidor, puerto, usuario y contraseña SMTP.'],
        fields: [
            { key: 'phoneNumberId', label: 'Identificador del número de WhatsApp', required: true },
            { key: 'accessToken', label: 'Token permanente', type: 'secret', required: true },
            { key: 'smtp', label: 'También enviar por correo', type: 'checkbox', value: false },
            { key: 'host', label: 'Servidor SMTP', required: true, placeholder: 'smtp.turestaurante.mx', when: v => v.smtp },
            { key: 'port', label: 'Puerto', type: 'number', value: 587, when: v => v.smtp },
            { key: 'username', label: 'Usuario', required: true, when: v => v.smtp },
            { key: 'password', label: 'Contraseña del buzón', type: 'secret', required: true, when: v => v.smtp },
            { key: 'from', label: 'Remitente', required: true, placeholder: 'avisos@turestaurante.mx', when: v => v.smtp },
            { key: 'fromName', label: 'Nombre del remitente', when: v => v.smtp },
        ],
        build: v => ({ whatsapp: { phoneNumberId: trim(v.phoneNumberId), accessToken: v.accessToken, apiVersion: 'v23.0', countryCode: '52', templates: Object.fromEntries(Object.entries(TEMPLATES).map(([k, name]) => [k, { name, language: 'es_MX' }])) }, ...(v.smtp ? { smtp: { host: trim(v.host), port: Number(v.port) || 587, username: trim(v.username), password: v.password, from: trim(v.from), fromName: trim(v.fromName) || trim(v.from), implicitTls: Number(v.port) === 465 } } : {}) }),
        after: 'En Ajustes indica el correo y el WhatsApp del dueño para reenviarle los avisos.',
    },
    aggregators: {
        need: ['Contrato con Deliverect conectado a Uber Eats, Rappi o DiDi Food.', 'La Central de Culinary publicada en internet para recibir los pedidos (la configura soporte).'],
        steps: ['Pide a Deliverect las credenciales de API: client ID y client secret.', 'Copia el Account ID y el Location ID de tu sucursal en el panel de Deliverect.'],
        fields: [
            { key: 'clientId', label: 'Client ID', required: true },
            { key: 'clientSecret', label: 'Client secret', type: 'secret', required: true },
            { key: 'accountId', label: 'Account ID', required: true },
            { key: 'locationId', label: 'Location ID', required: true },
        ],
        build: v => ({ provider: 'deliverect', baseUrl: 'https://api.deliverect.com', clientId: trim(v.clientId), clientSecret: v.clientSecret, accountId: trim(v.accountId), locationId: trim(v.locationId) }),
    },
    pms: {
        need: ['Que el hotel habilite el acceso a su sistema: la API de Cloudbeds o Oracle OHIP para Opera.', 'El código de cargo que el hotel asignó al restaurante.'],
        steps: ['Cloudbeds: el hotel crea una API key en Cloudbeds › Marketplace › API Credentials y te da el Property ID.', 'Opera: el hotel registra la aplicación en el portal de OHIP y te entrega gateway, client ID, client secret, app key, enterprise ID y hotel ID.', 'Define el crédito máximo por cargo a habitación.'],
        fields: [
            { key: 'system', label: 'Sistema del hotel', type: 'select', value: 'cloudbeds', options: [{ value: 'cloudbeds', label: 'Cloudbeds' }, { value: 'opera', label: 'Oracle Opera (OHIP)' }] },
            { key: 'apiKey', label: 'API key de Cloudbeds', type: 'secret', required: true, when: v => v.system === 'cloudbeds' },
            { key: 'propertyId', label: 'Property ID', required: true, when: v => v.system === 'cloudbeds' },
            { key: 'gatewayUrl', label: 'Gateway de OHIP', required: true, placeholder: 'https://…', when: v => v.system === 'opera' },
            { key: 'clientId', label: 'Client ID', required: true, when: v => v.system === 'opera' },
            { key: 'clientSecret', label: 'Client secret', type: 'secret', required: true, when: v => v.system === 'opera' },
            { key: 'appKey', label: 'App key', type: 'secret', required: true, when: v => v.system === 'opera' },
            { key: 'enterpriseId', label: 'Enterprise ID', required: true, when: v => v.system === 'opera' },
            { key: 'hotelId', label: 'Hotel ID', required: true, when: v => v.system === 'opera' },
            { key: 'transactionCode', label: 'Código de cargo', required: true, value: '2000', when: v => v.system === 'opera' },
            { key: 'creditLimit', label: 'Crédito máximo por cargo (MXN)', type: 'number', value: 5000 },
        ],
        build: v => ({ system: v.system, creditLimit: Number(v.creditLimit) || 5000, itemName: 'Consumo en restaurante', ...(v.system === 'cloudbeds' ? { cloudbeds: { apiKey: v.apiKey, propertyId: trim(v.propertyId) } } : { opera: { gatewayUrl: trim(v.gatewayUrl), tokenUrl: trim(v.gatewayUrl).replace(/\/$/, '') + '/oauth/v1/tokens', grantType: 'client_credentials', clientId: trim(v.clientId), clientSecret: v.clientSecret, scope: 'urn:opc:hgbu:ws:__myscopes__', enterpriseId: trim(v.enterpriseId), appKey: v.appKey, hotelId: trim(v.hotelId), transactionCode: trim(v.transactionCode), cashierId: 1 } }) }),
    },
    maps: {
        need: ['Un proyecto de Google Cloud con facturación activa.', 'Una clave de API restringida a Geocoding API y Routes API.'],
        steps: ['En console.cloud.google.com › APIs y servicios, habilita Geocoding API y Routes API.', 'En Credenciales crea una clave de API y restríngela a esas dos API.', 'Copia la clave.'],
        fields: [{ key: 'apiKey', label: 'Clave de API de Google', type: 'secret', required: true }, { key: 'travelMode', label: 'Reparto en', type: 'select', value: 'TWO_WHEELER', options: [{ value: 'TWO_WHEELER', label: 'Moto' }, { value: 'DRIVE', label: 'Auto' }, { value: 'BICYCLE', label: 'Bicicleta' }] }],
        build: v => ({ apiKey: v.apiKey, region: 'mx', language: 'es', travelMode: v.travelMode }),
        after: 'En Ajustes marca la ubicación del restaurante y el radio de reparto.',
    },
    callerid: {
        need: ['Un conmutador Asterisk en la red de la sucursal.', 'Un usuario AMI de sólo lectura (lo crea quien instaló el conmutador).'],
        steps: ['Pide al técnico del conmutador la dirección, el puerto AMI con TLS (normalmente 5039), el usuario y su secreto.'],
        fields: [
            { key: 'host', label: 'Dirección del conmutador', required: true, placeholder: 'conmutador.local' },
            { key: 'port', label: 'Puerto', type: 'number', value: 5039 },
            { key: 'username', label: 'Usuario AMI', required: true },
            { key: 'secret', label: 'Secreto', type: 'secret', required: true },
        ],
        build: v => ({ host: trim(v.host), port: Number(v.port) || 5039, username: trim(v.username), secret: v.secret }),
    },
    printer: {
        need: ['Una impresora térmica ESC/POS de 80 mm o 58 mm, en red o por USB.', 'El cajón de dinero conectado a la impresora con su cable RJ11.'],
        steps: ['Imprime la hoja de configuración de la impresora (mantén presionado el botón FEED al encenderla) y anota su dirección IP.', 'Si es por USB, conéctala al servidor de la sucursal y elige «USB».'],
        fields: [
            { key: 'connection', label: 'Conexión', type: 'select', value: 'tcp', options: [{ value: 'tcp', label: 'Red (IP)' }, { value: 'usb', label: 'USB' }] },
            { key: 'ip', label: 'Dirección IP', required: true, placeholder: '192.168.1.50', when: v => v.connection === 'tcp' },
            { key: 'columns', label: 'Ancho del papel', type: 'select', value: '48', options: [{ value: '48', label: '80 mm' }, { value: '32', label: '58 mm' }] },
        ],
        build: v => ({ uri: v.connection === 'usb' ? 'file:///dev/usb/lp0' : 'tcp://' + trim(v.ip) + ':9100', columns: Number(v.columns) }),
    },
};

function Input({ field, value, set }: { field: Field; value: any; set: (v: any) => void }) {

    const id = 'setup-' + field.key;
    if (field.type === 'secret') return <SecretInput id={id} name={field.key} label={field.label} value={value ?? ''} onChange={set} required={field.required} autoComplete="new-password" />;
    if (field.type === 'checkbox') return <label className="check"><input type="checkbox" checked={!!value} onChange={e => set(e.target.checked)} />{field.label}</label>;
    return <label htmlFor={id}>{field.label}
        {field.type === 'select' ? <Select id={id} value={value} onChange={e => set(e.target.value)}>{field.options!.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}</Select>
            : field.type === 'lines' ? <textarea id={id} rows={3} value={value ?? ''} placeholder={field.placeholder} required={field.required} onChange={e => set(e.target.value)} />
                : <input id={id} type={field.type === 'number' ? 'number' : 'text'} value={value ?? ''} placeholder={field.placeholder} required={field.required} autoComplete="off" spellCheck={false} onChange={e => set(e.target.value)} />}
        {field.hint && <small>{field.hint}</small>}</label>;

}
export function ActivateDialog({ provider, name, vendor, close, action, replacing }: { provider: string; name: string; vendor: string; close: () => void; action: (type: string, payload: any) => Promise<any>; replacing: boolean }) {

    const guide = GUIDES[provider];
    const [step, setStep] = useState(0), [busy, setBusy] = useState(false), [error, setError] = useState(''), [restarting, setRestarting] = useState(false);
    const [values, setValues] = useState<Values>(() => Object.fromEntries(guide.fields.map(f => [f.key, f.value ?? (f.type === 'checkbox' ? false : '')])));
    const shown = guide.fields.filter(f => !f.when || f.when(values));
    const save = async (e: React.FormEvent) => {

        e.preventDefault();
        if (busy) return;
        setBusy(true); setError('');
        try { const r = await action('integration.credentials', { provider, credentials: guide.build(values) }); setRestarting(!!r?.restarting); setValues({}); setStep(2); } catch (x: any) { setError(x.message); } finally { setBusy(false); }

    };
    const labels = ['Antes de empezar', 'Datos de conexión', 'Listo'];
    return <DialogFrame label={'Activar ' + name} close={close} busy={busy} className="setup-dialog">
        <div className="dialog-head"><div><h2>{replacing ? 'Cambiar datos de ' : 'Activar '}{name}</h2><p>{vendor}</p></div><Button className="ghost dialog-close" icon="close" onClick={close} disabled={busy} aria-label="Cerrar" /></div>
        <ol className="setup-progress" aria-label="Pasos">{labels.map((l, i) => <li key={l} className={i === step ? 'current' : i < step ? 'done' : ''} aria-current={i === step ? 'step' : undefined}>{l}</li>)}</ol>
        {step === 0 && <>
            <section className="setup-section"><h3>Qué necesitas</h3><ul>{guide.need.map(t => <li key={t}>{t}</li>)}</ul></section>
            <section className="setup-section"><h3>Dónde obtener los datos</h3><ol>{guide.steps.map(t => <li key={t}>{t}</li>)}</ol></section>
            <div className="dialog-actions"><Button type="button" className="ghost" icon={false} onClick={close}>Cancelar</Button><Button className="primary" icon="arrow" onClick={() => setStep(1)}>Ya los tengo</Button></div>
        </>}
        {step === 1 && <form className="setup-form" onSubmit={save} autoComplete="off">
            <div className="setup-fields">{shown.map(f => <Input key={f.key} field={f} value={values[f.key]} set={v => setValues(x => ({ ...x, [f.key]: v }))} />)}</div>
            <p className="dialog-note">Se guardan cifrados en el servidor de la sucursal y no se vuelven a mostrar. Sólo verás los últimos cuatro caracteres.</p>
            {error && <div className="alert error" role="alert">{error}</div>}
            <div className="dialog-actions"><Button type="button" className="ghost" icon={false} disabled={busy} onClick={() => setStep(0)}>Atrás</Button><Button className="primary" icon="check" disabled={busy}>{busy ? 'Comprobando…' : 'Guardar y conectar'}</Button></div>
        </form>}
        {step === 2 && <>
            <div className="setup-done" role="status"><b>Datos guardados</b><span>{restarting ? 'El servidor está reiniciando sus conexiones; en unos segundos la tarjeta mostrará si el proveedor responde.' : 'La integración quedó activa.'}</span>{guide.after && <span>{guide.after}</span>}</div>
            <div className="dialog-actions"><Button className="primary" icon={false} onClick={close}>Listo</Button></div>
        </>}
    </DialogFrame>;

}
