import React,{useEffect,useRef,useState} from 'react';
import {Button,PanelHeading,Kpis,MetricCard,Pill,Sq,Empty,Notice,DataTable,PageTools,Bar,UiIcon} from './ui';
import {DialogFrame} from './motion';
import {cents} from './bridge';

type Row={id:string;[key:string]:any};
type Ctx={close:()=>void;submit:(fn:()=>Promise<any>)=>void;busy:boolean;error:string};
export type Dialog=(render:(ctx:Ctx)=>React.ReactNode)=>void;
type Props={state:any;form:any;action:(type:string,payload:any)=>Promise<any>;run:any;dialog:Dialog};

export const money=(n:number)=>new Intl.NumberFormat('es-MX',{style:'currency',currency:'MXN'}).format((n||0)/100);
const day=(s:string)=>s?new Date(s).toLocaleDateString('es-MX',{day:'numeric',month:'short'}):'—';
const date=(s:string)=>s?new Date(s).toLocaleString('es-MX',{dateStyle:'medium',timeStyle:'short'}):'—';
const num=(v:number,d=3)=>v.toLocaleString('es-MX',{minimumFractionDigits:0,maximumFractionDigits:d});
const plural=(n:number,one:string,many:string)=>n+' '+(n===1?one:many);
const daysUntil=(s:string)=>Math.ceil((Date.parse(s)-Date.now())/86400000);
const monthStart=()=>{const d=new Date();return new Date(d.getFullYear(),d.getMonth(),1).getTime();};
// Stock quantities are thousandths of the ingredient's base unit (g, ml or piece).
export function qty(raw:number,unit='g'){const v=(raw||0)/1000;if(unit==='g'&&Math.abs(v)>=1000)return num(v/1000)+' kg';if(unit==='ml'&&Math.abs(v)>=1000)return num(v/1000)+' l';return num(v)+' '+(unit==='unit'?'u':unit);}
const units=(family:string):[string,string][]=>family==='g'?[['kg','kg'],['g','g']]:family==='ml'?[['l','l'],['ml','ml']]:[['unit','pzas'],['dozen','doc.']];
const inUnit=(raw:number,unit:string)=>String(unit==='kg'||unit==='l'?raw/1e6:unit==='dozen'?raw/12000:raw/1000);
const fromUnit=(text:string,unit:string)=>{const v=Number(String(text).replace(',','.'));return Number.isFinite(v)?Math.round(v*(unit==='kg'||unit==='l'?1e6:unit==='dozen'?12000:1000)):NaN;};
const decimal=(text:string)=>{const t=String(text??'').trim().replace(',','.');if(!/^\d+(\.\d+)?$/.test(t))throw new Error('Usa cantidades positivas con punto decimal');return t;};
const pesos=(text:string)=>String(text??'').trim()===''?0:cents(text);
const list=(s:any,kind:string):Row[]=>s[kind+'s']||[];
const nameOf=(s:any,kind:string,id:string)=>list(s,kind).find(x=>x.id===id)?.name||'—';
const folio=(n:number)=>'OC-'+String(n||0).padStart(4,'0');
const SOURCES=[{value:'bank',label:'Banco o transferencia'},{value:'register',label:'Efectivo de la caja'},{value:'petty',label:'Caja chica'}];
const METHODS=[{value:'transfer',label:'Transferencia'},{value:'cash',label:'Efectivo'},{value:'check',label:'Cheque'},{value:'card',label:'Tarjeta'}];

function Head({title,note,close,busy}:{title:string;note?:string;close:()=>void;busy:boolean}){
 return <div className="dialog-head"><div><h2>{title}</h2>{note&&<p>{note}</p>}</div><Button className="ghost dialog-close" icon="close" onClick={close} disabled={busy} aria-label="Cerrar diálogo">{''}</Button></div>;
}

type Line={key:string;ingredientId:string;quantity:string;unit:string;cost:string};
function PurchaseDialog({state:s,mode,order,ctx,action}:{state:any;mode:'order'|'receive'|'direct';order?:Row;ctx:Ctx;action:Props['action']}){
 const ingredients=list(s,'ingredient'),suppliers=list(s,'supplier').filter(x=>x.active||x.id===order?.supplierId),warehouses=list(s,'warehouse');
 const family=(id:string)=>ingredients.find(i=>i.id===id)?.unit||'g';
 const blank=():Line=>{const i=ingredients[0];const unit=units(i?.unit||'g')[0][0];return {key:crypto.randomUUID(),ingredientId:i?.id||'',quantity:'',unit,cost:''};};
 const [lines,setLines]=useState<Line[]>(()=>order?order.lines.map((l:Row)=>{const unit=units(family(l.ingredientId))[0][0];return {key:crypto.randomUUID(),ingredientId:l.ingredientId,quantity:inUnit(l.quantity,unit),unit,cost:String((l.cost||0)/100)};}):[blank()]);
 const [supplierId,setSupplier]=useState(order?.supplierId||suppliers[0]?.id||''),[warehouseId,setWarehouse]=useState(order?.warehouseId||s.restaurant?.saleWarehouseId||warehouses[0]?.id||'');
 const supplier=suppliers.find(x=>x.id===supplierId);
 const [invoice,setInvoice]=useState(''),[dueDays,setDueDays]=useState(String(supplier?.creditDays??0)),[payNow,setPayNow]=useState(false),[source,setSource]=useState('register'),[notes,setNotes]=useState('');
 const patch=(key:string,change:Partial<Line>)=>setLines(ls=>ls.map(l=>l.key===key?{...l,...change}:l));
 const total=lines.reduce((t,l)=>{try{return t+pesos(l.cost);}catch{return t;}},0);
 const receiving=mode!=='order',title=mode==='order'?'Orden de compra':mode==='receive'?'Recibir '+folio(order!.number):'Compra directa';
 const ordered=(id:string)=>order?.lines.find((l:Row)=>l.ingredientId===id);
 const send=()=>ctx.submit(async()=>{
  const payloadLines=lines.filter(l=>l.ingredientId).map(l=>({ingredientId:l.ingredientId,quantity:receiving&&mode==='receive'&&String(l.quantity).trim()===''?'0':decimal(l.quantity),unit:l.unit,cost:pesos(l.cost)}));
  if(!payloadLines.length)throw new Error('Agrega al menos un insumo');
  const extra=receiving?{invoice:invoice.trim(),dueDays:Number(dueDays)||0,payNow,source,method:source==='bank'?'transfer':'cash'}:{notes};
  if(receiving&&!invoice.trim())throw new Error('Escribe el folio de la factura o remisión');
  if(mode==='order')return action('purchase.order',{supplierId,warehouseId,lines:payloadLines,notes});
  return action('purchase.receive',{...(order?{id:order.id}:{supplierId,warehouseId}),lines:payloadLines,...extra});
 });
 return <DialogFrame label={title} close={ctx.close} busy={ctx.busy} className="po-dialog">
  <Head title={title} note={mode==='receive'?'Captura lo que realmente llegó; las diferencias contra la orden quedan registradas.':mode==='direct'?'Entrada al almacén con su factura; genera la cuenta por pagar.':'El inventario no cambia hasta recibir la mercancía.'} close={ctx.close} busy={ctx.busy}/>
  {ctx.error&&<div className="alert error" role="alert">{ctx.error}</div>}
  {!suppliers.length?<Notice tone="warn">Registra un proveedor antes de comprar.</Notice>:<>
  <div className="po-top">
   <label>Proveedor<select value={supplierId} disabled={!!order} onChange={e=>{setSupplier(e.target.value);setDueDays(String(suppliers.find(x=>x.id===e.target.value)?.creditDays??0));}}>{suppliers.map(x=><option key={x.id} value={x.id}>{x.name}</option>)}</select></label>
   <label>Almacén de entrega<select value={warehouseId} disabled={!!order} onChange={e=>setWarehouse(e.target.value)}>{warehouses.map(x=><option key={x.id} value={x.id}>{x.name}</option>)}</select></label>
  </div>
  <div className="po-lines" role="table" aria-label="Insumos de la compra">
   <div className="po-line head" role="row"><span>Insumo</span><span>Cantidad</span><span>Unidad</span><span>Costo total</span><span/></div>
   {lines.map(l=>{const o=ordered(l.ingredientId);const diff=o&&mode==='receive'&&fromUnit(l.quantity,l.unit)!==o.quantity;return <div className={'po-line'+(diff?' diff':'')} role="row" key={l.key}>
    <select aria-label="Insumo" value={l.ingredientId} onChange={e=>patch(l.key,{ingredientId:e.target.value,unit:units(family(e.target.value))[0][0]})}>{ingredients.map(i=><option key={i.id} value={i.id}>{i.name}</option>)}</select>
    <input aria-label={'Cantidad de '+nameOf(s,'ingredient',l.ingredientId)} type="number" inputMode="decimal" min={0} step="any" value={l.quantity} placeholder="0" onFocus={e=>e.target.select()} onChange={e=>patch(l.key,{quantity:e.target.value})}/>
    <select aria-label="Unidad" value={l.unit} onChange={e=>patch(l.key,{unit:e.target.value})}>{units(family(l.ingredientId)).map(([v,t])=><option key={v} value={v}>{t}</option>)}</select>
    <span className="affix"><span aria-hidden="true">$</span><input aria-label={'Costo de '+nameOf(s,'ingredient',l.ingredientId)} type="number" inputMode="decimal" min={0} step="0.01" value={l.cost} placeholder="0.00" onFocus={e=>e.target.select()} onChange={e=>patch(l.key,{cost:e.target.value})}/></span>
    <Button type="button" className="ghost icon-only sm" icon="close" aria-label="Quitar renglón" disabled={lines.length===1} onClick={()=>setLines(ls=>ls.filter(x=>x.key!==l.key))}>{''}</Button>
    {diff&&<small className="po-diff">Pedido: {qty(o.quantity,family(l.ingredientId))}</small>}
   </div>;})}
  </div>
  <div className="po-foot"><Button type="button" className="text-button" icon="plus" disabled={lines.length>=30||!ingredients.length} onClick={()=>setLines(ls=>[...ls,blank()])}>Agregar insumo</Button><span>Total <b className="num">{money(total)}</b></span></div>
  {receiving?<div className="po-top">
   <label>Folio de factura o remisión<input value={invoice} maxLength={60} onChange={e=>setInvoice(e.target.value)} placeholder="Ej. F-1024"/></label>
   <label>Días de crédito<input type="number" min={0} max={180} value={dueDays} onFocus={e=>e.target.select()} onChange={e=>setDueDays(e.target.value)}/><small>Vence el {day(new Date(Date.now()+(Number(dueDays)||0)*86400000).toISOString())}</small></label>
   <label className="check-row"><input type="checkbox" checked={payNow} onChange={e=>setPayNow(e.target.checked)}/>Pagar ahora</label>
   {payNow&&<label>Pagar desde<select value={source} onChange={e=>setSource(e.target.value)}>{SOURCES.map(o=><option key={o.value} value={o.value}>{o.label}</option>)}</select></label>}
  </div>:<label>Notas para el proveedor<textarea value={notes} maxLength={500} onChange={e=>setNotes(e.target.value)} placeholder="Horario de entrega, presentación…"/></label>}
  </>}
  <div className="dialog-actions"><Button type="button" className="ghost" icon={false} onClick={ctx.close} disabled={ctx.busy}>Volver</Button><Button className="primary" icon={receiving?'check':'save'} disabled={ctx.busy||!suppliers.length||!warehouses.length} onClick={send}>{receiving?'Registrar entrada':'Crear orden'}</Button></div>
 </DialogFrame>;
}

function SuggestDialog({state:s,result,ctx,action}:{state:any;result:any;ctx:Ctx;action:Props['action']}){
 const lines:Row[]=result.lines||[];const groups=[...new Set(lines.map(l=>l.supplierId||''))];const creatable=groups.filter(g=>g&&list(s,'supplier').find(x=>x.id===g)?.active).length;
 return <DialogFrame label="Sugerencia de compra" close={ctx.close} busy={ctx.busy} className="po-dialog">
  <Head title="Sugerencia de compra" note={'Insumos en o bajo su mínimo en '+nameOf(s,'warehouse',result.warehouseId)+'. Se pide lo necesario para llegar al máximo, descontando lo que ya viene en camino.'} close={ctx.close} busy={ctx.busy}/>
  {ctx.error&&<div className="alert error" role="alert">{ctx.error}</div>}
  {!lines.length?<Empty icon="check" title="Todo está sobre el mínimo" text="Define mínimos y máximos en Inventario para recibir sugerencias."/>:groups.map(g=><section className="suggest-group" key={g||'none'}>
   <h4>{g?nameOf(s,'supplier',g):'Sin proveedor preferido'}{!g&&<small> · asígnalo en Inventario › Mínimos y máximos</small>}</h4>
   <DataTable columns={['Insumo','>Disponible','>En camino','>Mín · Máx','>Sugerido','>Estimado']} rows={lines.filter(l=>(l.supplierId||'')===g).map(l=>[<b>{l.name}</b>,<span className="neg">{qty(l.available,l.unit)}</span>,l.incoming?qty(l.incoming,l.unit):'—',qty(l.min,l.unit)+' · '+qty(l.max,l.unit),<b>{qty(l.suggest,l.unit)}</b>,l.estimated?money(l.estimated):'—'])}/>
  </section>)}
  <div className="dialog-actions"><Button type="button" className="ghost" icon={false} onClick={ctx.close} disabled={ctx.busy}>Volver</Button>{creatable>0&&<Button className="primary" icon="plus" disabled={ctx.busy} onClick={()=>ctx.submit(()=>action('purchase.suggest',{warehouseId:result.warehouseId,create:true}))}>{creatable===1?'Crear orden':'Crear '+creatable+' órdenes'}</Button>}</div>
 </DialogFrame>;
}

export function CountDialog({state:s,ctx,action}:{state:any;ctx:Ctx;action:Props['action']}){
 const warehouses=list(s,'warehouse'),ingredients=list(s,'ingredient'),stocks=list(s,'stock');
 const [warehouseId,setWarehouse]=useState(s.restaurant?.saleWarehouseId||warehouses[0]?.id||''),[note,setNote]=useState('');
 const [counts,setCounts]=useState<Record<string,{value:string;unit:string}>>({});
 const rows=ingredients.map(i=>{const st=stocks.find(x=>x.warehouseId===warehouseId&&x.ingredientId===i.id);return {i,theoretical:st?.quantity||0,reserved:st?.reserved||0};});
 const entry=(id:string,family:string)=>counts[id]||{value:'',unit:units(family)[0][0]};
 const filled=rows.filter(r=>entry(r.i.id,r.i.unit).value.trim()!=='');
 const send=()=>ctx.submit(async()=>{if(!filled.length)throw new Error('Captura al menos un insumo contado');if(filled.length>25)throw new Error('Captura hasta 25 insumos por conteo');return action('stock.count',{warehouseId,note,lines:filled.map(r=>{const e=entry(r.i.id,r.i.unit);return {ingredientId:r.i.id,quantity:decimal(e.value),unit:e.unit};})});});
 return <DialogFrame label="Conteo físico" close={ctx.close} busy={ctx.busy} className="po-dialog">
  <Head title="Conteo físico" note="Captura lo que hay en el almacén. La diferencia contra el inventario teórico se ajusta a costo y queda en el historial." close={ctx.close} busy={ctx.busy}/>
  {ctx.error&&<div className="alert error" role="alert">{ctx.error}</div>}
  <div className="po-top"><label>Almacén<select value={warehouseId} onChange={e=>{setWarehouse(e.target.value);setCounts({});}}>{warehouses.map(w=><option key={w.id} value={w.id}>{w.name}</option>)}</select></label><label>Nota<input value={note} maxLength={300} onChange={e=>setNote(e.target.value)} placeholder="Ej. Cierre semanal"/></label></div>
  {!rows.length?<Empty icon="box" title="Sin insumos" text="Registra insumos en Inventario."/>:<div className="po-lines count" role="table" aria-label="Conteo por insumo">
   <div className="po-line head" role="row"><span>Insumo</span><span>Teórico</span><span>Contado</span><span>Unidad</span><span>Diferencia</span></div>
   {rows.map(({i,theoretical,reserved})=>{const e=entry(i.id,i.unit);const counted=e.value.trim()===''?NaN:fromUnit(e.value,e.unit);const diff=Number.isNaN(counted)?null:counted-theoretical;return <div className="po-line" role="row" key={i.id}>
    <span className="w"><b>{i.name}</b>{reserved>0&&<small>Reservado {qty(reserved,i.unit)}</small>}</span>
    <span className="num">{qty(theoretical,i.unit)}</span>
    <input aria-label={'Contado de '+i.name} type="number" inputMode="decimal" min={0} step="any" placeholder="—" value={e.value} onFocus={ev=>ev.target.select()} onChange={ev=>setCounts(c=>({...c,[i.id]:{...e,value:ev.target.value}}))}/>
    <select aria-label={'Unidad de '+i.name} value={e.unit} onChange={ev=>setCounts(c=>({...c,[i.id]:{...e,unit:ev.target.value}}))}>{units(i.unit).map(([v,t])=><option key={v} value={v}>{t}</option>)}</select>
    <span className={'num '+(diff===null||diff===0?'':diff<0?'neg':'pos')}>{diff===null?'—':(diff>0?'+':'')+qty(diff,i.unit)}</span>
   </div>;})}
  </div>}
  <div className="dialog-actions"><small className="count-note">{filled.length} de 25 insumos capturados</small><Button type="button" className="ghost" icon={false} onClick={ctx.close} disabled={ctx.busy}>Volver</Button><Button className="primary" icon="check" disabled={ctx.busy||!filled.length} onClick={send}>Ajustar inventario</Button></div>
 </DialogFrame>;
}

export function VariancePanel({state:s,action,run}:{state:any;action:Props['action'];run:any}){
 const [period,setPeriod]=useState('7'),[data,setData]=useState<any>(null);
 const range=()=>{const to=new Date(Date.now()+60000);const from=period==='month'?new Date(monthStart()):new Date(Date.now()-Number(period)*86400000);return {from:from.toISOString(),to:to.toISOString()};};
 const pct=(v:number)=>data?.sales?(v/data.sales*100).toLocaleString('es-MX',{maximumFractionDigits:1})+' % de ventas':'sin ventas en el periodo';
 const counts=list(s,'stockcount').slice(-4).reverse();
 return <section className="card panel"><PanelHeading title="Costo teórico vs real" note="Teórico = recetas vendidas. Real = teórico + merma + faltantes de conteo − sobrantes." icon="chart" action={<div style={{display:'flex',gap:8,alignItems:'center',flexWrap:'wrap'}}><div className="seg sm" role="group" aria-label="Periodo">{[['7','7 días'],['30','30 días'],['month','Este mes']].map(([v,t])=><Button key={v} icon={false} className={period===v?'active':''} aria-pressed={period===v} onClick={()=>{setPeriod(v);setData(null);}}>{t}</Button>)}</div><Button className="ghost sm" icon="refresh" onClick={()=>run(async()=>setData(await action('inventory.variance',range())),'Periodo calculado')}>Calcular</Button></div>}/>
  {data?<>
   <div className="variance-kpis">
    <div><small>Ventas</small><b className="num">{money(data.sales)}</b></div>
    <div><small>Costo teórico</small><b className="num">{money(data.theoretical)}</b><span>{pct(data.theoretical)}</span></div>
    <div><small>Costo real</small><b className="num">{money(data.real)}</b><span>{pct(data.real)}</span></div>
    <div className={data.real-data.theoretical>0?'bad':''}><small>Diferencia</small><b className="num">{money(data.real-data.theoretical)}</b><span>merma {money(data.waste)} · faltantes {money(data.shortage)}</span></div>
   </div>
   <DataTable columns={['Insumo','>Teórico','>Merma','>Faltante','>Sobrante','>Real','>Diferencia']} rows={(data.rows as Row[]).sort((a,b)=>(b.real-b.theoretical)-(a.real-a.theoretical)).map(r=>[<span className="w"><b>{r.name}</b><small>{qty(r.theoreticalQty,r.unit)} teórico · {qty(r.realQty,r.unit)} real</small></span>,money(r.theoretical),r.waste?money(r.waste):'—',r.shortage?<span className="neg">{money(r.shortage)}</span>:'—',r.surplus?<span className="pos">{money(r.surplus)}</span>:'—',<b>{money(r.real)}</b>,<span className={r.real-r.theoretical>0?'neg':''}>{money(r.real-r.theoretical)}</span>])} empty="Sin consumos, mermas ni conteos en el periodo."/>
  </>:<Empty icon="chart" title="Elige un periodo y calcula" text="Compara lo que las recetas dicen que se gastó contra lo que realmente salió del almacén."/>}
  {counts.length>0&&<><h4 className="sub-h">Conteos físicos recientes</h4><div className="rows">{counts.map(c=><div className="r" key={c.id}><Sq icon="check" tone={c.shortageCost>0?'red':'green'}/><div className="w"><b>{nameOf(s,'warehouse',c.warehouseId)} · {plural((c.lines||[]).length,'insumo','insumos')}</b><span>{c.note||'Sin nota'} · {date(c.at)}</span></div><span className="amt num">{c.shortageCost?<span className="neg">−{money(c.shortageCost)}</span>:null}{c.surplusCost?<span className="pos"> +{money(c.surplusCost)}</span>:null}{!c.shortageCost&&!c.surplusCost&&'Sin diferencia'}</span></div>)}</div></>}
 </section>;
}

export function levelsForm(s:any,form:any,ingredient?:Row){
 const ingredients=list(s,'ingredient');const first=ingredient||ingredients[0];const fam=first?.unit||'g';const unit=units(fam)[0][0];
 form('Mínimos y máximos',[
  {key:'ingredientId',label:'Insumo',type:'select',value:first?.id,options:ingredients.map(i=>({value:i.id,label:i.name}))},
  {key:'unit',label:'Unidad',type:'select',value:unit,options:[...units('g'),...units('ml'),...units('unit')].map(([v,t])=>({value:v,label:t}))},
  {key:'min',label:'Mínimo',type:'number',value:first?.minStock?inUnit(first.minStock,unit):'',min:0,step:'any',hint:'Al llegar aquí se sugiere comprar.'},
  {key:'max',label:'Máximo',type:'number',value:first?.maxStock?inUnit(first.maxStock,unit):'',min:0,step:'any',hint:'La sugerencia rellena hasta este nivel.'},
  {key:'supplierId',label:'Proveedor preferido',type:'select',value:first?.supplierId||'',options:[{value:'',label:'Sin proveedor'},...list(s,'supplier').filter(x=>x.active).map(x=>({value:x.id,label:x.name}))],full:true}
 ],'inventory.levels',{},'Cantidades en la unidad elegida. Deja vacío para no usar sugerencia.',(x:any)=>({...x,min:String(x.min||'0'),max:String(x.max||'0')}));
}

export function lowStock(s:any){
 return list(s,'ingredient').filter(i=>i.maxStock>0).filter(i=>{const available=list(s,'stock').filter(x=>x.ingredientId===i.id).reduce((t,x)=>t+x.quantity-(x.reserved||0),0);return available<=i.minStock;});
}

export function supplierForm(form:any,x?:Row){
 form(x?'Editar proveedor':'Proveedor',[
  {key:'name',label:'Nombre o razón social',value:x?.name,required:true,full:true},
  {key:'rfc',label:'RFC',value:x?.rfc,placeholder:'Opcional'},
  {key:'creditDays',label:'Días de crédito',type:'number',value:x?.creditDays??0,min:0,max:180,hint:'Vencimiento de sus facturas.'},
  {key:'contact',label:'Contacto',value:x?.contact,placeholder:'Nombre de quien atiende'},
  {key:'phone',label:'Teléfono',value:x?.phone},
  {key:'email',label:'Correo',value:x?.email,full:true},
  ...(x?[{key:'active',label:'Proveedor activo',type:'checkbox',value:x.active!==false}]:[])
 ],'supplier.save',{id:x?.id},undefined,(v:any)=>({...v,creditDays:Number(v.creditDays)||0,active:v.active??true}));
}

export function PurchasesPage({state:s,form,action,run,dialog}:Props){
 const [tab,setTab]=useState('orders');
 const purchases=list(s,'purchase').slice().reverse(),payables=list(s,'payable'),suppliers=list(s,'supplier');
 const open=payables.filter(p=>p.status==='open'),balance=(p:Row)=>p.amount-(p.paid||0),overdue=open.filter(p=>daysUntil(p.dueAt)<0);
 const ordered=purchases.filter(p=>p.status==='ordered'),monthReceived=purchases.filter(p=>p.status==='received'&&Date.parse(p.receivedAt)>=monthStart());
 const warehouse=s.restaurant?.saleWarehouseId||list(s,'warehouse')[0]?.id;
 const purchase=(mode:'order'|'receive'|'direct',order?:Row)=>dialog(ctx=><PurchaseDialog state={s} mode={mode} order={order} ctx={ctx} action={action}/>);
 const suggest=()=>run(async()=>{if(!warehouse)throw new Error('Crea un almacén en Inventario');const result=await action('purchase.suggest',{warehouseId:warehouse});dialog(ctx=><SuggestDialog state={s} result={result} ctx={ctx} action={action}/>);},'Sugerencia calculada');
 const pay=(p:Row)=>form('Pagar a '+nameOf(s,'supplier',p.supplierId),[
  {key:'amount',label:'Importe MXN',type:'money',value:balance(p)/100,min:0.01,required:true,hint:'Saldo pendiente '+money(balance(p))},
  {key:'source',label:'Pagar desde',type:'select',value:'bank',options:SOURCES},
  {key:'method',label:'Forma de pago',type:'select',value:'transfer',options:METHODS,showIf:(x:any)=>x.source==='bank'},
  {key:'reference',label:'Referencia',placeholder:'SPEI, cheque…',full:true}
 ],'payable.pay',{id:p.id},'Factura '+p.invoice+' · vence '+day(p.dueAt),(x:any)=>({...x,amount:cents(x.amount),method:x.source==='bank'?x.method:'cash'}));
 const status=(p:Row)=>p.status==='ordered'?<Pill tone="blue" icon="clock">Por recibir</Pill>:p.status==='received'?<Pill tone="green" icon="check">Recibida{p.differences?' · '+plural(p.differences,'diferencia','diferencias'):''}</Pill>:<Pill>Cancelada</Pill>;
 const aging=[['Por vencer',open.filter(p=>daysUntil(p.dueAt)>=0)],['1 a 30 días',overdue.filter(p=>daysUntil(p.dueAt)>=-30)],['31 a 60 días',overdue.filter(p=>daysUntil(p.dueAt)<-30&&daysUntil(p.dueAt)>=-60)],['Más de 60 días',overdue.filter(p=>daysUntil(p.dueAt)<-60)]] as [string,Row[]][];
 const totalOpen=open.reduce((t,p)=>t+balance(p),0);
 return <>
  <PageTools><Button className="ghost" icon="plus" onClick={()=>supplierForm(form)}>Proveedor</Button><Button className="ghost" icon="spark" onClick={suggest}>Sugerir compra</Button><Button className="ghost" icon="box" onClick={()=>purchase('direct')}>Compra directa</Button><Button className="primary" icon="plus" onClick={()=>purchase('order')}>Orden de compra</Button></PageTools>
  {overdue.length>0&&<Notice tone="warn"><b>{plural(overdue.length,'factura vencida','facturas vencidas')}</b> por {money(overdue.reduce((t,p)=>t+balance(p),0))}. La más antigua: {nameOf(s,'supplier',overdue.sort((a,b)=>Date.parse(a.dueAt)-Date.parse(b.dueAt))[0].supplierId)}.</Notice>}
  <Kpis>
   <MetricCard className="lead" label="Cuentas por pagar" value={money(totalOpen)} note={plural(open.length,'factura abierta','facturas abiertas')} icon="wallet" tone="amber"/>
   <MetricCard label="Vencido" value={money(overdue.reduce((t,p)=>t+balance(p),0))} note={overdue.length?plural(overdue.length,'factura','facturas'):'Al corriente'} icon="alert" tone={overdue.length?'red':'green'}/>
   <MetricCard label="Órdenes por recibir" value={ordered.length} note={money(ordered.reduce((t,p)=>t+p.total,0))+' estimado'} icon="clock" tone="blue"/>
   <MetricCard label="Compras del mes" value={money(monthReceived.reduce((t,p)=>t+(p.receivedTotal||0),0))} note={plural(monthReceived.length,'entrada','entradas')} icon="box" tone="purple"/>
  </Kpis>
  <div className="seg tabs-seg" role="tablist" aria-label="Sección de compras">{[['orders','Órdenes',ordered.length],['payables','Cuentas por pagar',open.length],['suppliers','Proveedores',suppliers.length]].map(([k,t,n])=><Button key={k as string} role="tab" icon={false} aria-selected={tab===k} className={tab===k?'active':''} onClick={()=>setTab(k as string)}>{t} <span className="c">{n}</span></Button>)}</div>
  {tab==='orders'&&<section className="card panel"><PanelHeading title="Órdenes y entradas" note="Recibir genera la entrada al almacén y la cuenta por pagar" icon="receipt"/>
   <DataTable columns={['Folio','Proveedor','Almacén','Insumos','>Total','Estado','']} rows={purchases.map(p=>[<span className="w"><b>{folio(p.number)}</b><small>{date(p.receivedAt||p.at)}</small></span>,nameOf(s,'supplier',p.supplierId),nameOf(s,'warehouse',p.warehouseId),(p.received||p.lines).map((l:Row)=>nameOf(s,'ingredient',l.ingredientId)).join(', '),<b>{money(p.receivedTotal??p.total)}</b>,status(p),p.status==='ordered'?<span className="row-actions"><Button className="primary xs" icon="check" onClick={()=>purchase('receive',p)}>Recibir</Button><Button className="ghost xs" icon="close" onClick={()=>form('Cancelar '+folio(p.number),[{key:'reason',label:'Motivo',required:true,full:true}],'purchase.cancel',{id:p.id})}>Cancelar</Button></span>:p.invoice?<small>Factura {p.invoice}</small>:p.cancelReason?<small>{p.cancelReason}</small>:null])} empty="Crea una orden de compra o registra una compra directa."/>
  </section>}
  {tab==='payables'&&<div className="g g-side start"><section className="card panel"><PanelHeading title="Cuentas por pagar" note="Ordenadas por vencimiento" icon="wallet"/>
   <DataTable columns={['Proveedor','Factura','Vence','>Importe','>Saldo','']} rows={payables.slice().sort((a,b)=>(a.status==='open'?0:1)-(b.status==='open'?0:1)||Date.parse(a.dueAt)-Date.parse(b.dueAt)).map(p=>{const d=daysUntil(p.dueAt);return [<b>{nameOf(s,'supplier',p.supplierId)}</b>,<code>{p.invoice}</code>,p.status==='paid'?<Pill tone="green" icon="check">Pagada</Pill>:<span className="w"><span>{day(p.dueAt)}</span><small className={d<0?'neg':d<=3?'warn-t':''}>{d<0?'Vencida hace '+plural(-d,'día','días'):d===0?'Vence hoy':'En '+plural(d,'día','días')}</small></span>,money(p.amount),<b className={p.status==='open'&&d<0?'neg':''}>{money(balance(p))}</b>,p.status==='open'&&<Button className="primary xs" icon="cash" onClick={()=>pay(p)}>Pagar</Button>];})} empty="Las compras recibidas a crédito aparecerán aquí."/>
  </section><section className="card panel"><PanelHeading title="Antigüedad de saldos" note="Saldo abierto por días de atraso" icon="clock"/>
   <div className="rows">{aging.map(([t,rows],i)=>{const v=rows.reduce((n,p)=>n+balance(p),0);return <div className="r aging" key={t}><div className="w"><b>{t}</b><span>{plural(rows.length,'factura','facturas')}</span><Bar value={v} max={totalOpen||1} tone={i===0?'g':i===1?'':'r'}/></div><span className="amt num">{money(v)}</span></div>;})}</div>
  </section></div>}
  {tab==='suppliers'&&<section className="card panel"><PanelHeading title="Proveedores" note="Crédito, saldo y compras" icon="truck"/>
   <DataTable columns={['Proveedor','RFC','Contacto','>Crédito','>Saldo','>Compras','Estado','']} rows={suppliers.map(x=>{const due=open.filter(p=>p.supplierId===x.id).reduce((t,p)=>t+balance(p),0);return [<span className="w"><b>{x.name}</b>{x.email&&<small>{x.email}</small>}</span>,x.rfc?<code>{x.rfc}</code>:<span className="no">—</span>,<span className="w"><span>{x.contact||'—'}</span>{x.phone&&<small>{x.phone}</small>}</span>,x.creditDays?plural(x.creditDays,'día','días'):'Contado',due?<b>{money(due)}</b>:<span className="no">—</span>,purchases.filter(p=>p.supplierId===x.id&&p.status==='received').length,x.active?<Pill tone="green">Activo</Pill>:<Pill>Inactivo</Pill>,<Button className="ghost xs" icon="edit" onClick={()=>supplierForm(form,x)}>Editar</Button>];})} empty="Registra a tus proveedores para comprar y llevar sus saldos."/>
  </section>}
 </>;
}

export function ExpensesPage({state:s,form,action,run}:Omit<Props,'dialog'>){
 const accounts=list(s,'expenseaccount'),expenses=list(s,'expense'),moves=list(s,'pettymove');
 const balance=moves.reduce((t,m)=>t+(m.type==='in'?m.amount:-m.amount),0),fund=s.restaurant?.pettyFund||0;
 const month=expenses.filter(e=>Date.parse(e.at)>=monthStart()),spent=(id:string)=>month.filter(e=>e.accountId===id).reduce((t,e)=>t+e.amount,0);
 const over=accounts.filter(a=>a.budget>0&&spent(a.id)>a.budget);
 const cash=(s.cash||[]).find((c:Row)=>c.status==='open'&&!c.blind);
 const sourceLabel:Record<string,string>={petty:'Caja chica',register:'Caja',bank:'Banco'};
 const record=()=>form('Registrar gasto',[
  {key:'accountId',label:'Cuenta de gasto',type:'select',options:accounts.filter(a=>a.active).map(a=>({value:a.id,label:a.name}))},
  {key:'amount',label:'Importe MXN',type:'money',min:0.01,required:true},
  {key:'source',label:'Pagado con',type:'select',value:'petty',options:[{value:'petty',label:'Caja chica · saldo '+money(balance)},{value:'register',label:cash?'Efectivo de la caja abierta':'Efectivo de la caja (cerrada)'},{value:'bank',label:'Banco o tarjeta de la empresa'}]},
  {key:'description',label:'Concepto',required:true,full:true,placeholder:'Ej. Gas, jabón, reparación'},
  {key:'supplierId',label:'Proveedor',type:'select',value:'',options:[{value:'',label:'Sin proveedor'},...list(s,'supplier').filter(x=>x.active).map(x=>({value:x.id,label:x.name}))]},
  {key:'receipt',label:'Comprobante',placeholder:'Folio o ticket'}
 ],'expense.record',{},undefined,(x:any)=>({...x,amount:cents(x.amount)}));
 const account=(a?:Row)=>form(a?'Editar cuenta de gasto':'Cuenta de gasto',[{key:'name',label:'Nombre',value:a?.name,required:true,full:true,placeholder:'Ej. Limpieza, Mantenimiento, Gas'},{key:'budget',label:'Presupuesto mensual MXN',type:'money',value:a?a.budget/100:'',min:0,hint:'Vacío o 0 = sin presupuesto.'},...(a?[{key:'active',label:'Cuenta activa',type:'checkbox',value:a.active!==false}]:[])],'expense.account',{id:a?.id},undefined,(x:any)=>({...x,budget:x.budget===''?0:cents(x.budget),active:x.active??true}));
 const replenish=()=>form('Reponer caja chica',[{key:'amount',label:'Importe MXN',type:'money',value:fund>balance?(fund-balance)/100:'',min:0.01,required:true,hint:fund?'Fondo fijo '+money(fund)+' · saldo actual '+money(balance):undefined},{key:'fromRegister',label:'Tomar el efectivo de la caja abierta',type:'checkbox',value:!!cash,hint:'Si no, se registra como depósito externo.'},{key:'reason',label:'Motivo',value:'Reposición',required:true,full:true}],'petty.fund',{},undefined,(x:any)=>({...x,amount:cents(x.amount)}));
 const configure=()=>form('Fondo fijo de caja chica',[{key:'fund',label:'Fondo fijo MXN',type:'money',value:fund/100||'',min:0,hint:'Al reponer se sugiere la diferencia para volver a este monto.'}],'petty.configure',{},undefined,(x:any)=>({fund:x.fund===''?0:cents(x.fund)}));
 return <>
  <PageTools><Button className="ghost" icon="plus" onClick={()=>account()}>Cuenta de gasto</Button><Button className="ghost" icon="wallet" onClick={replenish}>Reponer caja chica</Button><Button className="primary" icon="minus" disabled={!accounts.some(a=>a.active)} onClick={record}>Registrar gasto</Button></PageTools>
  {!accounts.length&&<Notice>Crea cuentas de gasto (limpieza, gas, mantenimiento…) para clasificar cada salida de dinero.</Notice>}
  <Kpis>
   <MetricCard className="lead" label="Saldo de caja chica" value={money(balance)} note={fund?(balance<fund?'Reponer '+money(fund-balance)+' · fondo '+money(fund):'Fondo completo · '+money(fund)):'Sin fondo fijo configurado'} icon="wallet" tone="amber"/>
   <MetricCard label="Gastos del mes" value={money(month.reduce((t,e)=>t+e.amount,0))} note={plural(month.length,'gasto','gastos')} icon="minus" tone="blue"/>
   <MetricCard label="Sobre presupuesto" value={over.length} note={over.length?over.map(a=>a.name).join(', '):'Todas dentro del presupuesto'} icon="alert" tone={over.length?'red':'green'}/>
   <MetricCard label="Reposiciones" value={moves.filter(m=>m.type==='in').length} note={money(moves.filter(m=>m.type==='in').reduce((t,m)=>t+m.amount,0))+' en total'} icon="refresh" tone="purple"/>
  </Kpis>
  <div className="g g-side start">
   <section className="card panel"><PanelHeading title="Gasto por cuenta este mes" note="Contra su presupuesto mensual" icon="chart"/>
    {accounts.length?<div className="rows">{accounts.map(a=>{const v=spent(a.id);return <div className="r budget" key={a.id}><Sq icon="receipt" tone={a.budget&&v>a.budget?'red':'blue'}/><div className="w"><b>{a.name}{!a.active&&<small> · inactiva</small>}</b><span>{a.budget?money(v)+' de '+money(a.budget):money(v)+' · sin presupuesto'}</span>{a.budget>0&&<Bar value={v} max={a.budget} tone={v>a.budget?'r':v>a.budget*.8?'':'g'}/>}</div>{a.budget>0&&v>a.budget&&<Pill tone="red">+{money(v-a.budget)}</Pill>}<Button className="ghost xs icon-only" icon="edit" aria-label={'Editar '+a.name} onClick={()=>account(a)}>{''}</Button></div>;})}</div>:<Empty icon="receipt" title="Sin cuentas de gasto" text="Crea la primera para empezar a clasificar."/>}
   </section>
   <section className="card panel"><PanelHeading title="Caja chica" note="Entradas y salidas del fondo" icon="wallet" action={<Button className="ghost sm" icon="settings" onClick={configure}>Fondo fijo</Button>}/>
    {moves.length?<div className="rows">{moves.slice(-8).reverse().map(m=><div className="r" key={m.id}><Sq icon={m.type==='in'?'plus':'minus'} tone={m.type==='in'?'green':'amber'}/><div className="w"><b>{m.reason}</b><span>{date(m.at)}{m.fromRegister?' · desde la caja':''}</span></div><span className={'amt num '+(m.type==='in'?'pos':'')}>{(m.type==='in'?'+':'−')+money(m.amount)}</span></div>)}</div>:<Empty icon="wallet" title="Sin movimientos" text="Repón la caja chica para empezar."/>}
   </section>
  </div>
  <section className="card panel"><PanelHeading title="Gastos recientes" note="Cada gasto queda ligado a su cuenta y a su origen" icon="history"/>
   <DataTable columns={['Fecha','Cuenta','Concepto','Pagado con','Comprobante','>Importe']} rows={expenses.slice(-50).reverse().map(e=>[date(e.at),nameOf(s,'expenseaccount',e.accountId),<span className="w"><b>{e.description}</b>{e.supplierId&&<small>{nameOf(s,'supplier',e.supplierId)}</small>}</span>,<Pill tone={e.source==='petty'?'amber':e.source==='register'?'blue':'purple'}>{sourceLabel[e.source]}</Pill>,e.receipt||<span className="no">—</span>,<b>{money(e.amount)}</b>])} empty="Los gastos registrados aparecerán aquí."/>
  </section>
 </>;
}

// Manager PIN on the same device: pick who authorizes, type the PIN on a keypad.
export function PinDialog({managers,title,detail,ctx,authorize}:{managers:Row[];title:string;detail?:string;ctx:Ctx;authorize:(approverId:string,passcode:string)=>Promise<any>}){
 const [approver,setApprover]=useState(managers.length===1?managers[0].id:''),[code,setCode]=useState('');
 const input=useRef<HTMLInputElement>(null);useEffect(()=>{input.current?.focus({preventScroll:true});},[]);
 const press=(d:string)=>{setCode(c=>c.length<6?c+d:c);input.current?.focus({preventScroll:true});};
 const send=()=>ctx.submit(async()=>{try{const r=await authorize(approver,code);if(r&&r.ok===false)throw new Error(r.error+(r.remaining?' · quedan '+plural(r.remaining,'intento','intentos'):''));return r;}finally{setCode('');}});
 return <DialogFrame label={title} close={ctx.close} busy={ctx.busy} className="pin-dialog">
  <Head title={title} note={detail} close={ctx.close} busy={ctx.busy}/>
  {ctx.error&&<div className="alert error" role="alert">{ctx.error}</div>}
  {!managers.length?<Notice tone="warn">Ningún gerente tiene PIN asignado. Pídele que lo configure en su menú de perfil.</Notice>:<>
   <div className="pin-who" role="radiogroup" aria-label="Gerente que autoriza">{managers.map(m=><button type="button" key={m.id} role="radio" aria-checked={approver===m.id} className={'pin-person'+(approver===m.id?' on':'')} onClick={()=>setApprover(m.id)}><span className="av tone-amber">{m.name.slice(0,1)}</span>{m.name}</button>)}</div>
   <div className="pin-dots" aria-label={code.length+' dígitos escritos'}>{Array.from({length:6},(_,i)=><i key={i} className={i<code.length?'on':''}/>)}</div>
   <input className="sr-only" aria-label="PIN" inputMode="numeric" autoComplete="off" value={code} onChange={e=>setCode(e.target.value.replace(/\D/g,'').slice(0,6))} ref={input} onKeyDown={e=>{if(e.key==='Enter'&&approver&&code.length>=4)send();}}/>
   <div className="pin-pad">{['1','2','3','4','5','6','7','8','9'].map(d=><button type="button" key={d} onClick={()=>press(d)}>{d}</button>)}<button type="button" aria-label="Borrar todo" onClick={()=>setCode('')}><UiIcon name="close"/></button><button type="button" onClick={()=>press('0')}>0</button><button type="button" aria-label="Borrar último dígito" onClick={()=>setCode(c=>c.slice(0,-1))}><UiIcon name="arrow"/></button></div>
  </>}
  <div className="dialog-actions"><Button type="button" className="ghost" icon={false} onClick={ctx.close} disabled={ctx.busy}>Volver</Button><Button className="primary" icon="shield" disabled={ctx.busy||!approver||code.length<4} onClick={send}>Autorizar</Button></div>
 </DialogFrame>;
}
