import React,{useId,useState} from 'react';
import {DialogFrame} from './motion';
import {Button,Sq,UiIcon} from './ui';

export type Option={value:string;label:string};
export type Column={key:string;label:string;type?:'text'|'number'|'select';options?:Option[];placeholder?:string;width?:string;step?:string;prefix?:string};
// Field types beyond the native inputs:
//  money   number with a "$" prefix          tags  chips with suggestions; value is "a, b, c"
//  multi   chip toggles; value is string[]    rows  editable rows; value is text lines (see `rows`)
//  section heading that groups the fields after it
export type Field={key:string;label:string;type?:string;value?:any;options?:Option[];required?:boolean;hint?:string;min?:number;max?:number;step?:string;placeholder?:string;
 half?:boolean;full?:boolean;showIf?:(values:any)=>boolean;suggestions?:string[];
 rows?:{columns:Column[];split:(line:string)=>string[];join:(cells:string[])=>string;add:string;empty?:string}};
export type Modal={title:string;description?:string;fields:Field[];submit:string;run:(v:any)=>Promise<any>;danger?:boolean;products?:any[];people?:number;allowCourses?:boolean;instanceId?:string;
 // A fully custom dialog (split board, etc.); `submit` runs `run` with the value and closes on success.
 render?:(ctx:{close:()=>void;submit:(v:any)=>void;busy:boolean;error:string})=>React.ReactNode};

const SHORT=new Set(['number','money','select','date','datetime-local','color','password']);
const splitList=(v:any)=>String(v??'').split(',').map(x=>x.trim()).filter(Boolean);
const initial=(f:Field)=>{
 if(f.type==='select')return f.options?.some(o=>o.value===f.value)?f.value:f.options?.[0]?.value||'';
 if(f.type==='checkbox')return !!f.value;
 if(f.type==='multi')return Array.isArray(f.value)?f.value:[];
 if(f.type==='number'||f.type==='money')return f.value===undefined||f.value===null?'':String(f.value);
 return f.value??'';
};

function Tags({id,value,onChange,suggestions=[],placeholder,label}:{id:string;value:string;onChange:(v:string)=>void;suggestions?:string[];placeholder?:string;label:string}){
 const items=splitList(value),[draft,setDraft]=useState('');
 const has=(t:string)=>items.some(x=>x.toLowerCase()===t.toLowerCase());
 const set=(next:string[])=>onChange(next.join(', '));
 const add=(raw:string)=>{const parts=raw.split(',').map(x=>x.trim()).filter(x=>x&&!has(x));if(parts.length)set([...items,...parts]);setDraft('');};
 const offered=suggestions.filter(s=>!has(s));
 return <div className="tags-field">
  <div className="tags-box" onClick={e=>(e.currentTarget.querySelector('input') as HTMLInputElement)?.focus()}>
   {items.map(t=><span className="tag-chip" key={t}>{t}<button type="button" aria-label={'Quitar '+t} onClick={()=>set(items.filter(x=>x!==t))}><UiIcon name="close"/></button></span>)}
   <input id={id} aria-label={label} value={draft} placeholder={items.length?'':placeholder||'Escribe y presiona Enter'} onChange={e=>{const v=e.target.value;if(v.includes(','))add(v);else setDraft(v);}} onKeyDown={e=>{if(e.key==='Enter'){e.preventDefault();add(draft);}else if(e.key==='Backspace'&&!draft&&items.length)set(items.slice(0,-1));}} onBlur={()=>draft&&add(draft)}/>
  </div>
  {offered.length>0&&<div className="tag-suggestions" aria-label={'Sugerencias para '+label}>{offered.map(s=><button type="button" className="chip sm" key={s} onClick={()=>add(s)}><UiIcon name="plus"/>{s}</button>)}</div>}
 </div>;
}

type Group={name:string;required:boolean;max:number;options:{name:string;price:string}[]};
// Modifier groups: each one has a name, whether it is mandatory, how many options may be chosen and its options with price.
function Groups({value,onChange}:{value:string;onChange:(v:string)=>void}){
 const parse=():Group[]=>{try{return (JSON.parse(value||'[]') as any[]).map(g=>({name:g.name||'',required:!!g.required,max:Number(g.max)||1,options:(g.options||[]).map((o:any)=>({name:o.name||'',price:o.price===undefined||o.price===''?'':String(o.price)}))}));}catch{return [];}};
 const [groups,setGroups]=useState<Group[]>(parse);
 const commit=(next:Group[])=>{setGroups(next);onChange(JSON.stringify(next));};
 const patch=(i:number,p:Partial<Group>)=>commit(groups.map((g,k)=>k===i?{...g,...p}:g));
 return <div className="groups-field">
  {groups.map((g,i)=><div className="group-card" key={i}>
   <div className="group-head"><input aria-label={'Nombre del grupo '+(i+1)} placeholder="Ej. Término, Salsa, Tamaño" value={g.name} onChange={e=>patch(i,{name:e.target.value})}/>
    <label className="toggle-inline"><input type="checkbox" role="switch" checked={g.required} onChange={e=>patch(i,{required:e.target.checked})}/>Obligatorio</label>
    <label className="max-inline">Elegir hasta<input type="number" inputMode="numeric" min={1} max={20} value={g.max} aria-label={'Máximo de opciones del grupo '+(i+1)} onFocus={e=>e.target.select()} onChange={e=>patch(i,{max:Math.max(1,Math.min(20,Number(e.target.value)||1))})}/></label>
    <Button type="button" className="ghost icon-only sm" icon="close" aria-label={'Quitar grupo '+(i+1)} onClick={()=>commit(groups.filter((_,k)=>k!==i))}>{''}</Button></div>
   {g.options.map((o,j)=><div className="rows-line" key={j} style={{gridTemplateColumns:'1fr 150px 34px'}}><input aria-label={'Opción '+(j+1)+' del grupo '+(i+1)} placeholder="Ej. Término medio" value={o.name} onChange={e=>patch(i,{options:g.options.map((x,m)=>m===j?{...x,name:e.target.value}:x)})}/><span className="affix wide"><span aria-hidden="true">+$</span><input aria-label={'Precio de la opción '+(j+1)+' del grupo '+(i+1)} type="number" inputMode="decimal" min={0} step="0.01" placeholder="0.00" value={o.price} onFocus={e=>e.target.select()} onChange={e=>patch(i,{options:g.options.map((x,m)=>m===j?{...x,price:e.target.value}:x)})}/></span><Button type="button" className="ghost icon-only sm" icon="close" aria-label={'Quitar opción '+(j+1)} onClick={()=>patch(i,{options:g.options.filter((_,m)=>m!==j)})}>{''}</Button></div>)}
   <Button type="button" className="text-button" icon="plus" onClick={()=>patch(i,{options:[...g.options,{name:'',price:''}]})}>Agregar opción</Button>
  </div>)}
  {!groups.length&&<p className="rows-empty">Sin grupos. Úsalos para elecciones como término, salsa o tamaño; los obligatorios bloquean la comanda hasta elegir.</p>}
  <Button type="button" className="ghost sm" icon="plus" disabled={groups.length>=10} onClick={()=>commit([...groups,{name:'',required:true,max:1,options:[{name:'',price:''}]}])}>Agregar grupo</Button>
 </div>;
}
function Rows({field,value,onChange}:{field:Field;value:string;onChange:(v:string)=>void}){
 const spec=field.rows!;
 const rows=String(value||'').split('\n').filter(l=>l.trim()).map(spec.split);
 const [list,setList]=useState<string[][]>(()=>rows.length?rows:[]);
 const commit=(next:string[][])=>{setList(next);onChange(next.filter(r=>r.some(c=>String(c).trim())).map(spec.join).join('\n'));};
 const blank=()=>spec.columns.map(c=>c.type==='select'?c.options?.[0]?.value||'':'');
 return <div className="rows-field">
  {list.length>0&&<div className="rows-head" style={{gridTemplateColumns:spec.columns.map(c=>c.width||'1fr').join(' ')+' 34px'}} aria-hidden="true">{spec.columns.map(c=><span key={c.key}>{c.label}</span>)}<span/></div>}
  {list.map((row,i)=><div className="rows-line" key={i} style={{gridTemplateColumns:spec.columns.map(c=>c.width||'1fr').join(' ')+' 34px'}}>
   {spec.columns.map((c,j)=>{const set=(v:string)=>commit(list.map((r,k)=>k===i?r.map((x,m)=>m===j?v:x):r));const name=c.label+' '+(i+1);
    if(c.type==='select')return <select key={c.key} aria-label={name} value={row[j]} onChange={e=>set(e.target.value)}>{c.options?.map(o=><option key={o.value} value={o.value}>{o.label}</option>)}</select>;
    const input=<input key={c.key} aria-label={name} type={c.type==='number'?'number':'text'} inputMode={c.type==='number'?'decimal':undefined} step={c.step||'any'} min={c.type==='number'?0:undefined} placeholder={c.placeholder} value={row[j]??''} onFocus={e=>c.type==='number'&&e.target.select()} onChange={e=>set(e.target.value)}/>;
    return c.prefix?<span className="affix" key={c.key}><span aria-hidden="true">{c.prefix}</span>{input}</span>:input;})}
   <Button type="button" className="ghost icon-only sm" icon="close" aria-label={'Quitar fila '+(i+1)} onClick={()=>commit(list.filter((_,k)=>k!==i))}>{''}</Button>
  </div>)}
  {!list.length&&spec.empty&&<p className="rows-empty">{spec.empty}</p>}
  <Button type="button" className="ghost sm" icon="plus" onClick={()=>commit([...list,blank()])}>{spec.add}</Button>
 </div>;
}

// Pairs short fields two per row; a short field without a partner takes the whole row.
function layout(fields:Field[]){const spans:boolean[]=[];for(let i=0;i<fields.length;i++){const f=fields[i],half=(x?:Field)=>!!x&&!x.full&&x.type!=='section'&&(x.half||SHORT.has(x.type||'text')||(!x.type||x.type==='text'||x.type==='url'||x.type==='email'));if(spans[i]!==undefined)continue;if(half(f)&&half(fields[i+1])){spans[i]=true;spans[i+1]=true;}else spans[i]=false;}return spans;}

export function FormDialog({modal,close,submit,busy,error}:{modal:Modal;close:()=>void;submit:(v:any)=>void;busy:boolean;error:string}){
 const base=useId();
 const [v,setV]=useState<any>(()=>Object.fromEntries(modal.fields.filter(f=>f.type!=='section').map(f=>[f.key,initial(f)])));
 const set=(key:string,value:any)=>setV((old:any)=>({...old,[key]:value}));
 const visible=modal.fields.filter(f=>!f.showIf||f.showIf(v));
 const spans=layout(visible);
 const send=(e:React.FormEvent)=>{e.preventDefault();const out:any={...v};for(const f of modal.fields){if((f.type==='number'||f.type==='money')&&f.key in out)out[f.key]=out[f.key]===''?0:Number(out[f.key]);}submit(out);};
 const control=(f:Field,id:string,hintId?:string)=>{
  const common={id,'aria-describedby':hintId};
  switch(f.type){
   case 'select':return <select {...common} required={f.required} value={v[f.key]} onChange={e=>set(f.key,e.target.value)}>{f.options?.map(o=><option key={o.value} value={o.value}>{o.label}</option>)}</select>;
   case 'textarea':return <textarea {...common} value={v[f.key]} required={f.required} placeholder={f.placeholder} onChange={e=>set(f.key,e.target.value)}/>;
   case 'file':return <input {...common} type="file" required={f.required} accept=".exe" onChange={e=>set(f.key,e.target.files?.[0])}/>;
   case 'number':case 'money':{const input=<input {...common} aria-label={f.type==='money'?f.label:undefined} type="number" inputMode="decimal" value={v[f.key]} required={f.required||f.type==='number'} min={f.min} max={f.max} step={f.step||(f.type==='money'?'0.01':undefined)} placeholder={f.placeholder||'0'} autoComplete="off" onFocus={e=>e.target.select()} onChange={e=>set(f.key,e.target.value)}/>;
    return f.type==='money'?<span className="affix"><span aria-hidden="true">$</span>{input}<span aria-hidden="true" className="affix-end">MXN</span></span>:input;}
   default:return <input {...common} type={f.type||'text'} value={v[f.key]} required={f.required} min={f.min} max={f.max} step={f.step} placeholder={f.placeholder} autoComplete="off" onChange={e=>set(f.key,e.target.value)}/>;
  }
 };
 return <DialogFrame label={modal.title} close={close} busy={busy} className={(modal.danger?'danger-dialog ':'')+(modal.fields.length>8?'wide-dialog':'')}>
  <div className="dialog-head"><div className="ph">{modal.danger&&<Sq icon="alert"/>}<div><h2>{modal.title}</h2>{modal.description&&<p>{modal.description}</p>}</div></div><Button className="ghost dialog-close" icon="close" onClick={close} disabled={busy} aria-label="Cerrar diálogo">{''}</Button></div>
  {error&&<div className="alert error" role="alert">{error}</div>}
  <form onSubmit={send}>{visible.length>0&&<div className="form-fields">{visible.map((f,i)=>{
   const id=base+f.key,hintId=f.hint?id+'-hint':undefined,hint=f.hint&&<small id={hintId} className="field-hint" aria-hidden="true">{f.hint}</small>,cls=spans[i]?'':'full';
   if(f.type==='section')return <div className="form-section full" key={f.key}><h3>{f.label}</h3>{f.hint&&<p>{f.hint}</p>}</div>;
   if(f.type==='checkbox')return <label className="toggle-row full" key={f.key}><span className="toggle-text"><span>{f.label}</span>{f.hint&&<small aria-hidden="true">{f.hint}</small>}</span><input type="checkbox" role="switch" checked={v[f.key]} onChange={e=>set(f.key,e.target.checked)}/></label>;
   if(f.type==='multi')return <fieldset className="field full choice-field" key={f.key}><legend>{f.label}</legend><div className="choice-chips">{f.options?.map(o=><label className={'choice-chip'+(v[f.key]?.includes(o.value)?' on':'')} key={o.value}><input type="checkbox" checked={v[f.key]?.includes(o.value)||false} onChange={e=>set(f.key,e.target.checked?[...(v[f.key]||[]),o.value]:(v[f.key]||[]).filter((x:string)=>x!==o.value))}/>{o.label}</label>)}</div>{!f.options?.length&&<small className="field-hint">No hay opciones disponibles.</small>}{hint}</fieldset>;
   if(f.type==='tags')return <div className="field full" key={f.key}><label htmlFor={id}>{f.label}</label><Tags id={id} label={f.label} value={v[f.key]} onChange={x=>set(f.key,x)} suggestions={f.suggestions} placeholder={f.placeholder}/>{hint}</div>;
   if(f.type==='groups')return <div className="field full" key={f.key} role="group" aria-label={f.label}><span className="field-label">{f.label}</span><Groups value={v[f.key]} onChange={x=>set(f.key,x)}/>{hint}</div>;
   if(f.type==='rows')return <div className="field full" key={f.key} role="group" aria-label={f.label}><span className="field-label">{f.label}</span><Rows field={f} value={v[f.key]} onChange={x=>set(f.key,x)}/>{hint}</div>;
   return <label className={'field '+cls} key={f.key}>{f.type==='money'?f.label.replace(/\s*MXN/,''):f.label}{control(f,id,hintId)}{hint}</label>;
  })}</div>}
  <div className="dialog-actions"><Button type="button" className="ghost" icon={false} onClick={close} disabled={busy}>Volver</Button><Button className={modal.danger?'danger':'primary'} icon={modal.danger?'alert':'check'} disabled={busy}>{busy?'Guardando…':modal.submit}</Button></div></form>
 </DialogFrame>;
}
