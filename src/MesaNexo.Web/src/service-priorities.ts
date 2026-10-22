type Entity = {id:string;[key:string]:any};
export type ServicePriority = {id:string;visitId:string;kind:'ready'|'delayed'|'unavailable'|'bill'|'attention';label:string;detail:string;count:number;minutes:number};
const minutesSince = (at:string, now:number) => Number.isFinite(Date.parse(at)) ? Math.max(0,Math.floor((now-Date.parse(at))/60000)) : 0;

export function servicePriorities(visits:Entity[],units:Entity[],notifications:Entity[],delayMinutes:number,now=Date.now()):ServicePriority[] {
  const result:ServicePriority[]=[];
  for(const visit of visits.filter(v=>v.status==='open')) {
    const current=units.filter(unit=>unit.visitId===visit.id);
    const ready=current.filter(unit=>unit.status==='ready');
    const preparing=current.filter(unit=>['received','preparing'].includes(unit.status));
    const elapsed=preparing.map(unit=>{
      const receipts=(unit.history||[]).filter((event:any)=>event.status==='received');
      return minutesSince(receipts.at(-1)?.at || unit.receivedAt,now);
    });
    const longest=Math.max(0,...elapsed);
    if(ready.length) {
      const waiting=Math.max(0,...ready.map(unit=>minutesSince(unit.readyAt,now)));
      result.push({id:visit.id+':ready',visitId:visit.id,kind:'ready',label:'Recoger',detail:ready.length+' producto'+(ready.length===1?' listo':'s listos')+' · Hace '+waiting+' min',count:ready.length,minutes:waiting});
    }
    if(preparing.length && longest>=delayMinutes) result.push({id:visit.id+':delayed',visitId:visit.id,kind:'delayed',label:'Revisar retraso',detail:longest+' min desde recepción · '+preparing.length+' en preparación',count:preparing.length,minutes:longest});
    const pending=notifications.filter(n=>n.visitId===visit.id&&!n.attended);
    const unavailable=pending.filter(n=>n.kind==='unavailable'&&current.some(unit=>unit.id===n.unitId&&unit.status==='unavailable'));
    if(unavailable.length) result.push({id:visit.id+':unavailable',visitId:visit.id,kind:'unavailable',label:'No disponible',detail:unavailable[0].message,count:unavailable.length,minutes:0});
    // A balance alone never means the customer has requested the bill.
    const customer=pending.filter(n=>n.kind==='customer');
    if(customer.some(n=>/cuenta/i.test(n.message))) result.push({id:visit.id+':bill',visitId:visit.id,kind:'bill',label:'Cuenta solicitada',detail:'Saldo pendiente de pago',count:1,minutes:0});
    if(customer.some(n=>!/cuenta/i.test(n.message))) result.push({id:visit.id+':attention',visitId:visit.id,kind:'attention',label:'Solicita atención',detail:'Solicitud de la mesa pendiente de atender',count:1,minutes:0});
  }
  const order={ready:0,unavailable:1,delayed:2,bill:3,attention:4};
  return result.sort((a,b)=>order[a.kind]-order[b.kind]||b.minutes-a.minutes||a.id.localeCompare(b.id));
}
