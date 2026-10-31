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

// Only the appliance kiosk powers the machine. Fixed verbs, never renderer-selected
// commands; logind and the CulinaryOS polkit rule decide whether the operator may.
pub fn power(args: &Value) -> Result<Value, String> {
    if !enabled() {
        return Err("Apagar el equipo sólo está disponible en CulinaryOS".into());
    }
    let verb = match args["action"].as_str() {
        Some("poweroff") => "poweroff",
        Some("reboot") => "reboot",
        Some("suspend") => "suspend",
        _ => return Err("Acción de energía no válida".into()),
    };
    let status = std::process::Command::new("/usr/bin/systemctl")
        .args(["--no-ask-password", verb])
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .status()
        .map_err(|_| "No se pudo contactar al sistema")?;
    if !status.success() {
        return Err("El sistema no permitió la acción. Puede haber una tarea en curso; inténtalo de nuevo o avisa al administrador.".into());
    }
    Ok(json!({"ok":true,"data":{"action":verb}}))
}

// Wi-Fi of the appliance kiosk through NetworkManager. Fixed nmcli verbs only; the
// renderer supplies an SSID and password as plain arguments, never a command line.
fn nmcli(args: &[&str]) -> Result<String, String> {
    if !enabled() {
        return Err("La red sólo se administra desde CulinaryOS".into());
    }
    let output = std::process::Command::new("/usr/bin/nmcli")
        .args(args)
        .env("LC_ALL", "C")
        .stdin(std::process::Stdio::null())
        .output()
        .map_err(|_| "No se pudo contactar a NetworkManager")?;
    if output.status.success() {
        return Ok(String::from_utf8_lossy(&output.stdout).into_owned());
    }
    let error = String::from_utf8_lossy(&output.stderr).to_lowercase();
    Err(if error.contains("secrets were required") || error.contains("802-1x") || error.contains("psk") {
        "La contraseña no es correcta.".into()
    } else if error.contains("no network with ssid") {
        "La red ya no está al alcance. Busca de nuevo.".into()
    } else if error.contains("not authorized") {
        "El sistema no permitió cambiar la red. Avisa al administrador.".into()
    } else {
        "No se pudo conectar a la red. Inténtalo de nuevo.".into()
    })
}

// Terse nmcli output escapes ':' and '\' inside values.
fn fields(line: &str) -> Vec<String> {
    let (mut out, mut current, mut chars) = (Vec::new(), String::new(), line.chars());
    while let Some(c) = chars.next() {
        match c {
            '\\' => { if let Some(next) = chars.next() { current.push(next); } }
            ':' => out.push(std::mem::take(&mut current)),
            _ => current.push(c),
        }
    }
    out.push(current);
    out
}

pub fn network_status() -> Result<Value, String> {
    if !enabled() {
        return Ok(json!({"ok":true,"data":{"available":false}}));
    }
    let devices = nmcli(&["-t", "-f", "TYPE,STATE,CONNECTION", "device", "status"])?;
    let mut kind = "none";
    let mut name = String::new();
    let mut wifi = false;
    for f in devices.lines().map(fields) {
        if f.len() < 3 { continue; }
        if f[0] == "wifi" { wifi = true; }
        // Cable wins over Wi-Fi when both are up: NetworkManager routes through it first.
        if f[1] == "connected" && (f[0] == "ethernet" || (f[0] == "wifi" && kind != "ethernet")) {
            kind = if f[0] == "ethernet" { "ethernet" } else { "wifi" };
            name = f[2].clone();
        }
    }
    let mut signal = Value::Null;
    if kind == "wifi" {
        let list = nmcli(&["-t", "-f", "IN-USE,SIGNAL", "device", "wifi", "list", "--rescan", "no"])?;
        if let Some(f) = list.lines().map(fields).find(|f| f.len() >= 2 && f[0] == "*") {
            signal = f[1].parse::<u8>().map(Value::from).unwrap_or(Value::Null);
        }
    }
    Ok(json!({"ok":true,"data":{"available":true,"kind":kind,"name":name,"signal":signal,"wifi":wifi}}))
}

pub fn network_scan() -> Result<Value, String> {
    let list = nmcli(&["-t", "-f", "IN-USE,SSID,SIGNAL,SECURITY", "device", "wifi", "list", "--rescan", "yes"])?;
    let mut networks: Vec<Value> = Vec::new();
    for f in list.lines().map(fields) {
        if f.len() < 4 || f[1].is_empty() { continue; }
        let signal = f[2].parse::<u8>().unwrap_or(0);
        let secure = !f[3].trim().is_empty() && f[3].trim() != "--";
        // One row per SSID: several access points of the same network show as one.
        if let Some(seen) = networks.iter_mut().find(|n| n["ssid"] == f[1].as_str()) {
            if seen["signal"].as_u64().unwrap_or(0) < signal as u64 { seen["signal"] = signal.into(); }
            if f[0] == "*" { seen["active"] = true.into(); }
            continue;
        }
        networks.push(json!({"ssid":f[1],"signal":signal,"secure":secure,"active":f[0] == "*"}));
    }
    networks.sort_by(|a, b| b["active"].as_bool().cmp(&a["active"].as_bool())
        .then(b["signal"].as_u64().cmp(&a["signal"].as_u64())));
    Ok(json!({"ok":true,"data":{"networks":networks}}))
}

pub fn network_connect(args: &Value) -> Result<Value, String> {
    let ssid = args["ssid"].as_str().unwrap_or("");
    if ssid.is_empty() || ssid.len() > 32 || ssid.chars().any(char::is_control) {
        return Err("Nombre de red no válido".into());
    }
    let password = args["password"].as_str().unwrap_or("");
    if !password.is_empty() && (password.len() < 8 || password.len() > 63 || password.chars().any(char::is_control)) {
        return Err("La contraseña de una red Wi-Fi tiene de 8 a 63 caracteres.".into());
    }
    let mut command = vec!["--wait", "45", "device", "wifi", "connect", ssid];
    if !password.is_empty() {
        // A failed attempt leaves a profile with the wrong key; nmcli would reuse it.
        let _ = nmcli(&["connection", "delete", "id", ssid]);
        command.extend(["password", password]);
    }
    nmcli(&command)?;
    Ok(json!({"ok":true,"data":{"ssid":ssid}}))
}

// Battery of a laptop or UPS while mains power is out. Figures the hardware does
// not report stay null: the screen shows less rather than an invented estimate.
type Supply = std::collections::BTreeMap<String, String>;

fn number(text: &str) -> Option<f64> {
    text.trim().parse::<f64>().ok().filter(|value| value.is_finite() && *value >= 0.0)
}

fn minutes_left(cells: &[&Supply], stored: &str, rate: &str) -> Option<f64> {
    let (mut left, mut drain) = (0.0, 0.0);
    for cell in cells {
        left += number(cell.get(stored)?)?;
        drain += number(cell.get(rate)?)?;
    }
    (drain > 0.0).then(|| (left / drain * 60.0).round())
}

fn laptop_source(supplies: &[Supply]) -> Option<Value> {
    // Wireless mice and keyboards also report here, with scope=Device.
    let cells: Vec<&Supply> = supplies.iter()
        .filter(|s| s.get("type").is_some_and(|v| v.trim() == "Battery"))
        .filter(|s| s.get("scope").is_none_or(|v| v.trim() != "Device"))
        .collect();
    if !cells.iter().any(|s| s.get("status").is_some_and(|v| v.trim() == "Discharging")) {
        return None;
    }
    let levels: Vec<f64> = cells.iter().filter_map(|s| s.get("capacity").and_then(|v| number(v))).collect();
    let percent = (!levels.is_empty()).then(|| (levels.iter().sum::<f64>() / levels.len() as f64).round());
    let minutes = minutes_left(&cells, "energy_now", "power_now").or_else(|| minutes_left(&cells, "charge_now", "current_now"));
    Some(json!({"onBattery":true,"source":"laptop","percent":percent,"minutes":minutes}))
}

fn ups_source(upsc: &str) -> Option<Value> {
    let read = |key: &str| upsc.lines().find_map(|line| line.strip_prefix(key)?.strip_prefix(": ").map(str::trim));
    // OB means on battery; OL, on line. A status can carry several flags, such as "OB LB".
    if !read("ups.status")?.split_whitespace().any(|flag| flag == "OB") {
        return None;
    }
    let percent = read("battery.charge").and_then(number).map(f64::round);
    let minutes = read("battery.runtime").and_then(number).map(|seconds| (seconds / 60.0).round());
    Some(json!({"onBattery":true,"source":"ups","percent":percent,"minutes":minutes}))
}

fn read_supplies() -> Vec<Supply> {
    let Ok(entries) = std::fs::read_dir("/sys/class/power_supply") else { return Vec::new(); };
    entries.flatten().map(|entry| {
        ["type", "scope", "status", "capacity", "energy_now", "power_now", "charge_now", "current_now"].iter()
            .filter_map(|name| Some((name.to_string(), std::fs::read_to_string(entry.path().join(name)).ok()?)))
            .collect()
    }).collect()
}

// The UPS name is the one CulinaryOS gives it (docs/UPS-Y-APAGONES.md); no NUT, no UPS.
fn read_ups() -> Option<String> {
    let output = std::process::Command::new("/usr/bin/upsc")
        .arg("culinaryos@localhost")
        .stdin(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .output()
        .ok()?;
    output.status.success().then(|| String::from_utf8_lossy(&output.stdout).into_owned())
}

pub fn power_source() -> Result<Value, String> {
    let source = if enabled() {
        laptop_source(&read_supplies()).or_else(|| read_ups().as_deref().and_then(ups_source))
    } else {
        None
    };
    Ok(json!({"ok":true,"data":source.unwrap_or(json!({"onBattery":false}))}))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn supply(fields: &[(&str, &str)]) -> Supply {
        fields.iter().map(|(k, v)| (k.to_string(), format!("{v}\n"))).collect()
    }

    #[test]
    fn laptop_on_battery_reports_level_and_time() {
        let cell = supply(&[("type", "Battery"), ("status", "Discharging"), ("capacity", "78"), ("energy_now", "30000000"), ("power_now", "15000000")]);
        let mains = supply(&[("type", "Mains")]);
        let source = laptop_source(&[mains, cell]).unwrap();
        assert_eq!(source["source"], "laptop");
        assert_eq!(source["percent"], 78.0);
        assert_eq!(source["minutes"], 120.0);
    }

    #[test]
    fn laptop_reporting_charge_instead_of_energy_still_gets_time() {
        let cell = supply(&[("type", "Battery"), ("status", "Discharging"), ("capacity", "50"), ("charge_now", "2000000"), ("current_now", "4000000")]);
        assert_eq!(laptop_source(&[cell]).unwrap()["minutes"], 30.0);
    }

    #[test]
    fn laptop_without_a_rate_shows_no_estimate() {
        let cell = supply(&[("type", "Battery"), ("status", "Discharging"), ("capacity", "50"), ("energy_now", "2000000"), ("power_now", "0")]);
        let source = laptop_source(&[cell]).unwrap();
        assert_eq!(source["percent"], 50.0);
        assert!(source["minutes"].is_null());
    }

    #[test]
    fn plugged_in_or_peripheral_batteries_are_ignored() {
        let charging = supply(&[("type", "Battery"), ("status", "Charging"), ("capacity", "90")]);
        let mouse = supply(&[("type", "Battery"), ("scope", "Device"), ("status", "Discharging"), ("capacity", "40")]);
        assert!(laptop_source(&[charging, mouse]).is_none());
    }

    #[test]
    fn ups_reports_only_while_on_battery() {
        let source = ups_source("battery.charge: 64\nbattery.runtime: 1530\nups.status: OB LB\n").unwrap();
        assert_eq!(source["source"], "ups");
        assert_eq!(source["percent"], 64.0);
        assert_eq!(source["minutes"], 26.0);
        assert!(ups_source("battery.charge: 100\nups.status: OL CHRG\n").is_none());
    }
}
