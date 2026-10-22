import {test,expect,Page} from '@playwright/test';
import path from 'node:path';
import fs from 'node:fs';
// Setup mode UI with a simulated native bridge. The backend contract is covered by MesaNexo.Tests --setup-only.
const evidence=path.resolve('../../evidence');
async function mock(page:Page,complete:any){
 const calls:any[]=[];
 await page.exposeBinding('fixtureNative',async(_,command,args)=>{calls.push({command,args});
  const data:any={'profile.get':{ServerUrl:'https://culinaryos.internal:8443',SiteId:'site-setup'},'motion.get':{reduced:true},status:{authenticated:false,pending:false},'setup.status':{needsSetup:true,codeAvailable:true},'setup.complete':complete,state:{restaurant:{name:'La Cocina de Prueba',modules:[],settings:{stations:['Cocina']}},user:{id:'u1',name:'Gerencia',role:'manager'},tables:[],visits:[],units:[],commands:[]}}[command];
  return data===undefined?{ok:false,error:'Comando no simulado: '+command}:{ok:true,data};});
 await page.addInitScript(()=>{(window as any).__TAURI_INTERNALS__={invoke:async(_cmd:string,payload:any)=>(window as any).fixtureNative(payload.command,payload.args)};});
 await page.goto('/');return calls;
}
async function fill(page:Page){
 await page.getByRole('button',{name:'Comenzar'}).click();
 await page.getByLabel('Nombre del restaurante').fill('La Cocina de Prueba');await page.getByRole('button',{name:'Continuar'}).click();
 await page.getByLabel('Nombre completo').fill('Gerencia Setup');await page.getByLabel('Usuario',{exact:true}).fill('gerente');
 await page.getByLabel('Contraseña',{exact:true}).fill('Contrasena-Segura-1');await page.getByLabel('Repite la contraseña').fill('Contrasena-Distinta');
 await page.getByRole('button',{name:'Continuar'}).click();await expect(page.getByRole('alert')).toContainText('no coinciden');
 await page.getByLabel('Repite la contraseña').fill('Contrasena-Segura-1');await page.getByRole('button',{name:'Continuar'}).click();
 await expect(page.getByRole('heading',{name:'Confirma y crea'})).toBeVisible();
}
test('setup mode creates the restaurant and enters the operation',async({page})=>{
 fs.mkdirSync(evidence,{recursive:true});const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));
 const calls=await mock(page,{user:{id:'u1',name:'Gerencia',role:'manager'},setup:true});
 await expect(page.getByRole('heading',{name:'Configura tu restaurante'})).toBeVisible();await expect(page.getByRole('heading',{name:'Inicia sesión'})).toHaveCount(0);
 await page.waitForTimeout(650);await page.screenshot({path:path.join(evidence,'setup-welcome-1360.png')});
 await fill(page);await expect(page.getByLabel('Código de configuración del equipo')).toHaveCount(0);
 await page.waitForTimeout(650);await page.screenshot({path:path.join(evidence,'setup-confirm-1360.png')});
 await page.getByRole('button',{name:'Crear restaurante y entrar'}).click();
 await expect(page.locator('main h1')).toBeVisible();
 const sent=calls.find(c=>c.command==='setup.complete').args;
 expect(sent).toMatchObject({name:'La Cocina de Prueba',managerName:'Gerencia Setup',managerUsername:'gerente',managerPassword:'Contrasena-Segura-1',managerPasswordConfirm:'Contrasena-Segura-1'});
 expect(calls.some(c=>c.command==='login')).toBe(false);
 await page.setViewportSize({width:390,height:844});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
 expect(errors).toEqual([]);
});
test('setup keeps the one-time code visible when automatic sign-in fails',async({page})=>{
 await mock(page,{setup:true,signedIn:false,username:'gerente',enrollmentCode:'codigo-de-alta-unico-de-prueba-1234'});
 await fill(page);await page.getByRole('button',{name:'Crear restaurante y entrar'}).click();
 await expect(page.getByRole('heading',{name:'Guarda tu código de alta'})).toBeVisible();await expect(page.getByLabel('Código de alta del dispositivo')).toHaveText('codigo-de-alta-unico-de-prueba-1234');
 await page.waitForTimeout(650);await page.screenshot({path:path.join(evidence,'setup-code-1360.png')});
 await page.getByRole('button',{name:'Ir al inicio de sesión'}).click();
 await expect(page.getByRole('heading',{name:'Inicia sesión'})).toBeVisible();await expect(page.getByLabel('Usuario',{exact:true})).toHaveValue('gerente');
 await expect(page.getByLabel('Código de alta del dispositivo (sólo primer acceso)')).toHaveValue('codigo-de-alta-unico-de-prueba-1234');
});
test('setup on mobile width stays inside the viewport',async({page})=>{
 await page.setViewportSize({width:390,height:844});await mock(page,{});
 await page.getByRole('button',{name:'Comenzar'}).click();await expect(page.getByLabel('Nombre del restaurante')).toBeVisible();
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
 await page.waitForTimeout(650);await page.screenshot({path:path.join(evidence,'setup-restaurant-390.png'),fullPage:true});
});
