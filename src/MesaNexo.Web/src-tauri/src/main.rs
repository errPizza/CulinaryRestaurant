#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
use std::{io::{BufRead, BufReader, Write}, process::{Child, ChildStdin, Command, Stdio}, sync::{Arc, Mutex, mpsc}, time::Duration};
use serde_json::{json, Value};
use tauri::Manager;
struct Bridge { child: Child, input: ChildStdin, replies: mpsc::Receiver<String>, sequence: u64 }
impl Bridge {
    fn start() -> Result<Self,String> {
        let exe=std::env::current_exe().map_err(|e|e.to_string())?;
        let mut path=exe.parent().ok_or("Directorio de aplicaciÃ³n invÃ¡lido")?.join(if cfg!(windows){"culinary-bridge.exe"}else{"culinary-bridge"});
        if cfg!(debug_assertions) && !path.exists() {
            path=std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("binaries").join(if cfg!(windows){"culinary-bridge-x86_64-pc-windows-msvc.exe"}else{"culinary-bridge-x86_64-unknown-linux-gnu"});
        }
        let mut command=Command::new(path);command.stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::null());
        #[cfg(windows)] { use std::os::windows::process::CommandExt;command.creation_flags(0x08000000); }
        let mut child=command.spawn().map_err(|_|"No se pudo iniciar el transporte seguro de Culinary Restaurant. Prepara el sidecar antes de iniciar.".to_owned())?;
        let input=child.stdin.take().ok_or("Pipe de entrada no disponible")?;
        let output=child.stdout.take().ok_or("Pipe de salida no disponible")?;
        let (tx,rx)=mpsc::channel();
        std::thread::spawn(move || {for line in BufReader::new(output).lines(){match line{Ok(s)=>{if tx.send(s).is_err(){break;}},Err(_)=>break}}});
        Ok(Self{child,input,replies:rx,sequence:0})
    }
    fn call(&mut self,command:String,args:Value)->Result<Value,String>{
        self.sequence+=1;let id=self.sequence.to_string();
        let line=json!({"id":id,"command":command,"args":args}).to_string();
        if line.len()>2_000_000{return Err("Solicitud demasiado grande".into());}
        writeln!(self.input,"{}",line).map_err(|_|"Transporte seguro desconectado")?;self.input.flush().map_err(|_|"No se pudo enviar la solicitud")?;
        let reply=self.replies.recv_timeout(Duration::from_secs(120)).map_err(|_|"Transporte sin respuesta. Reinicia la app y reconcilia antes de repetir la operaciÃ³n.")?;
        let response:Value=serde_json::from_str(&reply).map_err(|_|"Respuesta nativa invÃ¡lida")?;
        if response["id"]!=id{return Err("Respuesta nativa fuera de secuencia; reinicia y reconcilia".into());}
        Ok(response)
    }
}
impl Drop for Bridge { fn drop(&mut self){let _=self.child.kill();let _=self.child.wait();} }
struct State(Arc<Mutex<Bridge>>);
mod kiosk;
#[tauri::command]
async fn native_call(window: tauri::WebviewWindow, state: tauri::State<'_,State>, command:String, args:Value)->Result<Value,String>{
    if window.label()!="main" {return Err("Ventana no autorizada".into());}
    if command=="window.present" {return kiosk::present(&window);}
    if command=="window.ready" {return kiosk::ready(&window);}
    if command=="power.status" {return Ok(json!({"ok":true,"data":{"available":kiosk::enabled()}}));}
    if command=="power.request" {return tauri::async_runtime::spawn_blocking(move || kiosk::power(&args)).await.map_err(|_|"No se pudo atender la solicitud".to_owned())?;}
    if command=="power.source" {return tauri::async_runtime::spawn_blocking(kiosk::power_source).await.map_err(|_|"No se pudo atender la solicitud".to_owned())?;}
    if command=="network.status" {return tauri::async_runtime::spawn_blocking(kiosk::network_status).await.map_err(|_|"No se pudo atender la solicitud".to_owned())?;}
    if command=="network.scan" {return tauri::async_runtime::spawn_blocking(kiosk::network_scan).await.map_err(|_|"No se pudo atender la solicitud".to_owned())?;}
    if command=="network.connect" {return tauri::async_runtime::spawn_blocking(move || kiosk::network_connect(&args)).await.map_err(|_|"No se pudo atender la solicitud".to_owned())?;}
    let bridge=state.0.clone();let app=window.app_handle().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let installing=command=="updates.install";
        let response=bridge.lock().map_err(|_|"Transporte ocupado")?.call(command,args)?;
        if installing && response["ok"]==true && response["data"]["started"]==true {app.exit(0);}
        Ok(response)
    }).await.map_err(|_|"No se pudo atender la solicitud".to_owned())?
}
// Customer-facing display: a second window without bridge access, placed on another monitor when one exists.
#[tauri::command]
async fn open_customer_display(app: tauri::AppHandle)->Result<(),String>{
    if let Some(window)=app.get_webview_window("customer"){let _=window.show();let _=window.set_focus();return Ok(());}
    let window=tauri::WebviewWindowBuilder::new(&app,"customer",tauri::WebviewUrl::App("cliente.html".into())).title("Pantalla del cliente").inner_size(1024.0,700.0).build().map_err(|e|e.to_string())?;
    let primary=app.primary_monitor().ok().flatten();
    if let (Ok(monitors),Some(primary))=(app.available_monitors(),primary){
        if let Some(second)=monitors.iter().find(|m|m.position()!=primary.position()){let _=window.set_position(*second.position());let _=window.set_fullscreen(true);}
    }
    Ok(())
}
fn main(){
    tauri::Builder::default().plugin(tauri_plugin_dialog::init()).setup(|app|{app.manage(State(Arc::new(Mutex::new(Bridge::start().map_err(std::io::Error::other)?))));kiosk::setup(app)?;Ok(())})
    .on_window_event(kiosk::window_event)
    .invoke_handler(tauri::generate_handler![native_call,open_customer_display]).run(tauri::generate_context!()).expect("No se pudo iniciar Culinary Restaurant");
}
