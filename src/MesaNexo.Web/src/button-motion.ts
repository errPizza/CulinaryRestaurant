// A click observer only: animation never submits or repeats a business action.
export function installButtonMotion(){
 const animations=new Map<HTMLElement,Animation>();
 const click=(event:MouseEvent)=>{const button=(event.target as Element)?.closest<HTMLElement>('button');if(!button||button.matches(':disabled')||window.matchMedia('(prefers-reduced-motion: reduce)').matches||document.documentElement.dataset.reducedMotion==='true')return;
 animations.get(button)?.cancel();const animation=button.animate([{scale:'.965'},{scale:'1.06',offset:.35},{scale:window.matchMedia('(hover:hover)').matches&&button.matches(':hover')?'1.025':'1'}],{duration:260,easing:'cubic-bezier(.22,1,.36,1)'});animations.set(button,animation);animation.onfinish=()=>animations.delete(button);
 };
 document.addEventListener('click',click,true);return()=>{document.removeEventListener('click',click,true);for(const a of animations.values())a.cancel();};
}
