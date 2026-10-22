import {native} from './bridge';

// Presentation never waits for the vault, network or sign-in. Secret Service may
// need to display an unlock dialog, which must not be hidden by the loading screen.
export function startKioskPresentation(){
 let disposed=false,heartbeat:ReturnType<typeof setInterval>|undefined;
 const frame=()=>new Promise<void>(resolve=>requestAnimationFrame(()=>resolve()));
 const presented=(async()=>{
  await document.fonts.ready;
  if(disposed)return;
  const result=await native('window.present');
  if(!result.enabled||disposed)return;
  await frame();await frame();
  await new Promise(resolve=>setTimeout(resolve,180));
  if(disposed)return;
  await native('window.ready');
  if(!disposed)heartbeat=setInterval(()=>{
   // A stalled renderer stops sending heartbeats; the appliance session recovers it.
   void native('window.ready').catch(()=>{});
  },10000);
 })().catch(()=>{ /* The appliance supervisor retains the loading/error screen. */ });
 return{presented,dispose:()=>{disposed=true;if(heartbeat)clearInterval(heartbeat);}};
}
