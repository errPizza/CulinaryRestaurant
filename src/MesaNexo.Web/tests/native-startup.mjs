import {chromium} from '@playwright/test';
import {spawn} from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import net from 'node:net';
const root=path.resolve('../..'),output=path.join(root,'evidence');
const app=path.join(root,'artifacts/desktop-css/CulinaryRestaurant.exe');
const port=await new Promise(resolve=>{const server=net.createServer();server.listen(0,'127.0.0.1',()=>{const p=server.address().port;server.close(()=>resolve(p));});});
const child=spawn(app,[],{cwd:path.dirname(app),windowsHide:true,env:{...process.env,WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS:'--remote-debugging-port='+port,WEBVIEW2_USER_DATA_FOLDER:path.join(root,'.lab','tauri-startup-'+Date.now())},stdio:'ignore'});
let browser;
try{
 let last;for(let i=0;i<30;i++){try{browser=await chromium.connectOverCDP('http://127.0.0.1:'+port);break;}catch(e){last=e;await new Promise(resolve=>setTimeout(resolve,500));}}
 if(!browser)throw last;
 const pages=browser.contexts().flatMap(c=>c.pages());const page=pages.find(p=>p.url().includes('tauri.localhost'))||pages[0];if(!page)throw new Error('No WebView2 page');
 await page.getByRole('heading',{name:'Inicia sesión'}).waitFor({timeout:20000});
 const ipc=await page.evaluate(()=>window.__TAURI_INTERNALS__.invoke('native_call',{command:'motion.get',args:{}}));if(!ipc.ok)throw new Error('Native IPC did not respond');
 const enter=page.getByRole('button',{name:'Entrar a la operación'});await enter.waitFor({timeout:20000});
 if(await enter.isDisabled())throw new Error('Login button is blocked despite the configured local profile');
 for(const [id,name] of [['login-password','contraseña'],['login-enrollment','código de alta']]){
  const field=page.locator('#'+id);await field.pressSequentially('Comprobacion-visual',{delay:20});
  await page.getByRole('button',{name:'Mostrar '+name,exact:true}).click();
  if(await field.getAttribute('type')!=='text'||await field.inputValue()!=='Comprobacion-visual')throw new Error('Secret visibility toggle failed');
  await page.getByRole('button',{name:'Ocultar '+name,exact:true}).click();
  if(await field.getAttribute('type')!=='password')throw new Error('Secret hide toggle failed');await field.focus();if(id==='login-password'){await page.waitForTimeout(150);await page.locator('.secret-field').first().screenshot({path:path.join(output,'hybrid-native-secret.png')});}await field.fill('');
 }
 const status=await page.evaluate(()=>window.__TAURI_INTERNALS__.invoke('native_call',{command:'status',args:{}}));
 if(status.data?.authenticated)throw new Error('Visibility controls must not submit login');
 await page.waitForTimeout(750);await page.screenshot({path:path.join(output,'hybrid-tauri-windows.png'),fullPage:true});
 const boundary=await page.evaluate(()=>({title:document.title,logos:document.images.length,loginArtworkImages:document.querySelectorAll('.login-art img').length,localStorage:Object.keys(localStorage),sessionStorage:Object.keys(sessionStorage),overflow:document.documentElement.scrollWidth>innerWidth}));
 if(boundary.title!=='Culinary Restaurant'||boundary.logos!==1||boundary.loginArtworkImages!==0||boundary.localStorage.length||boundary.sessionStorage.length||boundary.overflow)throw new Error('Native window boundary validation failed');
 const result={result:'passed',engine:browser.version(),nativeIPC:true,packagedAssets:true,secretVisibilityControls:true,singleRevealControl:true,loginEnabled:true,...boundary};fs.writeFileSync(path.join(output,'hybrid-tauri-startup.json'),JSON.stringify(result,null,2));console.log(JSON.stringify(result));
}finally{if(browser)await browser.close();child.kill();}
