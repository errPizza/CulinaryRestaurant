import {test,expect,Page} from '@playwright/test';
import {spawn,ChildProcessWithoutNullStreams} from 'node:child_process';
import {createInterface} from 'node:readline';
import path from 'node:path';
import fs from 'node:fs';
const root=path.resolve('../..'),evidence=path.join(root,'evidence');
let process:ChildProcessWithoutNullStreams,ready:any,sequence=0;
const pending=new Map<string,{resolve:(x:any)=>void,reject:(e:any)=>void}>();
function call(client:string,command:string,args:any={}){return new Promise<any>((resolve,reject)=>{const id=String(++sequence);pending.set(id,{resolve,reject});process.stdin.write(JSON.stringify({id,client,command,args})+'\n');});}
test.beforeAll(async()=>{fs.mkdirSync(evidence,{recursive:true});const started=new Promise<any>((resolve,reject)=>{
 process=spawn('dotnet',[path.join(root,'tests/MesaNexo.Hybrid.Tests/bin/Release/net10.0/MesaNexo.Hybrid.Tests.dll'),root,'C:/Users/kikin/Desktop/CulinaryOS/tools/python/python.exe','--serve'],{cwd:root,windowsHide:true});
 process.once('error',reject);process.stderr.on('data',data=>console.error(String(data)));
 createInterface({input:process.stdout}).on('line',line=>{let response;try{response=JSON.parse(line);}catch{return;}if(response.ready){resolve(response);return;}const task=pending.get(response.id);if(task){pending.delete(response.id);task.resolve(response);}});
 process.once('exit',code=>{for(const task of pending.values())task.reject(new Error('Fixture exited '+code));pending.clear();if(code)reject(new Error('Fixture exited '+code));});
 });ready=await started;});
test.afterAll(async()=>{process?.stdin.end();if(process&&!process.killed)await new Promise<void>(resolve=>{process.once('exit',()=>resolve());setTimeout(()=>{process.kill();resolve();},10000);});});
async function attach(page:Page,client:string,dropType='',missingProfile=false){let dropped=false;const calls:string[]=[];await page.exposeBinding('fixtureNative',async(_,command,args)=>{calls.push(command);if(missingProfile&&command==='profile.get')return {ok:true,data:null};const response=await call(client,command,args);if(!dropped&&dropType&&command==='execute'&&args.type===dropType&&response.ok){dropped=true;return {ok:false,error:'Respuesta IPC perdida en prueba',status:0};}return response;});await page.addInitScript(()=>{(window as any).__TAURI_INTERNALS__={invoke:async(_cmd:string,payload:any)=>(window as any).fixtureNative(payload.command,payload.args)};});await page.goto('/');return calls;}
async function login(page:Page,client:string,user='gerencia',dropType=''){await attach(page,client,dropType);await page.getByLabel('Usuario',{exact:true}).fill(user);await page.getByLabel('Contraseña',{exact:true}).fill(ready.password);await page.getByLabel('Código de alta del dispositivo (sólo primer acceso)').fill(ready.enrollmentCode);await page.getByRole('button',{name:'Entrar a la operación'}).click();await expect(page.locator('main h1')).toBeVisible();}
// Inventory configuration lives in the page's "Configurar" menu.
async function inventoryMenu(page:Page,name:string){await page.getByRole('button',{name:'Configurar',exact:true}).click();await page.getByRole('button',{name,exact:true}).click();}
async function nav(page:Page,name:string){const nav=page.getByRole('navigation',{name:'Navegación principal'});let target=nav.getByRole('button',{name,exact:true,includeHidden:true});if(!await target.isVisible()){await target.locator('xpath=ancestor::details').locator('summary').click();}await target.click();await expect(page.locator('main h1')).toHaveText(name);}
test('original CSS, supplied logos, responsive layout and click pop',async({page})=>{
 const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));await attach(page,'visual');
 await expect(page.getByRole('heading',{name:'Inicia sesión'})).toBeVisible();await expect(page.locator('.brand-wordmark')).toHaveCount(0);await expect(page.locator('.login-art img')).toHaveCount(0);await expect(page.locator('.brand-symbol')).toBeVisible();
 await expect(page.locator('.login-story')).toHaveCSS('background-image',/51, 66, 88/);
 const button=page.getByRole('button',{name:'Entrar a la operación'});await expect(button).toHaveCSS('border-radius',/\d/);
 await page.waitForTimeout(650);await page.screenshot({path:path.join(evidence,'hybrid-login-1360.png'),fullPage:true});
 const config=page.getByRole('button',{name:'Configurar dirección del backend'});await config.hover();await expect.poll(()=>config.evaluate(e=>getComputedStyle(e).scale)).not.toBe('1');
 await config.click();expect(await config.evaluate(e=>e.getAnimations().length)).toBeGreaterThan(0);await expect(page.getByRole('dialog')).toBeVisible();await page.keyboard.press('Escape');await expect(page.getByRole('dialog')).toHaveCount(0);
 await page.setViewportSize({width:390,height:844});await page.waitForTimeout(650);await page.screenshot({path:path.join(evidence,'hybrid-login-390.png'),fullPage:true});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect(errors).toEqual([]);
});
test('real HTTPS POS: open table, protected draft, kitchen, delivery, cash and close',async({page})=>{
 await login(page,'pos');await nav(page,'Mesas y cuentas');await page.getByRole('article').filter({has:page.getByRole('heading',{name:'Mesa 01',exact:true})}).getByRole('button',{name:'Abrir mesa'}).click();
 const open=page.getByRole('dialog');await open.getByLabel('Mesero responsable').selectOption({label:'Servicio'});await open.getByRole('button',{name:'Guardar',exact:true}).click();await expect(page.getByRole('dialog',{name:/Cuenta de/})).toBeVisible();
 await page.getByRole('button',{name:'Agregar productos',exact:true}).click();await expect(page.getByRole('dialog',{name:'Agregar productos al borrador'})).toBeVisible();
 await page.getByLabel('Notas para cocina').fill('Sin cebolla');await page.getByRole('button',{name:'Agregar al borrador',exact:true}).click();await page.getByRole('button',{name:'Confirmar y enviar a cocina →'}).click();await page.getByRole('dialog',{name:'Enviar comanda a cocina'}).getByRole('button',{name:'Confirmar',exact:true}).click();
 await expect(page.locator('.unit-row')).toHaveCount(1);const saved=await call('pos','draft.load',{visitId:(await call('pos','state')).data.visits.find((v:any)=>v.status==='open').id});expect(saved.data).toEqual([]);
 await page.waitForTimeout(650);await page.screenshot({path:path.join(evidence,'hybrid-account-1360.png'),fullPage:true});await page.getByRole('button',{name:'Cerrar detalle',exact:true}).click();await nav(page,'Cocina en vivo');
 await page.getByRole('button',{name:'Iniciar preparación',exact:true}).click();await page.getByRole('button',{name:'Listo para recoger',exact:true}).click();await page.waitForTimeout(650);await page.screenshot({path:path.join(evidence,'hybrid-kitchen-1360.png'),fullPage:true});
 await nav(page,'Mesas y cuentas');await page.locator('[data-open-account]').first().click();await page.getByRole('button',{name:'Confirmar entrega',exact:true}).click();await page.getByRole('button',{name:'Registrar pago',exact:true}).click();
 const payment=page.getByRole('dialog',{name:'Registrar pago'});await payment.getByLabel('Efectivo recibido MXN (sólo efectivo)').fill('500');await payment.getByRole('button',{name:'Guardar',exact:true}).click();await expect(page.getByRole('button',{name:'Registrar pago',exact:true})).toBeDisabled();
 await page.getByRole('button',{name:'Cerrar cuenta',exact:true}).click();await page.getByRole('dialog',{name:'Cerrar cuenta'}).getByRole('button',{name:'Confirmar',exact:true}).click();
 const state=(await call('pos','state')).data;expect(state.visits.find((v:any)=>v.tableId===state.tables.find((t:any)=>t.name==='Mesa 01').id).status).toBe('closed');expect(state.payments.at(-1).amount).toBe(18500);expect(state.payments.at(-1).change).toBe(31500);
 await page.reload();await expect(page.locator('main h1')).toHaveText('Resumen del turno');expect(await page.evaluate(()=>({local:Object.keys(localStorage),session:Object.keys(sessionStorage)}))).toEqual({local:[],session:[]});
});
test('all migrated administration pages load against current state',async({page})=>{
 const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));await login(page,'modules');for(const name of ['Catálogo','Caja','Inventario','Delivery y clientes','Impresión','Fiscal','Reportes','Equipo y dispositivos']){await nav(page,name);await expect(page.locator('main .page-content')).toBeVisible();}
 await nav(page,'Inventario');await inventoryMenu(page,'Insumo');const dialog=page.getByRole('dialog',{name:'Insumo',exact:true});await dialog.getByLabel('Nombre',{exact:true}).fill('Harina UI');await dialog.getByRole('button',{name:'Guardar',exact:true}).click();expect((await call('modules','state')).data.ingredients.some((i:any)=>i.name==='Harina UI')).toBe(true);
 await page.setViewportSize({width:390,height:844});await page.waitForTimeout(650);await page.screenshot({path:path.join(evidence,'hybrid-inventory-390.png'),fullPage:true});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);expect(errors).toEqual([]);
});
test('cashier visibility, reduced motion and no browser token persistence',async({page})=>{
 await login(page,'cashier','caja');await expect(page.locator('main h1')).toHaveText('Arqueo ciego');await expect(page.getByRole('navigation').getByRole('button',{name:'Catálogo'})).toHaveCount(0);
 const direct=await call('cashier','execute',{type:'catalog.category',payload:{name:'Forbidden'}});expect(direct.ok).toBe(false);expect(direct.status).toBe(403);
 await page.emulateMedia({reducedMotion:'reduce'});const button=page.getByRole('button',{name:/^Menú de/});await button.click();expect(await button.evaluate(e=>e.getAnimations().length)).toBe(0);await page.locator('.version-button').click();await expect(page.getByRole('dialog',{name:'Culinary Restaurant 0.3.0'})).toBeVisible();await page.keyboard.press('Escape');expect(await page.evaluate(()=>Object.keys(sessionStorage))).toEqual([]);
});

test('lost IPC receipt reconciles the same command without duplicating kitchen units',async({page})=>{
 await login(page,'lost-ipc','gerencia','command.send');await nav(page,'Mesas y cuentas');await page.getByRole('article').filter({has:page.getByRole('heading',{name:'Mesa 02',exact:true})}).getByRole('button',{name:'Abrir mesa'}).click();
 const open=page.getByRole('dialog');await open.getByLabel('Mesero responsable').selectOption({label:'Servicio'});await open.getByRole('button',{name:'Guardar',exact:true}).click();await page.getByRole('button',{name:'Agregar productos',exact:true}).click();await page.getByRole('button',{name:'Agregar al borrador',exact:true}).click();await page.getByRole('button',{name:'Confirmar y enviar a cocina →'}).click();await page.getByRole('dialog',{name:'Enviar comanda a cocina'}).getByRole('button',{name:'Confirmar',exact:true}).click();
 await expect(page.getByRole('dialog',{name:'Enviar comanda a cocina'}).getByText('Respuesta IPC perdida en prueba',{exact:true})).toBeVisible();const before=(await call('lost-ipc','state')).data,visit=before.visits.find((v:any)=>v.tableId===before.tables.find((t:any)=>t.name==='Mesa 02').id);expect(before.units.filter((u:any)=>u.visitId===visit.id)).toHaveLength(1);expect((await call('lost-ipc','status')).data.pending).toBe(true);
 // Reconciliation lives behind the drawer, so dismiss the failed confirmation and account.
 await page.keyboard.press('Escape');await page.getByRole('button',{name:'Cerrar detalle',exact:true}).click();await page.getByRole('button',{name:'Reconciliar operación',exact:true}).click();await expect(page.getByRole('button',{name:'Reconciliar operación',exact:true})).toHaveCount(0);
 expect((await call('lost-ipc','state')).data.units.filter((u:any)=>u.visitId===visit.id)).toHaveLength(1);expect((await call('lost-ipc','status')).data.pending).toBe(false);expect((await call('lost-ipc','draft.load',{visitId:visit.id})).data).toEqual([]);
});
test('inventory forms preserve exact quantities, recipe versions and sale policy',async({page})=>{
 await login(page,'stock-ui');await nav(page,'Inventario');await inventoryMenu(page,'Almacén');let dialog=page.getByRole('dialog',{name:'Almacén',exact:true});await dialog.getByLabel('Nombre',{exact:true}).fill('Principal UI');await dialog.getByRole('button',{name:'Guardar',exact:true}).click();
 await inventoryMenu(page,'Insumo');dialog=page.getByRole('dialog',{name:'Insumo',exact:true});await expect(dialog.getByRole('combobox',{name:'Unidad base',exact:true})).toHaveValue('g');await dialog.getByLabel('Nombre',{exact:true}).fill('Carne UI');await dialog.getByRole('button',{name:'Guardar',exact:true}).click();
 await inventoryMenu(page,'Receta o nueva versión');dialog=page.getByRole('dialog',{name:'Receta · nueva versión histórica',exact:true});await dialog.getByLabel('Nombre',{exact:true}).fill('Receta UI');await dialog.getByRole('button',{name:'Agregar insumo',exact:true}).click();await dialog.getByRole('combobox',{name:'Insumo 1',exact:true}).selectOption({label:'Carne UI'});await dialog.getByLabel('Cantidad 1',{exact:true}).fill('150');await dialog.getByRole('combobox',{name:'Unidad 1',exact:true}).selectOption('g');await dialog.getByRole('button',{name:'Guardar',exact:true}).click();
 await inventoryMenu(page,'Vincular receta');dialog=page.getByRole('dialog');await dialog.getByRole('combobox',{name:'Producto',exact:true}).selectOption({label:'Hamburguesa de la casa'});await dialog.getByRole('combobox',{name:'Receta',exact:true}).selectOption({label:'Receta UI'});await dialog.getByRole('button',{name:'Guardar',exact:true}).click();
 await page.getByRole('button',{name:'Entrada',exact:true}).click();dialog=page.getByRole('dialog',{name:'Entrada con costo total',exact:true});await dialog.getByRole('combobox',{name:'Almacén',exact:true}).selectOption({label:'Principal UI'});await dialog.getByRole('combobox',{name:'Insumo',exact:true}).selectOption({label:'Carne UI'});await dialog.getByLabel('Cantidad',{exact:true}).fill('1.25');await dialog.getByRole('combobox',{name:'Unidad',exact:true}).selectOption('kg');await dialog.getByLabel('Costo total MXN',{exact:true}).fill('250.50');await dialog.getByLabel('Referencia',{exact:true}).fill('INV-UI');await dialog.getByRole('button',{name:'Guardar',exact:true}).click();
 await inventoryMenu(page,'Política de inventario');dialog=page.getByRole('dialog');await dialog.getByRole('combobox',{name:'Almacén de venta',exact:true}).selectOption({label:'Principal UI'});await dialog.getByRole('button',{name:'Guardar',exact:true}).click();
 const state=(await call('stock-ui','state')).data,ingredient=state.ingredients.find((i:any)=>i.name==='Carne UI'),stock=state.stocks.find((x:any)=>x.ingredientId===ingredient.id);expect(stock.quantity).toBe(1250000);expect(stock.value).toBe(25050);expect(state.recipes.find((r:any)=>r.name==='Receta UI').version).toBe(1);expect(state.restaurant.inventoryPolicy).toBe('reserve-on-send-consume-on-close');
});


test('login setup, independent secret toggles and rejected credentials recover to a real session',async({page})=>{
 const calls=await attach(page,'login-setup','',true);
 await expect(page.getByRole('status')).toHaveText('Configura la dirección del backend antes de iniciar sesión.');
 const enter=page.getByRole('button',{name:'Entrar a la operación'});
 await expect(enter).toBeEnabled();
 await page.getByLabel('Usuario',{exact:true}).fill('gerencia');
 const password=page.getByLabel('Contraseña',{exact:true}),code=page.getByLabel('Código de alta del dispositivo (sólo primer acceso)');
 await password.fill('Incorrecta-de-prueba!');await code.fill(ready.enrollmentCode);
 await expect(password).toHaveAttribute('type','password');await expect(code).toHaveAttribute('type','password');
 await page.getByRole('button',{name:'Mostrar contraseña',exact:true}).click();
 await expect(password).toHaveAttribute('type','text');await expect(password).toHaveValue('Incorrecta-de-prueba!');await expect(code).toHaveAttribute('type','password');
 await page.getByRole('button',{name:'Mostrar código de alta',exact:true}).click();await expect(code).toHaveAttribute('type','text');
 await page.getByRole('button',{name:'Ocultar contraseña',exact:true}).click();await page.getByRole('button',{name:'Ocultar código de alta',exact:true}).click();
 await expect(password).toHaveAttribute('type','password');await expect(code).toHaveAttribute('type','password');
 expect(calls.filter(command=>command==='login')).toHaveLength(0);
 await enter.click();const dialog=page.getByRole('dialog',{name:'Conexión segura al backend'});await expect(dialog).toBeVisible();
 expect(calls.filter(command=>command==='login')).toHaveLength(0);
 for(const [label,key] of [['Dirección HTTPS','ServerUrl'],['Identidad del sitio','SiteId'],['Archivo de CA (.crt)','CaFile'],['Huella SHA-256 de la CA','CaSha256']])await dialog.getByLabel(label,{exact:true}).fill(ready.profile[key]);
 await dialog.getByRole('button',{name:'Guardar conexión',exact:true}).click();await expect(dialog).toHaveCount(0);await expect(page.getByRole('status')).toHaveCount(0);
 await enter.click();await expect(page.getByRole('alert').filter({hasText:'Usuario o contraseña incorrectos'})).toBeVisible();await expect(enter).toBeEnabled();
 await password.fill(ready.password);await enter.click();await expect(page.locator('main h1')).toHaveText('Resumen del turno');
 expect(calls.filter(command=>command==='login')).toHaveLength(2);
 expect(await page.evaluate(()=>({local:Object.keys(localStorage),session:Object.keys(sessionStorage)}))).toEqual({local:[],session:[]});
});


test('support stays visible and saves requests, replies and revocable consent',async({page})=>{
 const errors:string[]=[];page.on('pageerror',error=>errors.push(error.message));await login(page,'support-ui');
 const menu=page.getByRole('navigation',{name:'Navegación principal'});
 await menu.locator('summary').filter({hasText:'Restaurante'}).click();await menu.getByRole('button',{name:'Soporte',exact:true}).click();
 await expect(page.locator('main h1')).toHaveText('Soporte');
 await expect(page.getByText('Cobertura hasta', {exact:false})).toBeVisible();
 await expect(page.getByRole('heading',{name:'Sin solicitudes de soporte'})).toBeVisible();
 await page.getByRole('button',{name:'Nueva solicitud',exact:true}).click();let dialog=page.getByRole('dialog',{name:'Crear solicitud'});
 await dialog.getByLabel('Asunto',{exact:true}).fill('Consulta de prueba de soporte');await dialog.getByLabel('Descripción',{exact:true}).fill('El panel debe permanecer visible.');await dialog.getByRole('button',{name:'Guardar',exact:true}).click();
 const ticket=page.locator('article.ticket').filter({hasText:'Consulta de prueba de soporte'});await expect(ticket).toBeVisible();await ticket.getByRole('button',{name:'Responder',exact:true}).click();dialog=page.getByRole('dialog',{name:'Responder solicitud'});
 await dialog.getByLabel('Respuesta',{exact:true}).fill('Respuesta de prueba registrada.');await dialog.getByRole('button',{name:'Guardar',exact:true}).click();await expect(ticket.getByText('Respuesta de prueba registrada.',{exact:true})).toBeVisible();
 await page.getByRole('button',{name:'Autorizar consulta de soporte',exact:true}).click();dialog=page.getByRole('dialog',{name:'Autorizar soporte temporal'});
 await expect(dialog.getByLabel('Vigencia en horas (máximo 24)')).toHaveAttribute('min','1');await dialog.getByLabel('Motivo y consentimiento',{exact:true}).fill('Consulta temporal de prueba, sin acceso al sistema.');await dialog.getByRole('button',{name:'Guardar',exact:true}).click();
 const row=page.getByRole('row').filter({hasText:'Consulta temporal de prueba, sin acceso al sistema.'});await expect(row.getByRole('button',{name:'Revocar',exact:true})).toBeEnabled();await row.getByRole('button',{name:'Revocar',exact:true}).click();
 await page.getByRole('dialog',{name:'Revocar soporte'}).getByRole('button',{name:'Confirmar',exact:true}).click();await expect(row.getByRole('cell',{name:'Revocada',exact:true})).toBeVisible();
 const state=(await call('support-ui','state')).data;expect(state.tickets.find((t:any)=>t.subject==='Consulta de prueba de soporte').messages).toHaveLength(2);expect(state.grants.find((g:any)=>g.reason==='Consulta temporal de prueba, sin acceso al sistema.').revoked).toBe(true);
 await page.evaluate(()=>window.scrollTo(0,0));await page.waitForTimeout(450);await page.screenshot({path:path.join(evidence,'support-fixed-1360.png'),fullPage:true});expect(errors).toEqual([]);
});


test('approved design uses aligned controls, semantic icons and responsive real-data pages',async({page})=>{
 const errors:string[]=[];page.on('pageerror',error=>errors.push(error.message));await login(page,'approved-design');
 await expect(page.locator('.ui-metric')).toHaveCount(4);
 await expect.poll(async()=>page.locator('.ui-metric').evaluateAll(nodes=>{const heights=nodes.map(n=>n.getBoundingClientRect().height);return Math.max(...heights)-Math.min(...heights);})).toBeLessThan(1);
 await expect(page.locator('.ui-metric .ui-icon')).toHaveCount(4);
 await page.evaluate(()=>window.scrollTo(0,0));await page.waitForTimeout(400);await page.screenshot({path:path.join(evidence,'approved-overview-1360.png'),fullPage:true});
 await nav(page,'Mesas y cuentas');await expect(page.locator('.table-card')).toHaveCount(6);
 const filters=page.getByRole('group',{name:'Filtrar mesas'});await filters.getByRole('button',{name:/Libres/}).click();await expect(page.locator('.table-card.occupied')).toHaveCount(0);await filters.getByRole('button',{name:/Todas/}).click();
 await page.getByLabel('Buscar mesa',{exact:true}).fill('no existe');await expect(page.getByText('No hay mesas con este filtro.',{exact:true})).toBeVisible();await page.getByLabel('Buscar mesa',{exact:true}).fill('');
 await page.evaluate(()=>window.scrollTo(0,0));await page.waitForTimeout(400);await page.screenshot({path:path.join(evidence,'approved-tables-1360.png'),fullPage:true});
 await nav(page,'Soporte');await expect(page.locator('.coverage-card')).toHaveCount(2);await expect(page.getByRole('heading',{name:'Consulta temporal',exact:true})).toBeVisible();
 await page.getByRole('group',{name:'Filtrar solicitudes'}).getByRole('button',{name:/Cerradas/}).click();await expect(page.locator('article.ticket')).toHaveCount(0);await page.getByRole('group',{name:'Filtrar solicitudes'}).getByRole('button',{name:/Todas/}).click();
 await page.evaluate(()=>window.scrollTo(0,0));await page.waitForTimeout(400);await page.screenshot({path:path.join(evidence,'approved-support-1360.png'),fullPage:true});
 for(const name of ['Mesas y cuentas','Soporte','Inventario','Equipo y dispositivos']){
  await page.setViewportSize({width:390,height:844});await nav(page,name);await page.evaluate(()=>window.scrollTo(0,0));await page.waitForTimeout(300);
  const overflow=await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth);expect(overflow,name+' must fit mobile width').toBe(false);
  if(name==='Soporte')await page.screenshot({path:path.join(evidence,'approved-support-390.png'),fullPage:true});
  if(name==='Mesas y cuentas')await page.screenshot({path:path.join(evidence,'approved-tables-390.png'),fullPage:true});
 }
 expect(errors).toEqual([]);
});
test('counter sells takeout without a table, pays cash with change and mirrors the customer display',async({page,context})=>{
 const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));await login(page,'counter');const display=await context.newPage();await display.goto('/cliente.html');
 await nav(page,'Mostrador');const product=page.locator('.counter-product').first();const name=(await product.locator('.cp-name').textContent())!;await product.click();
 const options=page.getByRole('dialog');if(await options.isVisible())await options.getByRole('button',{name:'Agregar a la orden',exact:true}).click();
 await page.getByRole('button',{name:'Para llevar',exact:true}).click();await page.getByLabel(/Nombre del cliente/).fill('Mostrador <b>QA</b>');
 await expect(display.getByText(name,{exact:true})).toBeVisible();const total=(await page.locator('.counter-total strong').textContent())!;await expect(display.locator('.cd-total strong')).toHaveText(total);
 await page.getByRole('button',{name:'$500',exact:true}).click();await expect(page.getByText(/Venta registrada ·/)).toBeVisible();await expect(page.locator('.counter-done .ui-status')).toHaveText('PARA LLEVAR');await expect(display.getByText('¡Gracias por su compra!')).toBeVisible();
 const state=(await call('counter','state')).data;const visit=state.visits.find((v:any)=>v.prepaid&&v.takeout);expect(visit).toBeTruthy();expect(visit.tableId||'').toBe('');expect(visit.customerName).toBe('Mostrador <b>QA</b>');
 const pay=state.payments.find((p:any)=>p.visitId===visit.id);expect(pay.method).toBe('cash');expect(pay.change).toBe(50000-pay.amount);expect(state.commands.find((c:any)=>c.visitId===visit.id).label).toBe('PARA LLEVAR');
 await page.getByRole('button',{name:'Nueva venta',exact:true}).click();await expect(page.getByText('Orden vacía')).toBeVisible();expect(errors).toEqual([]);
});
test('back office buys from a supplier, pays the payable, spends petty cash and sets a manager PIN',async({page})=>{
 const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));await login(page,'backoffice');
 const dialog=()=>page.getByRole('dialog');const save=async()=>{await dialog().getByRole('button',{name:'Guardar',exact:true}).click();await expect(dialog()).toHaveCount(0);};
 await nav(page,'Inventario');
 await inventoryMenu(page,'Almacén');await dialog().getByLabel('Nombre',{exact:true}).fill('Bodega QA');await save();
 await inventoryMenu(page,'Insumo');await dialog().getByLabel('Nombre',{exact:true}).fill('Harina QA');await save();
 await nav(page,'Compras y proveedores');
 await page.getByRole('button',{name:'Proveedor',exact:true}).click();await dialog().getByLabel('Nombre o razón social').fill('Molinos QA');await dialog().getByLabel('Días de crédito').fill('20');await save();
 await page.getByRole('button',{name:'Orden de compra',exact:true}).click();
 await dialog().locator('.po-top select').nth(0).selectOption({label:'Molinos QA'});await dialog().locator('.po-top select').nth(1).selectOption({label:'Bodega QA'});await dialog().getByLabel('Insumo',{exact:true}).selectOption({label:'Harina QA'});
 await dialog().getByLabel('Cantidad de Harina QA').fill('5');await dialog().getByLabel('Costo de Harina QA').fill('250');await dialog().getByRole('button',{name:'Crear orden'}).click();await expect(dialog()).toHaveCount(0);
 const row=page.locator('tr',{hasText:'Molinos QA'});await expect(row.getByText('Por recibir')).toBeVisible();await row.getByRole('button',{name:'Recibir'}).click();
 await dialog().getByLabel('Cantidad de Harina QA').fill('4.5');await dialog().getByLabel('Costo de Harina QA').fill('225');await dialog().getByPlaceholder('Ej. F-1024').fill('MQ-77');await dialog().getByRole('button',{name:'Registrar entrada'}).click();await expect(dialog()).toHaveCount(0);
 let state=(await call('backoffice','state')).data;const supplier=state.suppliers.find((x:any)=>x.name==='Molinos QA');const order=state.purchases.find((x:any)=>x.supplierId===supplier.id);const wh=state.warehouses.find((x:any)=>x.name==='Bodega QA');const flour=state.ingredients.find((x:any)=>x.name==='Harina QA');
 expect(order.status).toBe('received');expect(order.differences).toBe(1);expect(state.stocks.find((x:any)=>x.warehouseId===wh.id&&x.ingredientId===flour.id).quantity).toBe(4_500_000);
 const payable=state.payables.find((x:any)=>x.purchaseId===order.id);expect(payable.amount).toBe(22500);expect(payable.status).toBe('open');
 await page.getByRole('tab',{name:/Cuentas por pagar/}).click();await page.locator('tr',{hasText:'MQ-77'}).getByRole('button',{name:'Pagar'}).click();await dialog().getByLabel('Referencia').fill('SPEI QA');await save();
 state=(await call('backoffice','state')).data;expect(state.payables.find((x:any)=>x.id===payable.id).status).toBe('paid');
 await nav(page,'Gastos y caja chica');
 await page.getByRole('button',{name:'Cuenta de gasto',exact:true}).click();await dialog().getByLabel('Nombre',{exact:true}).fill('Limpieza QA');await dialog().getByLabel('Presupuesto mensual MXN').fill('1000');await save();
 await page.getByRole('button',{name:'Reponer caja chica'}).click();await dialog().getByLabel('Importe MXN').fill('500');await dialog().getByLabel(/Tomar el efectivo de la caja abierta/).setChecked(false);await save();
 await page.getByRole('button',{name:'Registrar gasto'}).click();await dialog().getByLabel('Cuenta de gasto').selectOption({label:'Limpieza QA'});await dialog().getByLabel('Importe MXN').fill('120');await dialog().getByLabel('Concepto').fill('Jabón QA');await save();
 state=(await call('backoffice','state')).data;const account=state.expenseaccounts.find((x:any)=>x.name==='Limpieza QA');expect(state.expenses.find((x:any)=>x.accountId===account.id).amount).toBe(12000);
 expect(state.pettymoves.reduce((t:number,m:any)=>t+(m.type==='in'?m.amount:-m.amount),0)).toBe(38000);await expect(page.getByText('$380.00').first()).toBeVisible();
 await page.getByRole('button',{name:/Menú de /}).click();await page.getByRole('menuitem',{name:/Mi PIN de autorización/}).click();await dialog().getByLabel('PIN nuevo (4 a 6 dígitos)').fill('1234');await dialog().getByRole('button',{name:'Guardar',exact:true}).click();await expect(dialog().getByRole('alert')).toContainText('secuencia');
 await dialog().getByLabel('PIN nuevo (4 a 6 dígitos)').fill('4826');await save();
 state=(await call('backoffice','state')).data;const me=state.users.find((x:any)=>x.username==='gerencia');expect(me.hasPin).toBe(true);expect(me.pinHash).toBeUndefined();
 await nav(page,'Equipo y dispositivos');await expect(page.locator('tr',{hasText:'gerencia'}).getByText('Asignado')).toBeVisible();expect(errors).toEqual([]);
});
test('loyalty wallet pays at the counter, staff meal goes to payroll, price list and X reading work',async({page})=>{
 const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));await login(page,'loyalty');
 const dialog=()=>page.getByRole('dialog');const save=async()=>{await dialog().getByRole('button',{name:'Guardar',exact:true}).click();await expect(dialog()).toHaveCount(0);};
 await nav(page,'Clientes y monedero');
 await page.getByRole('button',{name:'Programa de lealtad'}).click();await dialog().getByLabel(/Programa activo/).setChecked(true);await dialog().getByLabel('Cashback %').fill('5');await save();
 await page.getByRole('button',{name:'Cliente',exact:true}).click();await dialog().getByLabel('Nombre',{exact:true}).fill('Lucía QA');await dialog().getByLabel('Teléfono').fill('5550001111');await save();
 await page.locator('tr',{hasText:'Lucía QA'}).getByRole('button',{name:'Recargar'}).click();await dialog().getByLabel('Importe MXN').fill('1000');await dialog().getByLabel('Recibido en').selectOption('card');await save();
 let state=(await call('loyalty','state')).data;const lucia=state.customers.find((c:any)=>c.name==='Lucía QA');expect(state.walletmoves.filter((m:any)=>m.customerId===lucia.id).reduce((t:number,m:any)=>t+m.amount,0)).toBe(100000);
 await nav(page,'Mostrador');const first=page.locator('.counter-product').first();await first.click();if(await dialog().isVisible())await dialog().getByRole('button',{name:'Agregar a la orden',exact:true}).click();
 await page.getByLabel(/Cliente frecuente/).selectOption(lucia.id);await page.getByRole('button',{name:/Pagar con monedero/}).click();await expect(page.getByText('Pagado con monedero')).toBeVisible();
 state=(await call('loyalty','state')).data;const walletPay=state.payments.find((p:any)=>p.method==='wallet');expect(walletPay).toBeTruthy();expect(state.visits.find((v:any)=>v.id===walletPay.visitId).customerId).toBe(lucia.id);
 await page.getByRole('button',{name:'Nueva venta',exact:true}).click();await page.getByRole('button',{name:'Consumo de empleado'}).click();await first.click();if(await dialog().isVisible())await dialog().getByRole('button',{name:'Agregar a la orden',exact:true}).click();
 await page.getByRole('button',{name:'Cargar a nómina'}).click();await expect(page.getByText('Cargo a nómina')).toBeVisible();
 state=(await call('loyalty','state')).data;const charge=state.staffcharges.at(-1);expect(charge.status).toBe('open');expect(state.payments.find((p:any)=>p.visitId===charge.visitId).method).toBe('payroll');expect(state.commands.find((c:any)=>c.visitId===charge.visitId).label).toBe('EMPLEADO');
 await nav(page,'Catálogo');await page.getByRole('button',{name:'Lista',exact:true}).click();await dialog().getByLabel('Nombre',{exact:true}).fill('Hora QA');await dialog().getByLabel('Ajuste general %').fill('-10');await save();
 state=(await call('loyalty','state')).data;const list=state.pricelists.find((l:any)=>l.name==='Hora QA');expect(list.adjustBps).toBe(-1000);expect(list.channels).toEqual(['dine-in']);
 await nav(page,'Reportes');await page.getByRole('button',{name:'Corte X'}).click();await expect(dialog().getByRole('heading',{name:'Corte X'})).toBeVisible();await expect(dialog().getByText('Monedero')).toBeVisible();await expect(dialog().getByText('Nómina')).toBeVisible();
 expect(errors).toEqual([]);
});
test('integrations run in simulation: terminal, platform order, PAC stamp, outbox and locked live mode',async({page})=>{
 const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));await login(page,'integrations');
 const dialog=()=>page.getByRole('dialog');const save=async()=>{await dialog().getByRole('button',{name:'Guardar',exact:true}).click();await expect(dialog()).toHaveCount(0);};
 const exec=async(type:string,payload:any)=>{const r=await call('integrations','execute',{type,payload});if(r.ok)await call('integrations','ack',{operationId:r.data.operationId});return r;};
 const shot=async(name:string)=>{await page.waitForTimeout(650);await page.screenshot({path:path.join(evidence,'etapa4-'+name+'.png'),fullPage:true});};
 await nav(page,'Integraciones');await expect(page.getByText('Todo está en simulación.')).toBeVisible();
 for(const name of ['Terminal bancaria','Plataformas de reparto','Facturación CFDI 4.0','WhatsApp y correo','Pagos en línea']){await page.getByRole('switch',{name:'Activar '+name}).click();await expect(page.getByRole('switch',{name:'Apagar '+name})).toBeVisible();}
 const live=await exec('integration.configure',{provider:'pac',mode:'live'});expect(live.ok).toBe(false);
 await page.locator('.integration-card',{hasText:'Plataformas de reparto'}).getByRole('button',{name:'Simular pedido'}).click();await dialog().getByLabel('Cliente').fill('Laura QA');await save();
 await shot('integraciones-1360');
 // Platform order: accept, kitchen label, platform payment.
 await nav(page,'Delivery y clientes');const order=page.locator('.kc.platform',{hasText:'Laura QA'});await expect(order).toBeVisible();await order.getByRole('button',{name:'Aceptar'}).click();
 await expect(page.locator('.kc.platform',{hasText:'Laura QA'}).getByRole('button',{name:'Entregar al repartidor'})).toBeVisible();
 let state=(await call('integrations','state')).data;const accepted=state.aggregatororders.find((o:any)=>o.customerName==='Laura QA');expect(accepted.status).toBe('accepted');
 expect(state.commands.find((c:any)=>c.visitId===accepted.visitId).label).toBe('UBER EATS');expect(state.payments.find((p:any)=>p.visitId===accepted.visitId).method).toBe('platform');
 await shot('delivery-plataformas-1360');
 // PIN pad from an account: declined, then approved with authorization code.
 const table=state.tables.find((t:any)=>t.status==='free'),waiter=state.users.find((u:any)=>u.role==='mesero'&&u.active),burger=state.products.find((p:any)=>p.name==='Hamburguesa de la casa');
 const opened=await exec('visit.open',{tableId:table.id,people:2,waiterId:waiter.id});const visitId=opened.data.result.id;
 await exec('command.send',{visitId,items:[{productId:burger.id,quantity:1}]});
 await nav(page,'Mesas y cuentas');await page.locator('[data-open-account="'+visitId+'"]').click();await page.getByRole('button',{name:'Cobrar con terminal'}).click();
 const terminal=page.getByRole('dialog',{name:'Cobrar con terminal'});await terminal.getByRole('radio',{name:/Rechazada/}).click();await terminal.getByRole('button',{name:'Enviar a la terminal'}).click();await expect(terminal.getByText('RECHAZADA')).toBeVisible();
 await terminal.getByRole('radio',{name:/Aprobada/}).click();await terminal.getByRole('button',{name:'Intentar con otra tarjeta'}).click();await expect(terminal.getByText('APROBADA')).toBeVisible();
 await page.waitForTimeout(650);await page.screenshot({path:path.join(evidence,'etapa4-terminal-1360.png')});await terminal.getByRole('button',{name:'Listo'}).click();
 state=(await call('integrations','state')).data;const card=state.payments.find((p:any)=>p.visitId===visitId&&p.method==='card');expect(card.terminal.authCode).toMatch(/^\d{6}$/);
 expect(state.terminaltxns.filter((t:any)=>t.visitId===visitId).map((t:any)=>t.status).sort()).toEqual(['approved','declined']);await expect(page.getByText(/Visa •••• 4242 · aut\./)).toBeVisible();
 await page.getByRole('button',{name:'Cerrar detalle',exact:true}).click();
 // Close the platform order and stamp its invoice with the simulated PAC.
 for(const unit of state.units.filter((u:any)=>u.visitId===accepted.visitId&&u.status==='received')){await exec('unit.state',{id:unit.id,status:'preparing'});await exec('unit.state',{id:unit.id,status:'ready'});}
 await nav(page,'Delivery y clientes');await page.locator('.kc.platform',{hasText:'Laura QA'}).getByRole('button',{name:'Entregar al repartidor'}).click();await expect(page.locator('.kc.platform',{hasText:'Laura QA'}).getByText(/Entregado a/)).toBeVisible();
 const fiscal=await exec('fiscal.request',{visitId:accepted.visitId,rfc:'EKU9003173C9',name:'ESCUELA KEMPER URGATE',postalCode:'42501',regime:'601',use:'G03',email:'qa@example.mx'});expect(fiscal.ok).toBe(true);
 await nav(page,'Fiscal');await expect(page.getByText('PAC simulado activo.')).toBeVisible();await page.getByRole('button',{name:'Timbrar (simulado)'}).first().click();await expect(page.getByText('Timbrada · simulación sin validez fiscal').first()).toBeVisible();
 state=(await call('integrations','state')).data;const stamped=state.fiscals.find((f:any)=>f.id===fiscal.data.result.id);expect(stamped.status).toBe('simulated-stamped');expect(state.messages.some((m:any)=>m.channel==='email'&&m.body.includes(stamped.fiscalUuid))).toBe(true);
 await shot('fiscal-simulado-1360');
 await nav(page,'Integraciones');await expect(page.locator('.r.outbox',{hasText:'qa@example.mx'})).toBeVisible();await expect(page.getByRole('cell',{name:/Cargo|Rechazo|CFDI/}).first()).toBeVisible();
 await page.evaluate(()=>{document.documentElement.dataset.theme='dark';});await shot('integraciones-oscuro-1360');await page.evaluate(()=>{delete document.documentElement.dataset.theme;});
 await page.setViewportSize({width:390,height:844});await shot('integraciones-390');expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
 expect(errors).toEqual([]);
});
test('a PAC submission that lost its answer is retried by the manager with the same folio and date',async({page})=>{
 const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));await login(page,'fiscal');
 const exec=async(type:string,payload:any)=>{const r=await call('fiscal','execute',{type,payload});expect(r.ok,r.error).toBe(true);await call('fiscal','ack',{operationId:r.data.operationId});return r.data.result;};
 // The Facturama sandbox leaves the requests alone while the PAC simulator is on.
 await exec('integration.configure',{provider:'pac',mode:'off'});
 let state=(await call('fiscal','state')).data;const burger=state.products.find((p:any)=>p.name==='Hamburguesa de la casa');
 const visitId=(await exec('visit.quick',{})).id;const units=(await exec('command.send',{visitId,items:[{productId:burger.id,quantity:1}]})).unitIds;
 for(const id of units)for(const status of ['preparing','ready','delivered'])await exec('unit.state',{id,status});
 await exec('payment.add',{visitId,amount:18500,tip:0,method:'cash',tendered:18500});await exec('visit.close',{id:visitId});
 const id=(await exec('fiscal.request',{visitId,rfc:'EKU9003173C9',name:'ESCUELA KEMPER URGATE',postalCode:'42501',regime:'601',use:'G03'})).id;
 // Facturama stamps and the answer is lost: the request waits for the manager instead of being sent again.
 let sandbox=(await call('fixture','fiscal.sandbox',{mode:'lost'})).data;
 state=(await call('fiscal','state')).data;let request=state.fiscals.find((f:any)=>f.id===id);expect(request.status).toBe('pending-reconciliation');
 const sent=()=>sandbox.stamps.filter((x:any)=>x.folio===request.folio);expect(sent()).toHaveLength(1);
 await nav(page,'Fiscal');const row=page.getByRole('row').filter({hasText:visitId.slice(0,8)});await expect(row.getByText('Pendiente de conciliación')).toBeVisible();await expect(row.getByText(request.folio)).toBeVisible();
 await row.getByRole('button',{name:'Reintentar',exact:true}).click();
 const dialog=page.getByRole('dialog',{name:'Reintentar con el folio '+request.folio});await dialog.getByLabel('Revisé Facturama y el folio no existe').fill('No aparece en el portal de Facturama');
 await page.waitForTimeout(650);await page.screenshot({path:path.join(evidence,'fiscal-reintento-1360.png'),fullPage:true});
 await dialog.getByRole('button',{name:'Guardar',exact:true}).click();await expect(dialog).toHaveCount(0);
 await expect(row.getByText(/Reintento autorizado/)).toBeVisible();await expect(row.getByRole('button',{name:'Reintentar',exact:true})).toHaveCount(0);
 // The retry sends the same Folio and Date, so a stamp that did happen would be found instead of duplicated.
 sandbox=(await call('fixture','fiscal.sandbox',{mode:'ok'})).data;expect(sent()).toHaveLength(2);expect(sent().every((x:any)=>x.date===request.issuedLocal)).toBe(true);
 state=(await call('fiscal','state')).data;request=state.fiscals.find((f:any)=>f.id===id);expect(request.status).toBe('sandbox-stamped');expect(request.retryReason).toBe('No aparece en el portal de Facturama');
 await expect(row.getByText('Prueba sandbox · sin validez fiscal')).toBeVisible();
 expect(errors).toEqual([]);
});
