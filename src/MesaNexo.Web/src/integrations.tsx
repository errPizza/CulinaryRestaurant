import React,{useState} from 'react';
import {Button,PanelHeading,Kpis,MetricCard,Pill,Sq,Empty,Notice,DataTable,Switch,UiIcon,Select} from './ui';
import {DialogFrame} from './motion';
import {cents} from './bridge';
import {money,type Dialog} from './backoffice';

// Third parties: off, simulated or live. Live is offered only when the server reports valid credentials for the provider
// (connection.integrations); the domain checks it again.
type Row={id:string;[key:string]:any};
type Ctx={close:()=>void;submit:(fn:()=>Promise<any>)=>void;busy:boolean;error:string};
type Act=(type:string,payload:any)=>Promise<any>;
const list=(s:any,kind:string):Row[]=>s[kind+'s']||[];
// Snapshot lists come in storage order; anything shown as "latest" is sorted by time first.
const byTime=(rows:Row[],key='at')=>rows.slice().sort((a,b)=>Date.parse(a[key])-Date.parse(b[key]));
const clock=(s:string)=>s?new Date(s).toLocaleTimeString('es-MX',{hour:'2-digit',minute:'2-digit',hour12:false}):'—';
const stamp=(s:string)=>s?new Date(s).toLocaleString('es-MX',{day:'numeric',month:'short',hour:'2-digit',minute:'2-digit',hour12:false}):'—';
const isToday=(s:string)=>!!s&&new Date(s).toDateString()===new Date().toDateString();
const plural=(n:number,one:string,many:string)=>n+' '+(n===1?one:many);
export const simOn=(s:any,key:string)=>list(s,'integration').some(x=>x.id===key&&x.mode==='simulated');
export const serverState=(s:any,key:string):string=>s.connection?.integrations?.[key]||'sin credenciales';
// The printer and the PAC ran from their own file before modes existed: without a saved mode they are live when ready.
export const modeOf=(s:any,key:string):'off'|'simulated'|'live'=>{const row=list(s,'integration').find(x=>x.id===key);if(row)return row.mode==='live'&&serverState(s,key)==='sin credenciales'?'off':row.mode;return ['pac','printer'].includes(key)&&serverState(s,key)!=='sin credenciales'?'live':'off';};
export const liveOn=(s:any,key:string)=>modeOf(s,key)==='live';
export const activeOn=(s:any,key:string)=>modeOf(s,key)!=='off';
const settingsOf=(s:any,key:string)=>list(s,'integration').find(x=>x.id===key)?.settings||{};
export const PLATFORMS:Record<string,{name:string;tone:string}>={uber:{name:'Uber Eats',tone:'green'},rappi:{name:'Rappi',tone:'red'},didi:{name:'DiDi Food',tone:'amber'}};
export const PROVIDERS=[
 {key:'pac',name:'Facturación CFDI 4.0',vendor:'PAC · Facturama',icon:'file',what:'Timbra y cancela las solicitudes fiscales y aplica las reglas del SAT al receptor.',real:'cuenta de Facturama (sandbox o producción) con el CSD del emisor cargado en su panel, y claves SAT revisadas con el contador.'},
 {key:'terminal',name:'Terminal bancaria',vendor:'PIN pad · Mercado Pago Point, Clip o BBVA',icon:'card',what:'Cobra con tarjeta desde la cuenta y el Mostrador; una respuesta perdida se consulta antes de volver a cobrar.',real:'las terminales de la sucursal con el token de Mercado Pago o las llaves de Clip; BBVA se registra capturando la autorización del voucher.'},
 {key:'onlinepay',name:'Pagos en línea',vendor:'Stripe',icon:'cloud',what:'Genera ligas de pago para pedidos por teléfono o a domicilio y aplica el pago cuando el cliente paga.',real:'cuenta de Stripe verificada y su llave secreta.'},
 {key:'messaging',name:'WhatsApp y correo',vendor:'WhatsApp Cloud API · SMTP',icon:'message',what:'Avisa al cliente que su pedido va en camino o está listo, envía ligas y facturas, y reenvía al dueño los avisos de la sucursal.',real:'número de WhatsApp Business con plantillas aprobadas por Meta y un buzón SMTP para el correo.'},
 {key:'aggregators',name:'Plataformas de reparto',vendor:'Uber Eats · Rappi · DiDi Food vía Deliverect',icon:'truck',what:'Recibe los pedidos de las plataformas, los manda a cocina con su etiqueta y registra lo que pagará cada una.',real:'contrato con Deliverect, sus credenciales y la Central publicada en internet para recibir los pedidos.'},
 {key:'pms',name:'Hotel (PMS)',vendor:'Oracle Opera · Cloudbeds',icon:'key',what:'Carga el consumo a la habitación del huésped después de validar su apellido y el crédito autorizado.',real:'acceso a OHIP o a la API de Cloudbeds habilitado por el hotel y el código de cargo del restaurante.'},
 {key:'maps',name:'Mapas',vendor:'Google Maps',icon:'pin',what:'Ubica las direcciones de entrega y calcula distancia y tiempo estimado.',real:'clave de API restringida a Geocoding y Routes, con facturación activa en Google Cloud.'},
 {key:'callerid',name:'Identificador de llamadas',vendor:'Conmutador Asterisk',icon:'phone',what:'Muestra quién llama y lo relaciona con su ficha para tomar el pedido.',real:'conmutador con AMI sobre TLS y un usuario de sólo lectura.'},
 {key:'printer',name:'Impresora y cajón',vendor:'ESC/POS · RJ11',icon:'print',what:'Imprime cuentas y cortes, y abre el cajón al cobrar en efectivo.',real:'impresora térmica ESC/POS en red o USB, con el cajón conectado a ella por RJ11.'},
];
const FILES:Record<string,string>={pac:'pac.json (o fiscal.json)',printer:'printer.json'};
const fileOf=(key:string)=>'/etc/culinaryos/integrations/'+(FILES[key]||key+'.json');
const providerName=(key:string)=>PROVIDERS.find(p=>p.key===key)?.name||key;
const channelPill=(c:string)=><Pill tone={c==='whatsapp'?'green':'blue'} icon={c==='whatsapp'?'message':'file'}>{c==='whatsapp'?'WhatsApp':'Correo'}</Pill>;
const logTone:Record<string,string>={approved:'green',stamped:'green',posted:'green',paid:'green',accepted:'green',ok:'green','handed-off':'green',simulated:'blue',declined:'red',rejected:'red',failed:'red',uncertain:'amber',reversed:'amber',refunded:'amber',pending:'amber',ringing:'blue',cancelled:'neutral',off:'neutral',expired:'neutral'};

function configure(s:any,form:any,key:string){
 const c=settingsOf(s,key),base={provider:key,mode:modeOf(s,key)==='live'?'live':'simulated'};
 const pct=(bps:number)=>(bps??0)/100;
 switch(key){
 case 'pac':return form('Facturación simulada',[{key:'issuerRfc',label:'RFC del emisor',value:c.issuerRfc||'EKU9003173C9',required:true,hint:'EKU9003173C9 es el RFC de pruebas publicado por el SAT.'},{key:'issuerName',label:'Razón social',value:c.issuerName||s.restaurant?.name,full:true},{key:'series',label:'Serie',value:c.series||'A'},{key:'autoStamp',label:'Timbrar al crear la solicitud',type:'checkbox',value:!!c.autoStamp}],'integration.configure',base,'Los timbres simulados no tienen validez fiscal.',(x:any)=>({settings:x}));
 case 'terminal':return form('Terminal bancaria simulada',[{key:'terminalId',label:'Identificador de la terminal',value:c.terminalId||'SIM-01',required:true}],'integration.configure',base,undefined,(x:any)=>({settings:x}));
 case 'onlinepay':return form('Pagos en línea simulados',[{key:'provider',label:'Proveedor',type:'select',value:c.provider||'stripe',options:[{value:'stripe',label:'Stripe'},{value:'mercadopago',label:'Mercado Pago'}]},{key:'expiresMinutes',label:'Vigencia de la liga (minutos)',type:'number',value:c.expiresMinutes||60,min:5,max:1440}],'integration.configure',base,undefined,(x:any)=>({settings:{provider:x.provider,expiresMinutes:Number(x.expiresMinutes)||60}}));
 case 'messaging':return form('Mensajería simulada',[{key:'whatsapp',label:'WhatsApp',type:'checkbox',value:c.whatsapp!==false},{key:'email',label:'Correo',type:'checkbox',value:c.email!==false},{key:'ownerEmail',label:'Correo del dueño',value:c.ownerEmail||'',placeholder:'dueno@turestaurante.mx',hint:'Recibe los avisos de límite de efectivo, arqueos y conciliaciones.'},{key:'ownerPhone',label:'WhatsApp del dueño',value:c.ownerPhone||'',placeholder:'55 1234 5678'}],'integration.configure',base,'Ningún mensaje sale de la sucursal: quedan en la bandeja de salida.',(x:any)=>({settings:{whatsapp:!!x.whatsapp,email:!!x.email,ownerEmail:x.ownerEmail||'',ownerPhone:x.ownerPhone||''}}));
 case 'aggregators':return form('Plataformas de reparto simuladas',[{key:'platforms',label:'Plataformas',type:'multi',value:c.platforms||Object.keys(PLATFORMS),options:Object.entries(PLATFORMS).map(([value,p])=>({value,label:p.name}))},{key:'commission',label:'Comisión de la plataforma %',type:'number',value:pct(c.commissionBps??2500),min:0,max:50,step:'any'},{key:'autoAccept',label:'Aceptar pedidos automáticamente',type:'checkbox',value:!!c.autoAccept,hint:'Si no, gerencia los acepta en Delivery y clientes.'}],'integration.configure',base,undefined,(x:any)=>({settings:{platforms:x.platforms,commissionBps:Math.round(Number(x.commission||0)*100),autoAccept:!!x.autoAccept}}));
 case 'pms':return form('Hotel simulado',[{key:'system',label:'Sistema del hotel',type:'select',value:c.system||'opera',options:[{value:'opera',label:'Oracle Opera'},{value:'cloudbeds',label:'Cloudbeds'}]},{key:'rooms',label:'Huéspedes registrados',type:'rows',value:(c.rooms||[]).map((r:Row)=>[r.room,r.guest,r.limit?r.limit/100:''].join('|')).join('\n'),hint:'Simula lo que respondería el PMS. Crédito vacío = sin límite.',rows:{columns:[{key:'room',label:'Habitación',type:'text' as const,width:'110px',placeholder:'204'},{key:'guest',label:'Huésped',type:'text' as const,placeholder:'Nombre y apellidos'},{key:'limit',label:'Crédito',type:'number' as const,width:'120px',prefix:'$'}],split:(l:string)=>l.split('|'),join:(c:string[])=>c.join('|'),add:'Agregar habitación',empty:'Sin huéspedes: el PMS rechazará los cargos.'}}],'integration.configure',base,undefined,(x:any)=>({settings:{system:x.system,rooms:String(x.rooms||'').split('\n').map((l:string)=>l.split('|')).filter(([room,guest])=>room?.trim()&&guest?.trim()).map(([room,guest,limit])=>({room:room.trim(),guest:guest.trim(),limit:String(limit??'').trim()===''?0:cents(limit)}))}}));
 case 'maps':return form('Mapas simulados',[{key:'centerLat',label:'Latitud del restaurante',type:'number',value:c.centerLat??19.4326,step:'any',required:true},{key:'centerLon',label:'Longitud del restaurante',type:'number',value:c.centerLon??-99.1332,step:'any',required:true},{key:'radiusKm',label:'Radio de reparto (km)',type:'number',value:c.radiusKm||5,min:1,max:30}],'integration.configure',base,'El geocodificador simulado coloca cada dirección siempre en el mismo punto dentro del radio.',(x:any)=>({settings:{centerLat:String(x.centerLat),centerLon:String(x.centerLon),radiusKm:Number(x.radiusKm)||5}}));
 }
}
function usage(s:any,key:string){
 const logs=list(s,'integrationlog').filter(l=>l.provider===key),today=logs.filter(l=>isToday(l.at)).length,c=settingsOf(s,key);
 switch(key){
 case 'pac':return plural(list(s,'fiscal').filter(f=>f.status==='simulated-stamped').length,'timbre simulado','timbres simulados')+' · serie '+(c.series||'A')+(c.autoStamp?' · automático':'');
 case 'terminal':return (c.terminalId||'SIM-01')+' · '+plural(list(s,'terminaltxn').filter(t=>isToday(t.at)).length,'cobro hoy','cobros hoy');
 case 'onlinepay':return (c.provider==='mercadopago'?'Mercado Pago':'Stripe')+' · '+plural(list(s,'paylink').filter(l=>l.status==='pending').length,'liga vigente','ligas vigentes');
 case 'messaging':return plural(list(s,'message').length,'mensaje en bandeja','mensajes en bandeja')+(c.ownerEmail?' · dueño '+c.ownerEmail:'');
 case 'aggregators':return (c.platforms||[]).map((p:string)=>PLATFORMS[p]?.name).join(', ')+' · comisión '+((c.commissionBps??2500)/100)+' %';
 case 'pms':return (c.system==='cloudbeds'?'Cloudbeds':'Opera')+' · '+plural((c.rooms||[]).length,'habitación','habitaciones')+' · '+plural(list(s,'pmspost').length,'cargo','cargos');
 case 'maps':return 'Centro '+Number(c.centerLat??19.4326).toFixed(4)+', '+Number(c.centerLon??-99.1332).toFixed(4)+' · radio '+(c.radiusKm||5)+' km';
 case 'callerid':return plural(list(s,'call').filter(x=>isToday(x.at)).length,'llamada hoy','llamadas hoy');
 case 'printer':return plural(list(s,'printjob').filter(j=>j.status==='simulated').length,'trabajo simulado','trabajos simulados');
 }return plural(today,'operación hoy','operaciones hoy');
}
export function simulateCall(form:any){form('Simular llamada entrante',[{key:'number',label:'Número que llama',required:true,placeholder:'55 1234 5678'},{key:'name',label:'Nombre que envía la línea',placeholder:'Opcional'}],'callerid.simulate',{},'Aparece como llamada entrante en Delivery y clientes.');}
export function simulatePlatformOrder(s:any,form:any){const c=settingsOf(s,'aggregators');form('Simular pedido de plataforma',[{key:'platform',label:'Plataforma',type:'select',value:(c.platforms||['uber'])[0],options:(c.platforms||Object.keys(PLATFORMS)).map((p:string)=>({value:p,label:PLATFORMS[p]?.name||p}))},{key:'customerName',label:'Cliente',placeholder:'Al azar si lo dejas vacío'},{key:'notes',label:'Indicaciones',placeholder:'Ej. Sin cebolla'}],'aggregator.simulate',{},'Elige productos al azar del menú publicado.',(x:any)=>({platform:x.platform,customerName:x.customerName||'',notes:x.notes||''}));}

export function IntegrationsPage({state:s,form,action,run}:{state:any;form:any;action:Act;run:any}){
 const active=PROVIDERS.filter(p=>activeOn(s,p.key)),live=PROVIDERS.filter(p=>liveOn(s,p.key)),logs=byTime(list(s,'integrationlog')),messages=byTime(list(s,'message')),orders=list(s,'aggregatororder');
 const setMode=(key:string,mode:string)=>run(()=>action('integration.configure',{provider:key,mode}),mode==='off'?providerName(key)+' apagada':mode==='live'?providerName(key)+': conexión real activa':'Simulación de '+providerName(key)+' activa');
 const tests:Record<string,React.ReactNode>={
  messaging:<Button className="ghost xs" icon="message" onClick={()=>form('Mensaje de prueba',[{key:'channel',label:'Canal',type:'select',value:'whatsapp',options:[{value:'whatsapp',label:'WhatsApp'},{value:'email',label:'Correo'}]},{key:'to',label:'Destinatario',required:true,placeholder:'55 1234 5678 o correo'}],'message.test',{},'Queda en la bandeja de salida; no se envía a nadie.')}>Mensaje de prueba</Button>,
  aggregators:<><Button className="ghost xs" icon="plus" onClick={()=>simulatePlatformOrder(s,form)}>Simular pedido</Button><Button className="ghost xs" icon="upload" onClick={()=>run(()=>action('aggregator.sync',{}),'Menú publicado en las plataformas (simulado)')}>Publicar menú</Button></>,
  maps:<Button className="ghost xs" icon="search" onClick={()=>form('Probar dirección',[{key:'text',label:'Dirección',required:true,full:true,placeholder:'Calle, número, colonia, ciudad'}],'geo.lookup',{},undefined,(x:any)=>x)}>Probar dirección</Button>,
  callerid:<Button className="ghost xs" icon="phone" onClick={()=>simulateCall(form)}>Simular llamada</Button>,
 };
 const lastGeo=logs.filter(l=>l.provider==='maps').at(-1);
 return <>
  {live.length?<Notice tone="info" icon="plug"><b>Conexión real: {live.map(p=>p.name).join(', ')}.</b> Estas integraciones contactan al proveedor, mueven dinero o emiten documentos de verdad. Las demás siguen apagadas o en simulación.</Notice>
  :<Notice tone="warn" icon="shield"><b>Todo está en simulación.</b> No se contacta a ningún proveedor, no se mueve dinero y los timbres no tienen validez fiscal. Para conectar una de verdad, guarda sus credenciales en el servidor y elige «Real» en su tarjeta.</Notice>}
  <Kpis>
   <MetricCard className="lead" label="Integraciones activas" value={<>{active.length} <small>/ {PROVIDERS.length}</small></>} note={active.length?plural(live.length,'real','reales')+' · '+plural(active.length-live.length,'simulada','simuladas'):'Activa las que quieras usar'} icon="plug" tone="amber"/>
   <MetricCard label="Operaciones hoy" value={logs.filter(l=>isToday(l.at)).length} note={logs.length?'Última: '+stamp(logs.at(-1)!.at):'Sin operaciones todavía'} icon="history" tone="blue"/>
   <MetricCard label="Bandeja de salida" value={messages.filter(m=>isToday(m.at)).length} note={plural(messages.filter(m=>m.channel==='whatsapp').length,'WhatsApp','WhatsApp')+' · '+plural(messages.filter(m=>m.channel==='email').length,'correo','correos')} icon="message" tone="green"/>
   <MetricCard label="Plataformas hoy" value={money(orders.filter(o=>isToday(o.receivedAt)&&o.net!=null).reduce((t,o)=>t+o.net,0))} note={plural(orders.filter(o=>isToday(o.receivedAt)).length,'pedido','pedidos')+' · neto de comisión'} icon="truck" tone="purple"/>
  </Kpis>
  <section className="integration-grid stagger" aria-label="Integraciones">{PROVIDERS.map(p=>{const mode=modeOf(s,p.key),on=mode!=='off',sim=mode==='simulated',server=serverState(s,p.key),ready=server==='lista'||mode==='live';return <article className={'card integration-card'+(on?' on':'')+(mode==='live'?' live':'')} key={p.key}>
   <header><Sq icon={p.icon} tone={mode==='live'?'green':on?'amber':undefined}/><div className="w"><b>{p.name}</b><small>{p.vendor}</small></div></header>
   <div className="seg sm" role="radiogroup" aria-label={'Modo de '+p.name}>{([['off','Apagada'],['simulated','Simulación'],['live','Real']] as const).map(([value,label])=><Button key={value} type="button" role="radio" aria-checked={mode===value} className={mode===value?'active':''} icon={false} disabled={value==='live'&&!ready} title={value==='live'&&!ready?'Faltan las credenciales en el servidor':undefined} onClick={()=>mode!==value&&setMode(p.key,value)}>{label}</Button>)}</div>
   <p>{p.what}</p>
   <div className="integration-state">{mode==='live'?<Pill tone="green" icon="plug">Conexión real</Pill>:sim?<Pill tone="amber" icon="spark">Simulación activa</Pill>:<Pill>Apagada</Pill>}{server==='lista'?<Pill tone="green" icon="check">Credenciales listas</Pill>:server.startsWith('error')?<Pill tone="red" icon="alert">{server}</Pill>:server==='sin conexión'?<Pill tone="amber" icon="alert">Sin conexión con el proveedor</Pill>:<Pill tone="neutral" icon="lock">Sin credenciales</Pill>}</div>
   {on&&<small className="integration-usage">{usage(s,p.key)}</small>}
   {on&&p.key==='maps'&&lastGeo&&<small className="integration-usage">{lastGeo.summary}</small>}
   <details className="integration-real"><summary>Para conectarlo de verdad</summary><span>Necesita {p.real} Las credenciales van en <code>{fileOf(p.key)}</code>; hay un ejemplo en <code>deploy/integrations</code>.</span></details>
   {on&&<div className="row-actions">{['pac','terminal','onlinepay','messaging','aggregators','pms','maps'].includes(p.key)&&<Button className="ghost xs" icon="settings" onClick={()=>configure(s,form,p.key)}>Configurar</Button>}{sim&&tests[p.key]}</div>}
  </article>;})}</section>
  <div className="g g2 start">
   <section className="card panel"><PanelHeading title="Bandeja de salida" note="Mensajes simulados · no salieron de la sucursal" icon="message"/>
    {messages.length?<div className="rows">{messages.slice(-12).reverse().map(m=><div className="r outbox" key={m.id}><Sq icon={m.channel==='whatsapp'?'message':'file'} tone={m.channel==='whatsapp'?'green':'blue'}/><div className="w"><b>{m.to}</b><span>{m.body}</span></div><span className="outbox-meta">{channelPill(m.channel)}<time>{clock(m.at)}</time></span></div>)}</div>
    :<Empty icon="message" title="Sin mensajes" text={simOn(s,'messaging')?'Aparecerán al despachar pedidos, enviar ligas o timbrar facturas.':'Activa WhatsApp y correo para ver los avisos que se enviarían.'}/>}
   </section>
   <section className="card panel"><PanelHeading title="Bitácora de integraciones" note="Cada operación con su referencia; las simuladas van marcadas" icon="history"/>
    <DataTable columns={['Hora','Integración','Operación','Resultado','Referencia']} rows={logs.slice(-25).reverse().map(l=>[stamp(l.at),providerName(l.provider),<span className="w"><span style={{whiteSpace:'normal'}}>{l.summary}</span>{l.simulated!==false&&<small>Simulado</small>}</span>,<Pill tone={logTone[l.status]||'neutral'}>{statusText[l.status]||l.status}</Pill>,l.reference?<code>{String(l.reference).slice(0,18)}</code>:<span className="no">—</span>])} empty="Las operaciones simuladas aparecerán aquí."/>
   </section>
  </div>
 </>;
}
const statusText:Record<string,string>={approved:'Aprobado',declined:'Rechazado',uncertain:'Sin respuesta',reversed:'Revertido',stamped:'Timbrado',rejected:'Rechazado',cancelled:'Cancelado',posted:'Cargado',paid:'Pagado',failed:'Fallido',expired:'Vencido',refunded:'Reembolsado',pending:'Pendiente',accepted:'Aceptado','handed-off':'Entregado',ok:'Correcto',ringing:'Entrante',simulated:'Activa',off:'Apagada'};

// ---------- simulated PIN pad ----------
const CARDS=[{value:'approve',label:'Aprobada',card:'Visa •••• 4242',tone:'green'},{value:'decline',label:'Rechazada',card:'Mastercard •••• 0002',tone:'red'},{value:'timeout',label:'Sin respuesta',card:'Visa •••• 0119',tone:'amber'}];
export function TerminalDialog({visit,state:s,action,ctx}:{visit:Row;state:any;action:Act;ctx:Ctx}){
 const folios=list(s,'folio').filter(f=>f.visitId===visit.id&&!f.void);
 const [amount,setAmount]=useState(String((visit.balance||0)/100)),[tip,setTip]=useState(''),[card,setCard]=useState('approve'),[folioId,setFolioId]=useState(folios[0]?.id||'');
 const [txn,setTxn]=useState<any>(null),[working,setWorking]=useState(false),[failure,setFailure]=useState('');
 const go=async(fn:()=>Promise<any>)=>{setWorking(true);setFailure('');try{setTxn(await fn());}catch(e:any){setFailure(e.message);}finally{setWorking(false);}};
 const charge=()=>go(()=>action('terminal.charge',{visitId:visit.id,amount:cents(amount),tip:tip.trim()?cents(tip):0,card,...(folioId?{folioId}:{})}));
 const terminalId=settingsOf(s,'terminal').terminalId||'SIM-01';
 const screen=txn?.status==='approved'?['ok','APROBADA','Aut. '+txn.authCode]:txn?.status==='declined'?['bad','RECHAZADA','Fondos insuficientes']:txn?.status==='uncertain'?['wait','SIN RESPUESTA','Consulta antes de reintentar']:txn?.status==='reversed'?['wait','REVERTIDA','La cuenta ya estaba cubierta']:['idle',working?'PROCESANDO…':'INSERTE O ACERQUE',money((()=>{try{return cents(amount)+(tip.trim()?cents(tip):0);}catch{return 0;}})())];
 return <DialogFrame label="Cobrar con terminal" close={ctx.close} busy={working} className="terminal-dialog">
  <div className="dialog-head"><div><h2>Cobrar con terminal</h2><p>Terminal simulada {terminalId} · ninguna tarjeta real se cobra</p></div><Button className="ghost dialog-close" icon="close" onClick={ctx.close} disabled={working} aria-label="Cerrar diálogo">{''}</Button></div>
  {failure&&<div className="alert error" role="alert">{failure}</div>}
  <div className={'pinpad '+screen[0]} aria-live="polite"><div className="pinpad-screen"><small>{terminalId}</small><b>{screen[1]}</b><span>{screen[2]}</span></div><div className="pinpad-keys" aria-hidden="true">{Array.from({length:12},(_,i)=><i key={i}/>)}</div></div>
  {!txn||txn.status==='declined'?<>
   <div className="terminal-fields">
    {folios.length>0&&<label>Subfolio<Select value={folioId} onChange={e=>setFolioId(e.target.value)}>{folios.map(f=><option key={f.id} value={f.id}>Parte {f.number} · {money(f.total)}</option>)}</Select></label>}
    <label>Importe MXN<input inputMode="decimal" value={amount} onChange={e=>setAmount(e.target.value)}/></label>
    <label>Propina MXN<input inputMode="decimal" value={tip} placeholder="0" onChange={e=>setTip(e.target.value)}/></label>
   </div>
   <div className="test-cards" role="radiogroup" aria-label="Tarjeta de prueba">{CARDS.map(c=><button type="button" key={c.value} role="radio" aria-checked={card===c.value} className={'test-card'+(card===c.value?' on':'')} onClick={()=>setCard(c.value)}><Pill tone={c.tone}>{c.label}</Pill><small>{c.card}</small></button>)}</div>
   <div className="dialog-actions"><Button type="button" className="ghost" icon={false} onClick={ctx.close} disabled={working}>Cancelar</Button><Button className="primary" icon="card" disabled={working} onClick={()=>void charge()}>{working?'Enviando…':txn?'Intentar con otra tarjeta':'Enviar a la terminal'}</Button></div>
  </>:<div className="dialog-actions">{txn.status==='uncertain'&&<Button className="ghost" icon="refresh" disabled={working} onClick={()=>void go(()=>action('terminal.reconcile',{id:txn.id}))}>Consultar terminal</Button>}<Button className="primary" icon={false} disabled={working} onClick={ctx.close}>Listo</Button></div>}
 </DialogFrame>;
}

// ---------- account payment extras ----------
export function AccountPayments({state:s,visit:v,action,run,form,dialog,canPay}:{state:any;visit:Row;action:Act;run:any;form:any;dialog:Dialog;canPay:boolean}){
 const manager=s.user?.role==='manager',links=byTime(list(s,'paylink').filter(l=>l.visitId===v.id&&['pending','failed','expired','refunded'].includes(l.status))).slice(-2),silent=list(s,'terminaltxn').filter(t=>t.visitId===v.id&&t.status==='uncertain');
 const room=()=>form('Cargo a habitación',[{key:'room',label:'Habitación',required:true,placeholder:'204'},{key:'guest',label:'Apellido del huésped',required:true,hint:'El PMS lo valida contra la habitación.'},{key:'amount',label:'Importe MXN',type:'money',min:0.01,value:v.balance/100,required:true}],'pms.charge',{visitId:v.id},(settingsOf(s,'pms').system==='cloudbeds'?'Cloudbeds':'Oracle Opera')+' simulado · el cargo aparece en el folio del huésped.',(x:any)=>({room:x.room,guest:x.guest,amount:cents(x.amount)}));
 const open=v.status==='open',hasBalance=v.balance>0;
 return <>
  {silent.map(t=><div className="alert warn" role="status" key={t.id}><UiIcon name="alert"/><span><b>Cobro con terminal sin respuesta · {money(t.amount+t.tip)}.</b> Consulta el resultado antes de volver a cobrar.</span><Button className="ghost sm" icon="refresh" onClick={()=>run(()=>action('terminal.reconcile',{id:t.id}),'Terminal consultada')}>Consultar terminal</Button></div>)}
  {links.map(l=><div className={'paylink'+(l.status==='pending'?'':' done')} key={l.id}><Sq icon="cloud" tone={l.status==='pending'?'blue':'neutral'}/><div className="w"><b>Liga de pago · {money(l.amount)} · {l.provider==='mercadopago'?'Mercado Pago':'Stripe'}</b><code>{l.payUrl}</code><small>{l.status==='pending'?'Vence a las '+clock(l.expiresAt)+(l.messageId?' · enviada por WhatsApp':''):l.message}</small></div>
   {l.status==='pending'&&<span className="row-actions">{manager&&<><Button className="primary xs" icon="check" onClick={()=>run(()=>action('paylink.simulate',{id:l.id,outcome:'paid'}),'Pago en línea recibido (simulado)')}>Simular pago</Button><Button className="ghost xs" icon="close" onClick={()=>run(()=>action('paylink.simulate',{id:l.id,outcome:'failed'}),'El proveedor rechazó el pago (simulado)')}>Simular rechazo</Button></>}<Button className="ghost xs" icon="close" onClick={()=>run(()=>action('paylink.cancel',{id:l.id}),'Liga cancelada')}>Cancelar liga</Button></span>}</div>)}
  {open&&canPay&&hasBalance&&(simOn(s,'terminal')||simOn(s,'pms')||simOn(s,'onlinepay'))&&<div className="pay-integrations">
   {simOn(s,'terminal')&&<Button className="ghost" icon="card" disabled={!!silent.length} onClick={()=>dialog(ctx=><TerminalDialog visit={v} state={s} action={action} ctx={ctx}/>)}>Cobrar con terminal</Button>}
   {simOn(s,'pms')&&<Button className="ghost" icon="key" onClick={room}>Cargo a habitación</Button>}
   {simOn(s,'onlinepay')&&<Button className="ghost" icon="cloud" disabled={links.some(l=>l.status==='pending')} onClick={()=>run(()=>action('paylink.create',{visitId:v.id}),v.customerId&&simOn(s,'messaging')?'Liga de pago enviada por WhatsApp (simulado)':'Liga de pago creada')}>Liga de pago</Button>}
  </div>}
 </>;
}
export function paymentDetail(p:Row){return p.terminal?p.terminal.brand+' •••• '+p.terminal.last4+' · aut. '+p.terminal.authCode:p.method==='room'?'Habitación '+p.room+' · '+p.reference:p.method==='online'?(p.provider==='mercadopago'?'Mercado Pago':'Stripe')+' · '+p.reference:p.method==='platform'?(PLATFORMS[p.platform]?.name||'Plataforma')+' · '+p.reference:'';}

// ---------- delivery page: platform orders and incoming calls ----------
export function platformCards(s:any,{action,run,form,openVisit,manager}:{action:Act;run:any;form:any;openVisit:(id:string)=>void;manager:boolean}){
 const orders=byTime(list(s,'aggregatororder'),'receivedAt').reverse(),units=list(s,'unit');
 const ready=(o:Row)=>!!o.visitId&&units.filter(u=>u.visitId===o.visitId).every(u=>['ready','delivered','cancelled','unavailable'].includes(u.status));
 const card=(o:Row,actions:React.ReactNode,extra?:React.ReactNode)=>{const p=PLATFORMS[o.platform]||{name:o.platformName,tone:'neutral'};const late=o.status==='pending'?Math.ceil((Date.parse(o.acceptBy)-Date.now())/60000):null;return <div className={'kc platform'+(late!=null&&late<=2?' due':'')} key={o.id}><b>{o.customerName} · <Pill tone={p.tone}>{p.name}</Pill></b><small>{o.externalId} · {(o.items||[]).map((i:Row)=>i.quantity+' × '+i.name).join(', ')}{o.notes?' · '+o.notes:''}</small>{late!=null&&<span className={'ui-status tone-'+(late<=2?'red':'amber')}><UiIcon name="clock"/>{late>0?'Acepta en '+late+' min':'Tiempo de aceptación vencido'}</span>}{extra}<div className="row"><span className="num">{money(o.total??o.subtotal)}</span><span className="actions">{o.visitId&&<Button className="ghost xs" icon="receipt" onClick={()=>openVisit(o.visitId)}>Cuenta</Button>}{actions}</span></div></div>;};
 return {
  pending:orders.filter(o=>o.status==='pending').map(o=>card(o,manager&&<><Button className="ghost xs" icon="close" onClick={()=>form('Rechazar pedido de '+(PLATFORMS[o.platform]?.name||'la plataforma'),[{key:'reason',label:'Motivo para la plataforma',required:true,full:true,placeholder:'Ej. Cocina saturada'}],'aggregator.decide',{id:o.id,accept:false})}>Rechazar</Button><Button className="primary xs" icon="check" onClick={()=>run(()=>action('aggregator.decide',{id:o.id,accept:true}),'Pedido enviado a cocina')}>Aceptar</Button></>)),
  accepted:orders.filter(o=>o.status==='accepted').map(o=>card(o,<Button className={(ready(o)?'primary':'ghost')+' xs'} icon="truck" disabled={!ready(o)} title={ready(o)?undefined:'Espera a que cocina lo termine'} onClick={()=>run(()=>action('aggregator.handoff',{id:o.id}),'Entregado al repartidor de la plataforma')}>Entregar al repartidor</Button>,<Pill tone="purple" icon="wallet">Neto {money(o.net)}</Pill>)),
  closed:orders.filter(o=>['handed-off','rejected'].includes(o.status)).map(o=>card(o,null,<Pill tone={o.status==='rejected'?'red':'green'} icon={o.status==='rejected'?'close':'check'}>{o.status==='rejected'?'Rechazado · '+o.reason:'Entregado a '+o.courier}</Pill>)),
 };
}
export function CallBanner({state:s,action,run,form}:{state:any;action:Act;run:any;form:any}){
 const call=byTime(list(s,'call').filter(c=>!c.attended&&Date.now()-Date.parse(c.at)<15*60000)).at(-1);if(!call)return null;
 const customer=list(s,'customer').find(c=>c.id===call.customerId),orders=customer?list(s,'visit').filter(v=>v.customerId===customer.id).length:0;
 const pretty=String(call.number).replace(/^(\d{2})(\d{4})(\d{4})$/,'$1 $2 $3');
 return <div className="call-banner" role="status"><span className="call-ring"><UiIcon name="phone"/></span><div className="w"><small>Llamada entrante · {clock(call.at)}</small><b>{customer?customer.name:call.name||'Número nuevo'} · {pretty}</b><span>{customer?plural(orders,'pedido anterior','pedidos anteriores')+(customer.notes?' · '+customer.notes:''):'Sin ficha de cliente'}</span></div>
  <span className="row-actions">{customer?<Button className="primary sm" icon="plus" onClick={()=>form('Pedido para '+customer.name,[{key:'mode',label:'Servicio',type:'select',value:'delivery',options:[{value:'delivery',label:'Entrega a domicilio'},{value:'pickup',label:'Recoger en sucursal'}]},{key:'addressId',label:'Dirección de entrega',type:'select',options:list(s,'address').filter(a=>a.customerId===customer.id).map(a=>({value:a.id,label:a.label+' · '+a.text})),showIf:(x:any)=>x.mode==='delivery'}],'delivery.create',{customerId:customer.id},undefined,(x:any)=>({mode:x.mode,...(x.mode==='delivery'?{addressId:x.addressId}:{})}))}>Tomar pedido</Button>
   :<Button className="primary sm" icon="user" onClick={()=>form('Cliente nuevo',[{key:'name',label:'Nombre',required:true,value:call.name||''},{key:'phone',label:'Teléfono',required:true,value:call.number},{key:'notes',label:'Notas',type:'textarea',full:true}],'customer.save')}>Registrar cliente</Button>}
   <Button className="ghost sm" icon="check" onClick={()=>run(()=>action('call.attend',{id:call.id}),'Llamada atendida')}>Atendida</Button></span></div>;
}
