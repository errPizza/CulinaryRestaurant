import React,{useEffect,useState} from 'react';
import {createRoot} from 'react-dom/client';
import '@fontsource-variable/inter';
import './theme.css';
import {listenDisplay,type DisplayState} from './customer-display';

const money=(v:number)=>new Intl.NumberFormat('es-MX',{style:'currency',currency:'MXN'}).format((v||0)/100);
// Read-only view for the customer: no native bridge, no session, only what the operation window publishes.
function CustomerDisplay(){
  const [state,setState]=useState<DisplayState|null>(null),[banner,setBanner]=useState(0);
  useEffect(()=>listenDisplay(setState),[]);
  useEffect(()=>{const n=state?.banners.length||0;if(n<2)return;const t=setInterval(()=>setBanner(b=>(b+1)%n),6000);return()=>clearInterval(t);},[state?.banners.length]);
  const order=state?.order,banners=state?.banners||[];
  return <div className="cd">
    <header className="cd-head"><span className="brand-mark"><img className="brand-symbol" src="./branding/symbol.png" alt=""/></span><b>{state?.restaurant||'Culinary Restaurant'}</b></header>
    {order&&order.lines.length?<main className="cd-order">
      <section className="cd-lines"><div className="cd-title"><h1>{order.title}</h1>{order.label&&<span className="ui-status tone-amber">{order.label}</span>}</div>
        <ul>{order.lines.map((l,i)=><li key={i} className="stagger"><span className="cd-qty">{l.quantity}×</span><span className="cd-name"><b>{l.name}</b>{l.detail&&<small>{l.detail}</small>}</span><span className="cd-amount">{money(l.amount)}</span></li>)}</ul></section>
      <aside className="cd-total"><small>Total a pagar</small><strong key={order.total}>{money(order.total)}</strong>
        {order.tendered!==undefined&&<div className="cd-change"><span>Recibido <b>{money(order.tendered)}</b></span><span>Su cambio <b>{money(order.change||0)}</b></span></div>}
        {order.paid&&<p className="cd-thanks">¡Gracias por su compra!</p>}</aside>
    </main>:<main className="cd-welcome"><h1>Bienvenido</h1><p>Su orden aparecerá aquí.</p></main>}
    {banners.length>0&&<footer className="cd-banner"><span key={banner}>{banners[banner%banners.length]}</span></footer>}
  </div>;
}
createRoot(document.getElementById('root')!).render(<CustomerDisplay/>);
