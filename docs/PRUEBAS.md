# Reproducir validación

Requisitos: SDK 10.0.402 (`global.json`), restore NuGet, Python con `requirements-lab.txt` (PyNaCl 1.6.2) y sesión Windows con gráficos para las pruebas Avalonia/DPAPI. El Python de CulinaryOS instalado ya cumplía las dependencias; no se instala sobre ese directorio. Para otra máquina, usar entorno virtual propio.

Desde la raíz:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Test.ps1 -Python C:\Ruta\python.exe
```

El script ejecuta `python -B tests/test_runtime_extension.py` y `dotnet run --project tests/MesaNexo.Tests -c Debug -- <raiz> <python>`. Las pruebas de .NET guardan los datos en PostgreSQL 17: `tools/Get-Postgres.ps1` deja los binarios oficiales para Windows en `.tools/postgres`, y cada ejecución levanta un clúster privado en `.lab/pg-run-…` que se borra al terminar. `--store-only` corre sólo las pruebas del almacén y de la migración desde SQLite. Genera almacenamiento/certificados sintéticos privados en `.lab/` y evidencia pública en `evidence/`. No apunta al socket productivo ni importa datos del usuario. No empaquetar `.lab`. Los fixtures criptográficos en tests/vectors son públicos y exclusivos de pruebas.

Los procesos de integración escuchan solo loopback en puertos libres. Prueban API de sucursal, tres roles, conflictos, eventos durables, TLS, central mTLS, permisos, importación y recuperación; el renderizado headless usa estados sintéticos reales obtenidos de las pruebas. El doble FaultStore pierde la respuesta después del commit verdadero; MemoryVault es un doble explícito, mientras MigrationTests prueba DPAPI de Windows por separado. Los fallos de disco/permisos del runtime se inyectan.

Resultados conservados: integration.json (suite completa), runtime.json, y central/business/migration.json (ejecuciones enfocadas anteriores). No sumar esos últimos como pruebas adicionales a las 190 aserciones completas. Las comprobaciones headless no demuestran comportamiento del teclado virtual, suspensión móvil o accesibilidad con lector de pantalla físico.

Publicación: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -NsisCompiler <ruta-makensis>`. Ver versiones/rutas móviles en MOVILES.md. Después `python -B tools/package_deb.py` y `python -B tools/package_delivery.py`. Este último requiere las publicaciones y `evidence/windows-startup.json`; valida integridad del prototipo antes de crear el ZIP fuente y hashes de entrega. El ZIP central es un archivo de transporte: en Linux asegurar permisos ejecutables antes de instalar su servicio.

Las pruebas POSIX/VM, dispositivo físico, instalador real y actualizaciones completas están pendientes en ESTADO.md.

## Ampliación DEVMap — 0.2.0

Ejecutar `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Test.ps1` desde la raíz. Usa Python CulinaryOS con PyNaCl y .NET 10. Los tests crean sólo directorios .lab aislados, TLS/mTLS propios y Store cifrado original; no envían datos a terceros ni tocan bases del prototipo.

DevMapTests: stock reservado/consumido, recetas cíclicas, PEPS/promedio, concurrencia/reinicio de workers, arqueo ciego API/exportación, cadena auditada y manipulación, delivery sin pago implícito, folios/impuestos/centavos, QR y efectos físicos inciertos. TenantTests: aislamiento, membresía/dispositivo, contexto cifrado, cupones, token fiscal de un uso, webhook exacto y repetido. CloudScenario: HTTPS público separado, propuesta firmada/mTLS, revisión local, reinicio y ACK durable. SchemaTests: snapshot cifrado y restauración sintética con clave original. AdapterTests: contratos con dobles HTTP/driver únicamente. UiTests: páginas y controles a 360/834/1440, teclado y modales.

Resultados actuales: evidence/integration.json y runtime.json. Capturas PNG en evidence. warnings Bitmap.Save de pruebas son de API obsoleta, no errores. Son pruebas locales/headless y no carga medida, ejecución móvil, impresora, banco, sandbox PAC ni despliegue real.

Empaquetado: tools/build.ps1 publica Windows/Linux/API/Central/Public y opcionalmente compila Android; package_deb.py verifica ar/tar y payload; package_delivery.py comprueba evidencia, integridad de referencias y excluye estado privado. Verify-Branding.ps1 y la sonda --verify-startup del ejecutable publicado validan marca/arranque Windows. No instalar los paquetes como parte de esta suite.

Revisión visual: se inspeccionaron capturas PNG nativas de Inventario (360), Delivery (1440) y Cocina (834). La comprobación interactiva en navegador del portal quedó pendiente: el entorno CUA no expuso navegadores. El preview sintético GET-only se detuvo; no se recibió ningún pedido ni se contactó un proveedor. La prueba real HTTPS/mTLS local de CloudScenario sí se ejecuta en la suite.

Resultado final 0.2.0: **303 comprobaciones de integración/interfaz y 19 pruebas del runtime aprobadas**; 120 comprobaciones de interfaz incluidas en esa suite. El editor conserva la tasa de impuesto del producto; la auditoría identifica producto/costos/referencias antes y después. Evidencia: `evidence/integration.json`, `runtime.json` y `ui-final.json`.
