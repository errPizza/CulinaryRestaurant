import {invoke} from '@tauri-apps/api/core';
export async function native<T=any>(command:string,args:Record<string,any>={}):Promise<T>{
 if(!('__TAURI_INTERNALS__' in window))throw new Error('Abre Culinary Restaurant desde la aplicación de escritorio para usar la conexión segura.');
 const response=await invoke<any>('native_call',{command,args});
 if(!response.ok)throw Object.assign(new Error(response.error||'Operación rechazada'),{status:response.status});
 return response.data;
}
export function normalize(source:any){
 const s=structuredClone(source),r=s.restaurant||{},branch=r.branchId||s.siteId||'local';
 r.settings={color:'#f2b93b',logo:'',contact:'',delayMinutes:15,discountLimit:20,stations:['Cocina'],...r,...r.settings};r.modules||=[];
 s.categories=s.categorys||s.categories||[];s.cash=s.cashs||s.cash||[];s.audit=s.audits||s.audit||[];s.installations=s.devices||s.installations||[];
 s.branches ||= [{id:branch,name:r.branchName||r.name||'Sucursal local'}];
 for(const key of ['tables','units','visits','cash'])s[key]=(s[key]||[]).map((x:any)=>({...x,branchId:x.branchId||branch}));
 for(const key of ['requests','movements','notifications'])s[key]=(s[key]||[]).map((x:any)=>({...x,kind:x.type||x.kind}));
 s.audit=s.audit.map((x:any)=>({...x,type:x.operation,actorName:x.actor,details:JSON.stringify(x.changes||{},null,2)}));
 s.user={...s.user,permissions:{payments:true,shiftReports:true,quickSale:true,mergeTables:true,transfer:true,...s.user?.permissions}};
 return s;
}
// Only compatibility with the prototype's field names. Server permissions remain authoritative.
export function payloadFor(type:string,payload:any){
 const p={...payload};delete p.key;
 if(['request.create','customer.simulate','cash.movement'].includes(type)&&p.kind){p.type=p.kind;delete p.kind;}
 return p;
}
export function cents(value:unknown){
 const text=String(value??'').trim().replace(',','.');
 if(!/^\d+(?:\.\d{1,2})?$/.test(text))throw new Error('Usa un importe positivo con hasta dos decimales');
 const [whole,fraction='']=text.split('.');const result=Number(whole)*100+Number(fraction.padEnd(2,'0'));
 if(!Number.isSafeInteger(result))throw new Error('Importe fuera de rango');return result;
}
