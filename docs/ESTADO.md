# Estado de entrega — 16 de octubre de 2026

## Migración a React/CSS y Tauri

Cliente Windows nuevo en `artifacts/desktop-css/CulinaryRestaurant.exe`, con sidecar .NET autocontenido. Prototipo grafito/ámbar, logos y animaciones DOM/CSS. Se mantienen el backend, la bóveda y los recibos de operaciones; el puente añade reconocimiento del renderer para cubrir pérdidas de respuesta IPC. Regresión existente: 312 comprobaciones aprobadas. Evidencia y alcance actualizado en [MIGRACION-TAURI.md](MIGRACION-TAURI.md). Linux requiere compilación/QA en Linux y Android/iOS conservan Avalonia hasta portar el puente móvil. Las secciones siguientes documentan las entregas previas.

## Evidencia ejecutada

- Solución .NET: compilada. Publicación autocontenida Windows x64, cliente/backend Linux x64 y central Linux x64 con runtime 10.0.13.
- Android: APK Debug compilado con JDK 21/SDK Android y Avalonia 12.1.4; no ejecutado en teléfono ni emulador. Hay dos avisos de API NSD obsoleta, sin errores.
- Integración y UI: 190 aserciones aprobadas en `evidence/integration.json`; no equivale a 190 escenarios independientes. Usan API HTTPS real, procesos, SQLite cifrado original, reinicio, conflictos y pérdida de respuesta posterior al commit. El doble de fallos está identificado en las pruebas.
- Runtime: 17 pruebas aprobadas en `evidence/runtime.json`. Incluyen vectores criptográficos, atomicidad y deduplicación. Disco lleno y fallo de permisos se inyectan; no se llenó físicamente el disco ni se validaron permisos POSIX desde Windows.
- UI: verificación Avalonia headless/Skia de 14 vistas operativas y 7 de proveedor en anchos 360, 834 y 1440; foco/Escape en diálogos. Se guardaron capturas de todas las vistas en cada ancho, además de acceso, cuenta lateral y personalización de pedidos. Son renderizados reales de controles, no ejecución en dispositivos móviles.
- Central: persistencia, mTLS, confirmación durable, contratación firmada, respuesta de soporte con eco local, publicación y snapshot/restauración probados localmente. No se desplegó en infraestructura remota.

La revisión 0.1.1 incorpora el nombre Culinary Restaurant y los logos originales en Windows, Linux, Android y recursos iOS. Se repitió la suite completa después de la revisión visual (190 comprobaciones). El ajuste final de traducciones y pendientes se verificó después con otras 104 comprobaciones enfocadas de UI; no se suman como escenarios de negocio independientes. Consulta `evidence/delivery.json` para las verificaciones finales de paquetes, arranque Windows e integridad del prototipo.

## Artefactos de desarrollo

| Archivo en artifacts | Contenido |
|---|---|
| CulinaryRestaurant-0.1.1-dev-win-x64.exe | Instalador de cliente Windows x64 |
| CulinaryRestaurant-0.1.1-dev-amd64.deb | Cliente/backend e integración CulinaryOS para Debian 13 amd64 |
| android/CulinaryRestaurant-0.1.1-debug.apk | APK firmado con clave de depuración, no distribución productiva |
| CulinaryRestaurant-0.1.1-source.zip | Código, contratos, pruebas, documentación y evidencia, sin caches ni secretos |
| CulinaryRestaurant-0.1.1-central-linux-x64.zip | Servicio central autocontenido, requiere aprovisionamiento |
| SHA256SUMS.txt | Integridad de archivos entregados, sin constituir firma del editor |

## Pendientes para despliegue

No se ejecutaron en Debian los sockets Unix, systemd, el instalador de extensión, dpkg, Secret Service ni descubrimiento multicast. La VM CulinaryOS existente no fue modificada. Debe validarse apagado/reinicio, recuperación, arranque automático y operación con varios equipos físicos en esa VM. La comprobación criptográfica local no sustituye pruebas de corte eléctrico real.

Windows: falta instalar/desinstalar en una máquina limpia y aplicar una actualización real. El paquete de desarrollo no tiene firma de editor. Linux: falta validar dependencias y experiencia de escritorio en Debian 13. Android: falta ejecución física, permisos, reconexión, suspensión/reanudación y firma de publicación. iOS: fuente únicamente; no se compiló ni ejecutó, no hay IPA.

No hay prueba de impresora física, banco, facturación fiscal ni control remoto del sistema operativo. Las funciones que eran simuladas en el prototipo conservan esa condición. Persisten diferencias respecto al prototipo documentadas en PARIDAD.md: imágenes inline grandes, restauración administrativa y validación visual pendiente en sistemas distintos de Windows. No se declara paridad total ni preparación para producción.

## Ampliación DEVMap — 0.2.0

Esta revisión amplía la base existente; el historial 0.1.x anterior se conserva. Estado detallado por requisito, archivo y prueba: [DEVMap-REQUISITOS.md](DEVMap-REQUISITOS.md).

Integrado: cuentas impresas bloqueadas, servicio rápido, división por persona/N con impuestos exactos, inventario y recetas versionadas, reservas/consumo durable/mermas, almacenes y costo promedio/PEPS, arqueo ciego y ajustes, auditoría encadenada, CRM/delivery/pickup, semáforos KDS, SignalR TLS, central multiempresa, PWA y propuestas de nube con aceptación local, analítica del dueño y solicitud fiscal/QR.

La política de stock y el aislamiento de adaptadores son decisiones expresas del usuario. No hay rutas ni comandos para activar CUPS/AMI/geocodificación/PAC/pagos/push. Las pruebas de adaptadores usan transportes sintéticos; no equivalen a sandbox real, banco, teléfono, impresora o factura emitida.

La suite completa se registra en `evidence/integration.json` y `runtime.json`, incluyendo TLS/mTLS reales en loopback y el Store cifrado real en laboratorio. La interfaz se comprueba a 360/834/1440. No se declara operación productiva ni ejecución en Debian/Android/iOS a partir de esas pruebas. La publicación Windows tiene sonda de arranque nativo; no se instala el instalador. Android es APK debug; iOS continúa sólo en fuente.

Paquetes actuales 0.2.0: cliente Windows, cliente/backend Debian, APK debug, central Linux, portal Linux separado y código fuente con workspace VS Code. Los paquetes anteriores permanecen. Los SHA-256 comprueban integridad, no firma del editor. Faltan hardware, despliegue autorizado, restauración con datos reales, carga medida, firma productiva, pruebas de actualización y plataformas indicadas arriba.

Resultado final 0.2.0: **303 comprobaciones de integración/interfaz y 19 pruebas del runtime aprobadas**; 120 comprobaciones de interfaz incluidas en esa suite. El editor conserva la tasa de impuesto del producto; la auditoría identifica producto/costos/referencias antes y después. Evidencia: `evidence/integration.json`, `runtime.json` y `ui-final.json`.

## Ajuste visual posterior a 0.2.0

Corregidos fuente/plantillas de botón, cabecera compacta, iconos vectoriales y entradas animadas nativas con reducción de movimiento. Validación focalizada: 129 comprobaciones de interfaz aprobadas (evidence/ui-style-review.json). No se repitió la suite de dominio para un cambio de presentación. Detalles y límites: [DISENO-NATIVO-Y-CSS.md](DISENO-NATIVO-Y-CSS.md). Disponible al compilar Debug; paquetes 0.2.0 anteriores conservados.

## Botones actualizados

Bordes redondeados completos, sombra suave, hover y rebote de clic implementados con reducción de movimiento. Pasaron 143 comprobaciones de interfaz, incluidas 14 de botones y activación única con ratón/Enter/Espacio. Nueva publicación Windows en artifacts/desktop-actual; se conserva el instalador anterior. Detalles: [DISENO-NATIVO-Y-CSS.md](DISENO-NATIVO-Y-CSS.md).
