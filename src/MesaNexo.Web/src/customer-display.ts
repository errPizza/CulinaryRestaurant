import {invoke} from '@tauri-apps/api/core';

// The operation window publishes what the customer should see; the display window only renders it.
export type DisplayLine={quantity:number;name:string;detail?:string;amount:number};
export type DisplayState={restaurant:string;banners:string[];order?:{title:string;label?:string;lines:DisplayLine[];total:number;tendered?:number;change?:number;paid?:boolean}};
const CHANNEL='culinary-customer-display';
let last:DisplayState|null=null,channel:BroadcastChannel|null=null;
function open(){if(!channel&&'BroadcastChannel' in window){channel=new BroadcastChannel(CHANNEL);channel.onmessage=e=>{if(e.data?.type==='hello'&&last)channel?.postMessage({type:'state',state:last});};}return channel;}
export function publishDisplay(state:DisplayState){last=state;open()?.postMessage({type:'state',state});}
export function listenDisplay(onState:(s:DisplayState)=>void){const c=new BroadcastChannel(CHANNEL);c.onmessage=e=>{if(e.data?.type==='state')onState(e.data.state);};c.postMessage({type:'hello'});return()=>c.close();}
export async function openCustomerDisplay(){
  open();
  if('__TAURI_INTERNALS__' in window){await invoke('open_customer_display');return;}
  window.open('./cliente.html','culinary-cliente','popup,width=1024,height=700');
}
