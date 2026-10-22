//! Linux appliance presentation. Closing the Windows desktop app is unaffected.
use serde_json::{json, Value};
use tauri::{Manager, WebviewWindow};

pub fn enabled() -> bool {
    cfg!(target_os = "linux") && std::env::var("CULINARY_RESTAURANT_KIOSK").as_deref() == Ok("1")
}

pub fn setup(app: &tauri::App) -> Result<(), Box<dyn std::error::Error>> {
    let window = app.get_webview_window("main").ok_or("Main window missing")?;
    if enabled() {
        window.set_decorations(false)?;
        window.set_resizable(false)?;
        window.set_closable(false)?;
        window.set_fullscreen(true)?;
        // The Linux config starts hidden. React presents it after fonts and DOM load.
    } else {
        window.show()?;
    }
    Ok(())
}

pub fn window_event(window: &tauri::Window, event: &tauri::WindowEvent) {
    if enabled() && window.label() == "main" {
        if let tauri::WindowEvent::CloseRequested { api, .. } = event {
            api.prevent_close();
        }
        if let tauri::WindowEvent::Focused(true) = event {
            // Restore GTK's content-widget focus when the top-level window regains it.
            // Native dialogs keep focus while active; no timer forces focus away from them.
            if let Some(main) = window.app_handle().get_webview_window("main") {
                let view: &tauri::Webview = main.as_ref();
                let _ = view.set_focus();
            }
        }
    }
}

pub fn present(window: &WebviewWindow) -> Result<Value, String> {
    if enabled() {
        window.set_fullscreen(true).map_err(|e| e.to_string())?;
        window.show().map_err(|e| e.to_string())?;
        window.set_focus().map_err(|e| e.to_string())?;
        let view: &tauri::Webview = window.as_ref();
        view.set_focus().map_err(|e| e.to_string())?;
    }
    Ok(json!({"ok":true,"data":{"enabled":enabled()}}))
}

pub fn ready(window: &WebviewWindow) -> Result<Value, String> {
    if enabled() {
        if !window.is_visible().map_err(|e| e.to_string())? {
            return Err("Window is not visible".into());
        }
        #[cfg(target_os = "linux")]
        {
            use std::os::unix::net::UnixDatagram;
            let runtime = std::env::var("XDG_RUNTIME_DIR").map_err(|_| "Missing runtime directory")?;
            let nonce = std::env::var("CULINARY_RESTAURANT_KIOSK_NONCE").map_err(|_| "Missing session nonce")?;
            if nonce.len() != 64 || !nonce.bytes().all(|b| b.is_ascii_hexdigit()) {
                return Err("Invalid session nonce".into());
            }
            // No renderer-selected paths or commands. Only the session's fixed Unix socket.
            let socket = UnixDatagram::unbound().map_err(|e| e.to_string())?;
            socket.connect(std::path::Path::new(&runtime).join("culinary-restaurant-kiosk/ready.sock"))
                .map_err(|e| e.to_string())?;
            let message = json!({"protocol":1,"pid":std::process::id(),"nonce":nonce}).to_string();
            socket.send(message.as_bytes()).map_err(|e| e.to_string())?;
        }
    }
    Ok(json!({"ok":true,"data":{"visible":true}}))
}
