# Culinary Restaurant

## Cliente nuevo con CSS (Windows)

La interfaz React/CSS y Tauri se encuentra en `src/MesaNexo.Web`. Usa el diseño grafito y ámbar aprobado, con iconos por función, tarjetas alineadas y controles uniformes. Conserva el backend .NET y el transporte seguro. [Diseño y verificación](docs/DISENO-APROBADO.md).

```powershell
.\artifacts\desktop-css\CulinaryRestaurant.exe
```

Mantén `culinary-bridge.exe` al lado del ejecutable. Compilar: `powershell -ExecutionPolicy Bypass -File deploy/Build-Hybrid.ps1`. [Arquitectura, pruebas y estado por plataforma](docs/MIGRACION-TAURI.md). Avalonia sigue disponible durante la transición; Android/iOS aún requieren portar el puente móvil y Linux requiere validación en su propio sistema.


Aplicación nativa de restaurante con Avalonia/.NET 10 y API local independiente. Esta revisión 0.2.0 amplía POS, inventario, caja ciega, delivery, KDS y servicios centrales. Conserva el nombre **Culinary Restaurant**, los dos logos originales y el diseño **Grafito y Ámbar** elegido como referencia del prototipo.

- [Diseño, iconos y uso de cada logo](docs/MARCA-Y-DISENO.md)
- [Captura de mesas](evidence/native-Mesas-y-cuentas-1440.png)
- [Cuenta lateral](evidence/native-account-1440.png)
- [Personalización del pedido](evidence/native-order-product-1440.png)
- [Acceso con el logo completo](evidence/native-login-1440.png)

Ajuste visual actual en fuente: tipografía, cabecera y animaciones nativas revisadas. Para verlo, inicia el cliente con `dotnet run --project .\src\MesaNexo.Desktop -c Debug` (backend activo) o usa `tools/Start-Lab.ps1`. [Detalles](docs/DISENO-NATIVO-Y-CSS.md). Los paquetes 0.2.0 enlazados abajo corresponden a la entrega anterior a este ajuste.

**Escritorio Windows actualizado:** con el backend activo, cierra la ventana anterior y abre [artifacts/desktop-actual/CulinaryRestaurant.exe](artifacts/desktop-actual/CulinaryRestaurant.exe). Incluye bordes completos, aumento al pasar el cursor y rebote al hacer clic. Validación: 143 comprobaciones de interfaz aprobadas.

## Paquetes de desarrollo

- [Instalador Windows x64](artifacts/CulinaryRestaurant-0.2.0-dev-win-x64.exe)
- [Debian 13 amd64](artifacts/CulinaryRestaurant-0.2.0-dev-amd64.deb)
- [APK Android de prueba](artifacts/android/CulinaryRestaurant-0.2.0-debug.apk)
- [Código fuente](artifacts/CulinaryRestaurant-0.2.0-source.zip)
- [Servicio central Linux x64](artifacts/CulinaryRestaurant-0.2.0-central-linux-x64.zip)
- [Portal público Linux x64](artifacts/CulinaryRestaurant-0.2.0-public-linux-x64.zip)
- [SHA-256](artifacts/SHA256SUMS.txt)

Windows instala únicamente el cliente por usuario; necesita un servidor de sucursal aprovisionado o el laboratorio siguiente. Los paquetes no tienen firma productiva del editor; Android usa firma de depuración. Debian y Android compilan, pero falta ejecución en sus entornos reales. iOS incluye nombre, recursos y proyecto; no hay IPA ni compilación en Mac.

## Probar en Windows

Se requiere SDK .NET 10.0.402 y Python con PyNaCl; el Python ya instalado de CulinaryOS cumple esas dependencias. No se modifican servicios ni datos de CulinaryOS:

```powershell
cd C:\Users\kikin\Desktop\MesaNexoNativo
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Start-Lab.ps1
```

Si el laboratorio aún no está inicializado, el asistente pide restaurante, usuario y contraseña propios, sin cuentas demo. Al inicializar muestra el código de alta una sola vez, la CA pública y la huella. En «Configurar dirección del backend» usa `https://localhost:8443`, identidad `manual-lab`, CA y huella verificadas, y código de un solo uso. Después inicia sesión. La configuración y borradores quedan protegidos por el almacén seguro de la plataforma.

El laboratorio usa `.lab/manual`, almacenamiento cifrado original aislado y puente local de pruebas. No es una instalación productiva de la infraestructura. El backend y el cliente son procesos independientes. El iniciador abre el cliente React/CSS con Tauri y WebView2; usa `-Avalonia` para el cliente anterior o `-NoClient` para iniciar sólo el servidor.

Si ya hay un servidor en el puerto, el iniciador valida TLS, CA, nombre e identidad antes de reutilizarlo. Si necesita compilar, publica en una carpeta única bajo `.lab/start-builds`, sin sobrescribir las DLL de procesos abiertos. Un puerto ocupado por otro servicio produce un error y no abre otra instancia. Los usuarios y datos existentes se conservan; la presencia del archivo SQLite por sí sola no se considera una inicialización completa.

## Código y validación

`MesaNexo.slnx` incluye Core, API, cliente, escritorio, central, Integrations, Public y pruebas; los proyectos móviles están separados. Los nombres técnicos y los identificadores de instalación previos se conservan para preservar compatibilidad. Publicaciones actuales en `artifacts/0.2.0`; los artefactos 0.1.0 y 0.1.1 permanecen intactos.

- [Matriz DEVMap y límites reales](docs/DEVMap-REQUISITOS.md)
- [Trabajar en VS Code](docs/VS-CODE.md)
- [Configuración y aislamiento de proveedores](docs/PROVEEDORES.md)
- [Estado, pruebas y pendientes](docs/ESTADO.md)
- [Reproducir pruebas](docs/PRUEBAS.md)
- [Matriz de paridad](docs/PARIDAD.md)
- [Arquitectura](docs/ARQUITECTURA.md)
- [Integración CulinaryOS](docs/CULINARYOS.md)
- [Migración desde copias](docs/MIGRACION.md)
- [Recuperación](docs/RECUPERACION.md)
- [Android e iOS](docs/MOVILES.md)
- [Auditoría del prototipo](docs/AUDITORIA.md)
- [Auditoría fiscal, CSD, terminales y continuidad](docs/AUDITORIA-FISCAL-Y-CONTINUIDAD.md)

El proyecto no se declara listo para producción ni se considera la validación headless equivalente a probar dispositivos, instaladores o una actualización real.
La reserva ocurre al enviar a cocina; el consumo durable se crea al liquidar. Cancelar libera sólo lo no preparado. Los pedidos públicos esperan revisión y commit del gerente local. Por decisión del usuario los adaptadores externos permanecen aislados, sin rutas ni comandos de activación; no hay emisión fiscal, cobro online, impresión, AMI, geocodificación ni push reales.

Validación actual: **976 comprobaciones de integración/interfaz y 28 del runtime aprobadas** (31 de octubre de 2026, `evidence/integration.json` y `evidence/runtime.json`). Lo que se puede prometer está en [ESTADO-COMERCIAL.md](docs/ESTADO-COMERCIAL.md). Windows: arranque del cliente publicado comprobado; Android: compilación y firma debug verificadas. Linux es publicación cruzada; iOS permanece sin compilar. La revisión visual interactiva del portal queda pendiente por falta de navegador disponible.


Integración con CulinaryOS: [instalación en VirtualBox e inicio automático de la app](docs/CULINARYOS-INSTALACION.md).

Modo kiosco de la VM: [instalación, verificación y recuperación](docs/CULINARYOS-KIOSCO.md).
