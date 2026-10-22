import {Button} from './ui';
import React, {useState} from 'react';
import {DialogFrame} from './motion';
import {cookingLevels, supportsCooking} from '../shared/product-options';

export type ModifierGroup = {name:string;required?:boolean;max?:number;options:{name:string;price:number}[]};
export type Product = {id:string;name:string;category:string;description:string;price:number;station:string;extras:{name:string;price:number}[];supportsCooking?:boolean;cookingRequired?:boolean;modifiers?:ModifierGroup[]};
export type Selection = {productId:string;quantity:number;person:number;notes:string;cooking:string;extras:string[];course:string;held:boolean;modifiers:{group:string;options:string[]}[]};
const money = (value:number) => new Intl.NumberFormat('es-MX',{style:'currency',currency:'MXN'}).format(value/100);

// Price of one unit with its extras and chosen modifier options; the server recomputes it on send.
export function linePrice(product:Product|undefined,selection:Pick<Selection,'extras'|'modifiers'>){
  if(!product)return 0;
  const extras=product.extras.filter(e=>selection.extras.includes(e.name)).reduce((t,e)=>t+e.price,0);
  const mods=(product.modifiers||[]).reduce((t,g)=>t+g.options.filter(o=>selection.modifiers.find(m=>m.group===g.name)?.options.includes(o.name)).reduce((a,o)=>a+o.price,0),0);
  return product.price+extras+mods;
}
export function missingChoices(product:Product|undefined,selection:Pick<Selection,'cooking'|'modifiers'>){
  if(!product)return [];
  const missing=(product.modifiers||[]).filter(g=>g.required&&!selection.modifiers.find(m=>m.group===g.name)?.options.length).map(g=>g.name);
  if(supportsCooking(product)&&product.cookingRequired&&!selection.cooking)missing.unshift('Punto de cocción');
  return missing;
}

export function OrderProductDialog({products,people,allowCourses,close,submit,busy,error,fixedProductId,title='Agregar productos al borrador',submitLabel='Agregar al borrador',showPerson=true}:{
  products:Product[];people:number;allowCourses:boolean;close:()=>void;submit:(value:Selection)=>void;busy:boolean;error:string;fixedProductId?:string;title?:string;submitLabel?:string;showPerson?:boolean;
}) {
  const [selection,setSelection] = useState<Selection>({productId:fixedProductId||products[0]?.id || '',quantity:1,person:1,notes:'',cooking:'',extras:[],course:'principal',held:false,modifiers:[]});
  const [qty,setQty] = useState('1'),[person,setPerson] = useState('1'),[tried,setTried] = useState(false);
  const quantity = Number(qty) || 0;
  const product = products.find(p => p.id === selection.productId);
  const extras = product?.extras || [];
  const cooking = product ? supportsCooking(product) : false;
  const unitPrice = linePrice(product,selection);
  const missing = missingChoices(product,selection);
  const patch = (value:Partial<Selection>) => setSelection(current => ({...current,...value}));
  const chosen = (group:string) => selection.modifiers.find(m=>m.group===group)?.options||[];
  const toggle = (g:ModifierGroup,option:string) => {
    const current=chosen(g.name),max=g.max||1;
    const next=current.includes(option)?current.filter(o=>o!==option):max===1?[option]:current.length<max?[...current,option]:current;
    patch({modifiers:[...selection.modifiers.filter(m=>m.group!==g.name),...(next.length?[{group:g.name,options:next}]:[])]});
  };
  return <DialogFrame label={title} close={close} busy={busy} className="order-product-dialog">
    <div className="dialog-head order-dialog-heading"><div><h2>{title}</h2><p>Personaliza el pedido antes de enviarlo a cocina.</p></div><Button className="ghost dialog-close" icon="close" onClick={close} disabled={busy} aria-label="Cerrar diálogo">{""}</Button></div>
    {error && <div className="alert error" role="alert">{error}</div>}
    <form onSubmit={event => {event.preventDefault();setTried(true);if(!product||missing.length)return;submit({...selection,quantity:Number(qty),person:Number(person),cooking:cooking?selection.cooking:'',extras:selection.extras.filter(name=>extras.some(e=>e.name===name))});}}>
      {!fixedProductId&&<label>Producto<select aria-label="Producto" value={selection.productId} required disabled={busy || !products.length} onChange={event => patch({productId:event.target.value,extras:[],cooking:'',notes:'',modifiers:[]})}>
        {!products.length && <option value="">Sin productos disponibles</option>}
        {products.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
      </select></label>}
      {product ? <>
        <div className="order-product-summary"><div><span className="ui-status tone-neutral">{product.category}</span><h3>{product.name}</h3><p>{product.description}</p></div><div className="order-base-price"><b>{money(product.price)}</b><small>por unidad</small></div></div>
        <div className="order-counts"><div className="order-quantity"><span id="order-quantity-label">Cantidad</span><div className="quantity-control"><Button type="button" icon={false} aria-label="Reducir cantidad" disabled={busy || Number(qty)<=1} onClick={()=>setQty(String(Math.max(1,(Number(qty)||1)-1)))}>−</Button><input aria-label="Cantidad" type="number" inputMode="numeric" min={1} max={50} required value={qty} disabled={busy} onFocus={event=>event.target.select()} onChange={event=>setQty(event.target.value)}/><Button type="button" icon={false} aria-label="Aumentar cantidad" disabled={busy || Number(qty)>=50} onClick={()=>setQty(String(Math.min(50,(Number(qty)||0)+1)))}>+</Button></div></div>
          {showPerson&&<div className="order-quantity"><span id="order-person-label">Persona</span><div className="quantity-control"><Button type="button" icon={false} aria-label="Reducir persona" disabled={busy || Number(person)<=1} onClick={()=>setPerson(String(Math.max(1,(Number(person)||1)-1)))}>−</Button><input aria-label="Persona" type="number" inputMode="numeric" min={1} max={Math.max(1,people)} required value={person} disabled={busy} onFocus={event=>event.target.select()} onChange={event=>setPerson(event.target.value)}/><Button type="button" icon={false} aria-label="Aumentar persona" disabled={busy || Number(person)>=Math.max(1,people)} onClick={()=>setPerson(String(Math.min(Math.max(1,people),(Number(person)||0)+1)))}>+</Button></div></div>}</div>
        {cooking && <fieldset className={'order-modifiers cooking-modifiers'+(tried&&missing.includes('Punto de cocción')?' invalid':'')} disabled={busy}><legend>Punto de cocción <span>{product.cookingRequired?'Obligatorio':'Opcional'}</span></legend><div className="cooking-options">{[...(product.cookingRequired?[]:['']),...cookingLevels].map(level=><label className={'cooking-choice '+(selection.cooking===level?'is-checked':'')} key={level}><input type="radio" name="cooking" value={level} checked={selection.cooking===level} onChange={()=>patch({cooking:level})}/><span>{level || 'Sin especificar'}</span></label>)}</div></fieldset>}
        {(product.modifiers||[]).map(g=><fieldset key={g.name} className={'order-modifiers'+(tried&&missing.includes(g.name)?' invalid':'')} disabled={busy}><legend>{g.name} <span>{g.required?'Obligatorio':'Opcional'}{(g.max||1)>1?' · hasta '+g.max:''}</span></legend><div className="modifier-grid">{g.options.map(o=>{const on=chosen(g.name).includes(o.name);return <label className={'modifier-choice '+(on?'is-checked':'')} key={o.name}><input type={(g.max||1)===1?'radio':'checkbox'} name={'mod-'+g.name} checked={on} onChange={()=>toggle(g,o.name)} onClick={()=>{if((g.max||1)===1&&on&&!g.required)toggle(g,o.name);}}/><span>{o.name}</span>{o.price>0&&<b>+{money(o.price)}</b>}</label>;})}</div></fieldset>)}
        {extras.length>0&&<fieldset className="order-modifiers" disabled={busy}><legend>Extras <span>Opcionales · precio por unidad</span></legend><div className="modifier-grid">{extras.map(extra=><label className={'modifier-choice '+(selection.extras.includes(extra.name)?'is-checked':'')} key={extra.name}><input type="checkbox" checked={selection.extras.includes(extra.name)} onChange={event=>patch({extras:event.target.checked?[...selection.extras,extra.name]:selection.extras.filter(name=>name!==extra.name)})}/><span>{extra.name}</span><b>+{money(extra.price)}</b></label>)}</div></fieldset>}
        <label className="order-notes">{product.station==='Barra'?'Notas para barra':'Notas para cocina'}<textarea placeholder={product.station==='Barra'?'Ej. sin hielo, poco azúcar…':'Ej. sin cebolla, aderezo aparte…'} value={selection.notes} maxLength={500} disabled={busy} onChange={event=>patch({notes:event.target.value})}/><small className="field-hint">{selection.notes.length} de 500 caracteres</small></label>
        {allowCourses && <details className="order-course-details"><summary>Servicio por tiempos <span>Opcional</span></summary><div className="order-courses"><label>Tiempo<select value={selection.course} disabled={busy} onChange={event=>patch({course:event.target.value})}><option value="entrada">Entrada</option><option value="principal">Principal</option><option value="postre">Postre</option></select></label><label className="checkbox-label"><input type="checkbox" checked={selection.held} disabled={busy} onChange={event=>patch({held:event.target.checked})}/>Retener hasta liberación de cocina</label></div></details>}
        <div className="order-price-summary" aria-live="polite"><div><small>{quantity} × {money(unitPrice)} · incluye modificadores y extras</small><span>Subtotal estimado</span></div><strong key={unitPrice*quantity}>{money(unitPrice*quantity)}</strong></div>
        {tried&&missing.length>0?<p className="order-missing" role="alert">Falta elegir: {missing.join(', ')}</p>:<small>Las promociones aplicables se calculan al confirmar la comanda.</small>}
      </> : <p className="order-no-extras">No hay productos disponibles para agregar.</p>}
      <div className="dialog-actions"><Button type="button" className="ghost" onClick={close} disabled={busy}>Volver</Button><Button className="primary" disabled={busy || !product}>{busy?'Guardando…':submitLabel}</Button></div>
    </form>
  </DialogFrame>;
}
