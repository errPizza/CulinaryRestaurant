# Interfaz React/CSS con Tauri — etapa Windows

El nuevo cliente vive en `src/MesaNexo.Web`. Reutiliza los componentes React del prototipo y `branding/reference-grafito-ambar.css`, con los dos logotipos suministrados. No utiliza los estilos verdes de `studio.css`. Hover, pulsación, pop, entrada y salida de diálogos se ejecutan en CSS/DOM. Respeta la preferencia de movimiento reducido del sistema y la opción guardada en la bóveda.

## Iniciar Windows

Desde la raíz de MesaNexoNativo:

```powershell
.\artifacts\desktop-css\CulinaryRestaurant.exe
```

Para iniciar también el laboratorio local, ejecuta `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Start-Lab.ps1`. Valida y reutiliza un backend existente o compila el nuevo en una carpeta independiente de `.lab/start-builds`, evitando bloqueos de DLL. Si faltan las cuentas iniciales, solicita tus credenciales y muestra el código de alta una sola vez.

Conserva `culinary-bridge.exe` en la misma carpeta. Alternativa: `powershell -ExecutionPolicy Bypass -File deploy/Start-Hybrid.ps1`. El cliente usa WebView2 del sistema y el sidecar .NET autocontenido. La interfaz y sus recursos están empaquetados; no necesita Node.js, Rust ni internet para arrancar. Para operar necesita el servidor local HTTPS y el aprovisionamiento de confianza existente. No inicia ni modifica el backend por abrir la aplicación.

Usa el mismo perfil y la misma bóveda de usuario que Avalonia: CA, huella, identidad de sitio y credencial de dispositivo. La contraseña y el token de sesión no se guardan en localStorage ni sessionStorage. No se migran ni reinician datos del restaurante. La aplicación Avalonia permanece disponible durante la transición.

## Arquitectura y límites

React invoca un comando Tauri limitado. Rust inicia un binario fijo de la aplicación y habla por pipes privados; no publica un servidor HTTP de puente. `MesaNexo.Transport` compila las mismas fuentes de `NativeSession`, `TrustedConnection` y `Updates`, sin referencia a Avalonia. `MesaNexo.Bridge` conserva sesión y bóveda DPAPI/Secret Service fuera del WebView. Los comandos de archivo requieren un selector nativo y no admiten rutas libres desde JavaScript.

Se conservan TLS con CA aprovisionada, comprobación de nombre y sitio, recibos UUID, persistencia antes de enviar y reconciliación. Una comanda confirmada vacía el borrador protegido antes de retirar el pendiente. En el cliente híbrido el pendiente se conserva además hasta que React reconoce el recibo; el ACK comprueba de nuevo ese mismo UUID en el servidor. Si se pierde la respuesta entre .NET y WebView, la reconciliación recupera el recibo original y no genera otra mutación. Las mutaciones siguen pasando por las API y reglas actuales. Las integraciones CUPS, Asterisk, geocodificación y PAC permanecen aisladas: no se añadieron rutas ni comandos para activarlas.

Pantallas trasladadas: servicio, mesas/cuentas, KDS/historial, catálogo, reservaciones, evaluaciones, caja tradicional/ciega, inventario/recetas, delivery/clientes, conciliación de impresión, solicitudes fiscales/QR, reportes históricos, equipo/dispositivos y configuración. El proveedor utiliza los contratos actuales del central; el dueño consulta su espejo de analítica. Esto no equivale a una certificación de cada combinación de negocio: el recorrido POS y los módulos indicados en la evidencia automatizada son el alcance probado de esta etapa.

El acceso «Actualizar aplicación» conserva el flujo manual de actualización: verifica firma, destinatario, versión y SHA-512; consulta `can-install`, prepara una copia y vuelve a verificar bytes y permiso antes de confirmar el instalador. No se ejecutó ningún instalador durante esta migración. Los paquetes Tauri de distribución aún requieren firma del editor y validación de su ciclo de actualización. El transporte móvil no está implementado: un sidecar de escritorio no puede reutilizarse tal cual en Android/iOS. El código React es adaptable, pero Android/iOS siguen usando Avalonia hasta portar Keystore/Keychain, conexión segura y recuperación al puente móvil. Linux tiene configuración y preparación del sidecar x64; su build y pruebas deben ejecutarse en Linux con WebKitGTK y Secret Service. No se entrega un binario Linux probado desde Windows.

## Compilar y verificar

Requisitos: .NET 10, Node.js >=22.12, Rust estable, C++ Build Tools y Windows SDK; WebView2 para ejecutar. En este equipo Rust y los componentes SDK que faltaban se prepararon en `.tools`, sin modificar la instalación de Visual Studio.

```powershell
powershell -ExecutionPolicy Bypass -File deploy/Build-Hybrid.ps1
powershell -ExecutionPolicy Bypass -File deploy/Start-Hybrid.ps1 -Development
```

`npm run prepare:bridge` publica el sidecar autocontenido para la plataforma. Tauri lo ejecuta antes de desarrollo/build. En Linux: `npm ci`, `npm run prepare:bridge`, `npm run tauri build -- --config src-tauri/tauri.linux.conf.json`.

Pruebas del puente: `dotnet run --project tests/MesaNexo.Hybrid.Tests -c Release -- <raíz> <python-CulinaryOS>`. Pruebas web: construir antes ese proyecto y ejecutar `npm test` en `src/MesaNexo.Web`. Playwright sustituye sólo la entrada IPC por un binding de prueba; los comandos pasan por BridgeHost, HTTPS real, runtime cifrado y DPAPI de un directorio `.lab/hybrid-*` aislado. No usa cuentas ni datos de producción.

Evidencia: `evidence/hybrid-bridge.json`, `evidence/hybrid-web-tests.json`, capturas `evidence/hybrid-*.png` y regresión `evidence/integration.json`. Las capturas de Playwright verifican Chromium; la comprobación del ejecutable Tauri se registra aparte. El origen y el alcance de los recursos copiados están descritos aquí para que el prototipo original permanezca intacto.

## Resultado de verificación de esta etapa

- 21 comprobaciones del puente aprobadas: HTTPS/runtime/DPAPI reales, identidad/CA, UUID y ACK del renderer, recuperación de respuesta IPC perdida, cambio exacto, catálogo protegido y barreras del actualizador. El instalador no se ejecutó.
- 9 escenarios Playwright aprobados con recursos de producción: CSS/logos/hover/pop y anchos 1360/390, POS completo, recarga con sesión nativa, carga de administración, permisos de cajero/movimiento reducido, pérdida de recibo IPC sin duplicación y formularios de inventario/receta con cantidades y costos exactos. El séptimo reproduce una conexión sin configurar, comprueba los dos controles de visibilidad y recupera el acceso después de una contraseña rechazada.
- 312 comprobaciones existentes de integración/UI aprobadas nuevamente después del cambio compartido de transporte. Estos recuentos corresponden a suites con alcances distintos y no se presentan como escenarios independientes sumados.

Comando de verificación del cliente: `powershell -ExecutionPolicy Bypass -File tools/Test-Hybrid.ps1`. Agrega `-NativeStartup` para comprobar además el ejecutable construido mediante WebView2. La comprobación nativa no inicia sesión ni consulta datos del restaurante.

Arranque del ejecutable final aprobado con WebView2 155.0.4283.60: recursos empaquetados e IPC real, dos logos, sin desbordamiento horizontal ni datos en localStorage/sessionStorage. Captura: `evidence/hybrid-tauri-windows.png`; resultado: `evidence/hybrid-tauri-startup.json`. Hashes de los dos ejecutables en `artifacts/desktop-css/verification.json`.

Validación del iniciador: 8 comprobaciones aprobadas en un laboratorio cifrado separado: reutilización sin recompilar DLL abiertas, arranque desde publicación independiente, conservación de registros y rechazo de un puerto ajeno. Reproducir con `<python-CulinaryOS> -B tests/test_lab_launcher.py` (requiere el fixture `MesaNexo.Hybrid.Tests` compilado). Evidencia: `evidence/lab-launcher.json`. No inicializa ni detiene `.lab/manual`.

Corrección de acceso: contraseña y código de alta tienen controles independientes para mostrar/ocultar su contenido, accesibles por teclado y sin enviar el formulario. Si falta la conexión, se indica el motivo y enviar el formulario abre su configuración. El botón sólo se bloquea durante carga o conexión. Las pruebas usan credenciales temporales y un laboratorio aislado; no verifican ni modifican la contraseña del usuario.


Corrección de Soporte: la vista consultaba `restaurant.commercial.supportUntil`, pero la API local entrega `restaurant.supportUntil`. Se utiliza el campo real con compatibilidad con la estructura anterior y una alternativa si no hay fecha. La autorización acepta horas enteras entre 1 y 24, conforme al contrato. Un ticket local sin el campo histórico `covered` ya no se presenta como si hubiera sido creado sin cobertura. El octavo escenario reproduce el bloqueo anterior y verifica navegación, creación y respuesta de solicitudes, autorización y revocación en un laboratorio cifrado separado.

Referencia del diseño aprobado por el usuario: `evidence/design-review/propuesta.html`, con capturas `support.png`, `overview.png` y `tables.png` en esa misma carpeta. Es una maqueta independiente con datos de ejemplo; la maqueta sigue siendo independiente. Su diseño se trasladó a componentes reales de la aplicación. Propone iconos por función, alturas consistentes y acentos por estado, conservando grafito y ámbar. La línea visual ya fue aprobada y se integró al cliente. [Implementación y verificación](DISENO-APROBADO.md).

El noveno escenario verifica el diseño aprobado: alturas de métricas tras la animación de entrada, iconos de función, filtros y búsqueda de mesas, filtros de solicitudes y ausencia de desbordamiento en Resumen/Mesas/Soporte/Inventario/Equipo a anchos de escritorio y móvil. El ejecutable Windows también comprueba el control único de visibilidad de contraseña.
