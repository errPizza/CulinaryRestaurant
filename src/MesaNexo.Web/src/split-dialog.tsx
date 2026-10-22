import React,{useState} from 'react';
import {DialogFrame} from './motion';
import {Button,UiIcon} from './ui';

type Unit={id:string;name:string;price:number;person:number;status:string};
const money=(v:number)=>new Intl.NumberFormat('es-MX',{style:'currency',currency:'MXN'}).format((v||0)/100);

// Drag products between the current account and new folios; tapping a product and then "Mover aquí" does the same.
export function SplitDialog({units,people,close,submit,busy,error}:{units:Unit[];people:number;close:()=>void;submit:(groups:string[][])=>void;busy:boolean;error:string}){
  const [place,setPlace]=useState<Record<string,number>>(()=>Object.fromEntries(units.map(u=>[u.id,0])));
  const [columns,setColumns]=useState(1),[picked,setPicked]=useState<string[]>([]),[over,setOver]=useState<number|null>(null);
  const move=(ids:string[],to:number)=>{setPlace(p=>({...p,...Object.fromEntries(ids.map(id=>[id,to]))}));setPicked([]);};
  const byPerson=()=>{const persons=[...new Set(units.map(u=>u.person))].sort((a,b)=>a-b);setColumns(Math.max(1,persons.length-1));setPlace(Object.fromEntries(units.map(u=>[u.id,persons.indexOf(u.person)])));};
  const group=(c:number)=>units.filter(u=>place[u.id]===c);
  const groups=Array.from({length:columns},(_,i)=>group(i+1).map(u=>u.id)).filter(g=>g.length);
  const valid=groups.length>0&&group(0).length>0;
  const col=(c:number)=><section key={c} className={'split-col'+(over===c?' over':'')} onDragOver={e=>{e.preventDefault();setOver(c);}} onDragLeave={()=>setOver(null)} onDrop={e=>{e.preventDefault();setOver(null);const ids=JSON.parse(e.dataTransfer.getData('text/plain')||'[]');move(ids,c);}} aria-label={c===0?'Cuenta actual':'Nueva cuenta '+c}>
    <header><b>{c===0?'Cuenta actual':'Nueva cuenta '+c}</b><span className="num">{money(group(c).reduce((t,u)=>t+u.price,0))}</span></header>
    <div className="split-items">{group(c).map(u=><button type="button" key={u.id} draggable className={'split-item'+(picked.includes(u.id)?' picked':'')} aria-pressed={picked.includes(u.id)} onClick={()=>setPicked(p=>p.includes(u.id)?p.filter(x=>x!==u.id):[...p,u.id])} onDragStart={e=>{const ids=picked.includes(u.id)?picked:[u.id];e.dataTransfer.setData('text/plain',JSON.stringify(ids));e.dataTransfer.effectAllowed='move';}}><UiIcon name="dots"/><span className="w"><b>{u.name}</b><small>Persona {u.person}</small></span><span className="num">{money(u.price)}</span></button>)}
      {!group(c).length&&<p className="split-empty">{c===0?'Deja al menos un producto en la cuenta actual.':'Arrastra productos aquí.'}</p>}</div>
    {picked.length>0&&!picked.every(id=>place[id]===c)&&<Button type="button" className="ghost sm wide" icon="arrow" onClick={()=>move(picked,c)}>Mover aquí ({picked.length})</Button>}
  </section>;
  return <DialogFrame label="Dividir cuenta" close={close} busy={busy} className="split-dialog">
    <div className="dialog-head"><div><h2>Dividir cuenta</h2><p>Arrastra los productos a una cuenta nueva, o tócalos y pulsa «Mover aquí». No se reenvía nada a cocina.</p></div><Button className="ghost dialog-close" icon="close" onClick={close} disabled={busy} aria-label="Cerrar diálogo">{''}</Button></div>
    {error&&<div className="alert error" role="alert">{error}</div>}
    <div className="split-tools">{people>1&&<Button type="button" className="ghost sm" icon="users" onClick={byPerson}>Separar por persona</Button>}<Button type="button" className="ghost sm" icon="plus" disabled={columns>=6} onClick={()=>setColumns(c=>c+1)}>Otra cuenta nueva</Button></div>
    <div className="split-board" style={{gridTemplateColumns:`repeat(${columns+1},minmax(220px,1fr))`}}>{Array.from({length:columns+1},(_,c)=>col(c))}</div>
    <div className="dialog-actions"><Button type="button" className="ghost" icon={false} onClick={close} disabled={busy}>Volver</Button><Button className="primary" icon="split" disabled={busy||!valid} onClick={()=>submit(groups)}>{busy?'Dividiendo…':groups.length>1?'Crear '+groups.length+' cuentas':'Crear cuenta nueva'}</Button></div>
  </DialogFrame>;
}
