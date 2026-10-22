import React,{useEffect,useMemo,useState} from 'react';
import {Button,UiIcon,PageTools,PanelHeading,Empty,Notice} from './ui';
import {createPortal} from 'react-dom';
import {Presence} from './motion';
import {OrderProductDialog,linePrice,type Product,type Selection} from './order-product-dialog';
import {supportsCooking} from '../shared/product-options';
import {publishDisplay,openCustomerDisplay} from './customer-display';
import {walletBalance} from './loyalty';
import {simOn} from './integrations';

type Row={id:string;[key:string]:any};
type Line={key:string;product:Product;quantity:number;selection:Omit<Selection,'quantity'|'person'|'course'|'held'|'productId'>};
const money=(v:number)=>new Intl.NumberFormat('es-MX',{style:'currency',currency:'MXN'}).format((v||0)/100);
const QUICK_CASH=[5000,10000,20000,50000];
const art=(c:string)=>c==='Bebidas'?2:c==='Postres'?3:/entrada|sopa/i.test(c||'')?0:1;
const detailOf=(l:Line)=>[l.selection.cooking,...l.selection.modifiers.flatMap(m=>m.options),...l.selection.extras.map(e=>'+ '+e),l.selection.notes].filter(Boolean).join(' · ');
const needsDialog=(p:Product)=>supportsCooking(p)||(p.extras||[]).length>0||(p.modifiers||[]).length>0;

// Counter service: catalog first, cart beside it, cash shortcuts that charge and close in one tap.
export function CounterPage({state:s,action,run,busy,error,goCash}:{state:any;action:(type:string,payload:any)=>Promise<any>;run:(fn:()=>Promise<any>,message?:string)=>Promise<void>;busy:boolean;error:string;goCash?:()=>void}){
  const products:Product[]=useMemo(()=>(s.products||[]).filter((p:Row)=>p.visible&&p.available),[s.products]);
  const categories:string[]=[...new Set(products.map(p=>p.category))];
  const [category,setCategory]=useState('all'),[search,setSearch]=useState(''),[lines,setLines]=useState<Line[]>([]),[picking,setPicking]=useState<Product|null>(null);
  const [takeout,setTakeout]=useState(false),[name,setName]=useState(''),[other,setOther]=useState(''),[done,setDone]=useState<any>(null);
  const cashOpen=(s.cashs||[]).some((c:Row)=>c.status==='open');
  // Employee meals and frequent customers share the cart; the server applies the staff discount and wallet rules.
  const [mode,setMode]=useState<'sale'|'staff'>('sale'),[customerId,setCustomerId]=useState(''),[employeeId,setEmployeeId]=useState(''),[settle,setSettle]=useState('payroll');
  const me=s.user||{},manager=me.role==='manager',staffCfg=s.restaurant?.staff||{};
  const employees:Row[]=manager?(s.users||[]).filter((w:Row)=>w.active&&!['owner','dueno'].includes(w.role)):[me];
  const customers:Row[]=s.customers||[],balance=customerId?walletBalance(s,customerId):0;
  const total=lines.reduce((t,l)=>t+linePrice(l.product,l.selection)*l.quantity,0);
  const staffTotal=Math.round(total*(10000-(staffCfg.discountBps||0))/10000);
  const term=search.trim().toLowerCase();
  const shown=products.filter(p=>(category==='all'||p.category===category)&&(!term||(p.name+' '+(p.description||'')).toLowerCase().includes(term)));
  const add=(product:Product,sel?:Selection)=>{setDone(null);const selection={notes:sel?.notes||'',cooking:sel?.cooking||'',extras:sel?.extras||[],modifiers:sel?.modifiers||[]};const key=product.id+'|'+JSON.stringify(selection);
    setLines(current=>{const found=current.find(l=>l.key===key);return found?current.map(l=>l===found?{...l,quantity:Math.min(50,l.quantity+(sel?.quantity||1))}:l):[...current,{key,product,quantity:sel?.quantity||1,selection}];});};
  const setQty=(key:string,q:number)=>setLines(current=>q<=0?current.filter(l=>l.key!==key):current.map(l=>l.key===key?{...l,quantity:Math.min(50,q)}:l));
  const title=(takeout?'Para llevar':'Mostrador')+(name.trim()?' · '+name.trim():'');
  useEffect(()=>{publishDisplay({restaurant:s.restaurant?.name||'Culinary Restaurant',banners:s.restaurant?.displayBanners||[],order:done?{title:done.title,label:done.label,lines:done.lines,total:done.total,tendered:done.tendered,change:done.change,paid:true}:lines.length?{title,label:takeout?'PARA LLEVAR':undefined,lines:lines.map(l=>({quantity:l.quantity,name:l.product.name,detail:detailOf(l),amount:linePrice(l.product,l.selection)*l.quantity})),total}:undefined});},[lines,done,takeout,name,s.restaurant?.name,s.restaurant?.displayBanners]);
  const items=()=>lines.map(l=>({productId:l.product.id,quantity:l.quantity,notes:l.selection.notes,cooking:l.selection.cooking,extras:l.selection.extras,modifiers:l.selection.modifiers}));
  const staffMeal=()=>run(async()=>{const who=employees.find(w=>w.id===(employeeId||me.id));const r=await action('staff.meal',{employeeId:employeeId||me.id,settle,items:items()});setDone({...r,title:'Empleado · '+(who?.name||''),method:settle,staff:true,lines:lines.map(l=>({quantity:l.quantity,name:l.product.name,detail:detailOf(l),amount:linePrice(l.product,l.selection)*l.quantity}))});setLines([]);},'Consumo registrado');
  const pay=(method:'cash'|'card'|'transfer'|'wallet',tendered?:number)=>run(async()=>{
    const r=await action('sale.quick',{method,takeout,customerName:name.trim(),...(method==='card'&&simOn(s,'terminal')?{terminal:true}:{}),...(customerId?{customerId}:{}),...(method==='cash'?{tendered:tendered??total}:{}),items:lines.map(l=>({productId:l.product.id,quantity:l.quantity,notes:l.selection.notes,cooking:l.selection.cooking,extras:l.selection.extras,modifiers:l.selection.modifiers}))});
    setDone({...r,title,method,tendered:method==='cash'?(tendered??total):undefined,lines:lines.map(l=>({quantity:l.quantity,name:l.product.name,detail:detailOf(l),amount:linePrice(l.product,l.selection)*l.quantity}))});setLines([]);setOther('');
  },'Venta registrada');
  const otherCents=Math.round((Number(other)||0)*100);
  return <div className="counter">
    <PageTools><Button className="ghost" icon="monitor" onClick={()=>void openCustomerDisplay()}>Pantalla del cliente</Button></PageTools>
    {!cashOpen&&<Notice tone="warn">La caja está cerrada. <b>Ábrela para cobrar en mostrador.</b>{goCash&&<> <Button className="ghost xs" icon="wallet" onClick={goCash}>Ir a caja</Button></>}</Notice>}
    <div className="counter-grid">
      <section className="counter-catalog">
        <div className="filterbar"><div className="chips" role="group" aria-label="Categorías" style={{paddingLeft:6}}>{['all',...categories].map(c=><button type="button" key={c} className="chip" aria-pressed={category===c} onClick={()=>setCategory(c)}>{c==='all'?'Todos':c} <span className="c">{c==='all'?products.length:products.filter(p=>p.category===c).length}</span></button>)}</div><label className="ui-search"><UiIcon name="search"/><input aria-label="Buscar producto" placeholder="Buscar producto…" value={search} onChange={e=>setSearch(e.target.value)}/></label></div>
        {shown.length?<div className="counter-products stagger">{shown.map(p=><button type="button" key={p.id} className={'counter-product art-'+art(p.category)} aria-label={'Agregar '+p.name} onClick={()=>needsDialog(p)?setPicking(p):add(p)}><span className="cp-name">{p.name}</span><span className="cp-meta">{needsDialog(p)&&<span className="cp-opt"><UiIcon name="sliders"/>Opciones</span>}<b>{money(p.price)}</b></span></button>)}</div>
          :<section className="card"><Empty icon="food" title={products.length?'Sin productos con este filtro':'Catálogo vacío'} text={products.length?'Prueba con otra categoría o búsqueda.':'Agrega productos en Administración › Catálogo.'}/></section>}
      </section>
      <aside className="card counter-cart" aria-label="Orden actual">
        <PanelHeading title="Orden actual" note={lines.length?lines.reduce((t,l)=>t+l.quantity,0)+' productos':'Toca un producto para agregarlo'} icon="receipt" action={lines.length>0&&<Button className="text-button" icon="close" onClick={()=>setLines([])}>Vaciar</Button>}/>
        <div className="seg counter-mode" role="group" aria-label="Tipo de venta"><Button type="button" icon={false} className={mode==='sale'?'active':''} aria-pressed={mode==='sale'} onClick={()=>setMode('sale')}>Venta</Button><Button type="button" icon={false} className={mode==='staff'?'active':''} aria-pressed={mode==='staff'} onClick={()=>{setMode('staff');setCustomerId('');}}>Consumo de empleado</Button></div>
        {mode==='staff'?<div className="counter-staff"><label>Empleado<select value={employeeId||me.id} onChange={e=>setEmployeeId(e.target.value)} disabled={!manager}>{employees.map(w=><option key={w.id} value={w.id}>{w.name}</option>)}</select></label><label>Se paga con<select value={settle} onChange={e=>setSettle(e.target.value)}><option value="payroll">Descuento por nómina</option><option value="cash">Efectivo</option><option value="card">Tarjeta</option></select></label><small>Descuento de empleado {((staffCfg.discountBps||0)/100).toLocaleString('es-MX')} %{staffCfg.monthlyLimit?' · tope mensual '+money(staffCfg.monthlyLimit):''}</small></div>:<>
        <div className="seg" role="group" aria-label="Tipo de servicio"><Button type="button" icon={false} className={!takeout?'active':''} aria-pressed={!takeout} onClick={()=>setTakeout(false)}>Comer aquí</Button><Button type="button" icon={false} className={takeout?'active':''} aria-pressed={takeout} onClick={()=>setTakeout(true)}>Para llevar</Button></div>
        <label style={{marginTop:12}}>Nombre del cliente <small>Opcional · aparece en la comanda</small><input value={name} maxLength={80} onChange={e=>setName(e.target.value)} placeholder="Ej. Mario"/></label>
        <label style={{marginTop:10}}>Cliente frecuente <small>Opcional · acumula cashback y paga con monedero</small><select value={customerId} onChange={e=>{setCustomerId(e.target.value);const c=customers.find(x=>x.id===e.target.value);if(c&&!name.trim())setName(c.name);}}><option value="">Sin cliente registrado</option>{customers.map(c=><option key={c.id} value={c.id}>{c.name} · {c.phone} · {money(walletBalance(s,c.id))}</option>)}</select></label></>}
        {done?<div className="counter-done"><span className="sq tone-green"><UiIcon name="check"/></span><b>{done.staff?'Consumo registrado':'Venta registrada'} · {money(done.total)}</b>{done.method==='cash'&&!done.staff?<p>Su cambio <strong>{money(done.change)}</strong></p>:<p>{done.method==='payroll'?'Cargo a nómina':done.method==='wallet'?'Pagado con monedero':'Pago con '+(done.method==='card'?(simOn(s,'terminal')?'tarjeta · terminal simulada':'tarjeta'):done.method==='cash'?'efectivo':'transferencia')}</p>}{done.label&&<span className="ui-status tone-amber">{done.label}</span>}<Button className="primary wide" icon="plus" onClick={()=>{setDone(null);setName('');setTakeout(false);setCustomerId('');}}>Nueva venta</Button></div>
        :lines.length?<div className="counter-lines">{lines.map(l=><div className="counter-line" key={l.key}><div className="w"><b>{l.product.name}</b>{detailOf(l)&&<small>{detailOf(l)}</small>}</div><div className="stepper"><Button type="button" icon={false} aria-label={'Quitar uno de '+l.product.name} onClick={()=>setQty(l.key,l.quantity-1)}>−</Button><span className="num" aria-label="Cantidad">{l.quantity}</span><Button type="button" icon={false} aria-label={'Agregar uno de '+l.product.name} onClick={()=>setQty(l.key,l.quantity+1)}>+</Button></div><span className="amt num">{money(linePrice(l.product,l.selection)*l.quantity)}</span></div>)}</div>
        :<Empty icon="receipt" title="Orden vacía" text="Los productos que agregues aparecerán aquí."/>}
        {!done&&<>
          <div className="counter-total"><span>Total{mode==='staff'&&staffCfg.discountBps?<s>{money(total)}</s>:null}</span><strong className="num" key={mode+total}>{money(mode==='staff'?staffTotal:total)}</strong></div>
          {mode==='staff'?<Button className="primary lg wide counter-staff-go" icon="check" disabled={busy||!lines.length||(settle!=='payroll'&&!cashOpen)} onClick={staffMeal}>{settle==='payroll'?'Cargar a nómina':'Cobrar consumo'}</Button>:<>
          <h4 className="sub-h" style={{margin:'14px 0 8px'}}>Efectivo rápido</h4>
          <div className="quick-cash">{QUICK_CASH.map(v=><Button key={v} className="ghost lg" icon={false} disabled={busy||!lines.length||!cashOpen||v<total} onClick={()=>pay('cash',v)}>{money(v).replace('.00','')}</Button>)}<Button className="primary lg" icon="cash" disabled={busy||!lines.length||!cashOpen} onClick={()=>pay('cash',total)}>Exacto</Button></div>
          <div className="counter-other"><span className="affix"><span aria-hidden="true">$</span><input type="number" inputMode="decimal" min={0} step="0.01" aria-label="Otro importe recibido" placeholder="Otro importe" value={other} onFocus={e=>e.target.select()} onChange={e=>setOther(e.target.value)}/></span><Button className="ghost" icon="cash" disabled={busy||!lines.length||!cashOpen||otherCents<total} onClick={()=>pay('cash',otherCents)}>Cobrar{otherCents>=total&&total>0?' · cambio '+money(otherCents-total):''}</Button></div>
          <div className="counter-methods"><Button className="ghost" icon="wallet" disabled={busy||!lines.length||!cashOpen} onClick={()=>pay('card')} title={simOn(s,'terminal')?'Cobra en la terminal simulada (tarjeta de prueba aprobada)':undefined}>{simOn(s,'terminal')?'Tarjeta · terminal':'Tarjeta'}</Button><Button className="ghost" icon="swap" disabled={busy||!lines.length||!cashOpen} onClick={()=>pay('transfer')}>Transferencia</Button></div>
          {customerId&&<Button className="ghost wide counter-wallet" icon="star" disabled={busy||!lines.length||!cashOpen||balance<total} onClick={()=>pay('wallet')}>Pagar con monedero · saldo {money(balance)}</Button>}
          </>}
        </>}
      </aside>
    </div>
    {createPortal(<Presence>{picking&&<OrderProductDialog key={picking.id} products={products} people={1} allowCourses={false} fixedProductId={picking.id} showPerson={false} title={'Agregar '+picking.name} submitLabel="Agregar a la orden" close={()=>setPicking(null)} busy={busy} error={error} submit={sel=>{add(picking,sel);setPicking(null);}}/>}</Presence>,document.body)}
  </div>;
}
