# Plan de acción · Auditoría técnica 0.3.0

Respuesta a «Auditoría técnica · Culinary Restaurant · Problemas detectados y soluciones» (18 hallazgos). La auditoría sólo leyó el PDF de documentación del 27 de octubre de 2026. Cada hallazgo se contrastó con el código del 29 de octubre de 2026 en `MesaNexoNativo`, `CulinaryOS` y `CulinaryServer`. El estado indica lo que el código hace hoy, no lo que dice la documentación.

Estados: **Confirmado** (el riesgo existe en el código), **Parcial** (hay una parte resuelta y falta otra), **Desactualizado** (el código ya no hace lo que describe el documento, pero falta demostrarlo o documentarlo), **Fuera del código** (requiere hardware, cuentas, asesoría o procesos).

## Decisiones (29 de octubre de 2026)

- **Llave raíz:** fuera de línea; firma sólo intermedias. Una intermedia de licencias vive en el Worker; las actualizaciones se firman con otra intermedia desde la estación del proveedor. Las sucursales existentes se reaprovisionan con la nueva cadena.
- **Licencia:** avisos antes de vencer y gracia extendida (7 días) sólo cuando el servicio de licencias no responde. Vencida o revocada: bloqueo como hoy, sin exportar.
- **Copia externa y puntos de control de la bitácora:** Cloudflare R2, con credenciales de sólo escritura por sucursal y separadas del Worker de licencias.
- **Segunda aprobación:** configurable por el dueño, 1,000 MXN por defecto, para reembolsos, cancelaciones y ajustes.
- **Recuperación:** RTO de 4 horas y RPO de 15 minutos (copia externa cada 15 minutos).
- **Orden:** Fase 1 completa, en el orden del documento, antes de la Fase 2.

## Resumen

| ID | Hallazgo | Estado en el código | Fase |
|---|---|---|---|
| 1 | Llave raíz en servicio en línea | Confirmado | 1 |
| 2 | Eliminación de eventos del outbox | Desactualizado en PostgreSQL; queda una ruta en Python | 1 |
| 3 | Restauración y recuperación de llaves | Parcial | 1 |
| 4 | Pagos con resultado desconocido | Parcial | 1 |
| 5 | Bloqueo por licencia | Confirmado (decisión de negocio) | 2 |
| 6 | Certificados con renovación manual | Confirmado | 2 |
| 7 | Identidad de hardware y TPM | Parcial | 2 |
| 8 | Bitácora sin anclaje externo | Confirmado | 2 |
| 9 | Servidor único por sucursal | Parcial (UPS ya contemplado) | 2 |
| 10 | Inventario concurrente y recetas | Parcial | 2 |
| 11 | Redondeo, impuestos y división | Parcial | 2 |
| 12 | Privilegios de gerente | Confirmado | 2 |
| 13 | Cadena interfaz, puente y almacén | Parcial | 2 |
| 14 | Compatibilidad de versiones y migraciones | Parcial | 2 |
| 15 | Hardware e integraciones reales | Fuera del código | 3 |
| 16 | Evidencia de pruebas | Parcial | 3 |
| 17 | Estado comercial de plataformas | Documentación | 3 |
| 18 | Privacidad y cumplimiento | Fuera del código (requiere asesoría) | 3 |

## Fase 1 · Bloqueadores antes de un piloto con dinero real

### 1. Llave raíz fuera de línea · `CulinaryServer`, `MesaNexoNativo`
**Lo que hay hoy.** `ROOT_PRIVATE_KEY` es un secreto del Worker y firma todo: licencias (`culinary-license+jws`), avisos, manifiestos (`culinary-release+jws`) y actualizaciones (`culinary-update+jws`). Además deriva la llave HMAC de los retos (`signature.ts`). `CulinaryServer/secrets/root-private.txt` sigue en el disco del equipo de desarrollo; el paso 7 del README no se completó. El panel ya pide TOTP.

**Acciones.**
1. Raíz sólo fuera de línea. Un script local (`scripts/sign-intermediate.mjs`) firma con la raíz dos certificados intermedios con fecha de vencimiento y uso limitado:
   - `license` (licencias, avisos y autorizaciones de actualización), que vive en el Worker.
   - `release` (manifiestos de paquetes con su SHA-512), que firma desde la estación del proveedor, no desde el Worker.
2. Llave HMAC de retos independiente (`CHALLENGE_KEY`), sin derivarla de la raíz.
3. La sucursal valida la cadena raíz → intermedia → documento, y que el uso de la intermedia corresponda al `typ`. Una intermedia de licencias no puede autorizar paquetes. Se agrega una lista de intermedias revocadas, firmada por la raíz.
4. Registro de cada firma en D1 (quién, qué, cuándo, `kid`).
5. Procedimiento documentado de generación, custodia, rotación y revocación. Se borra `root-private.txt` del equipo una vez que esté en custodia.

**Comprobación.** Prueba: con sólo la intermedia de licencias, firmar un `culinary-update+jws` es rechazado por la sucursal. Prueba: una intermedia revocada o vencida ya no valida licencias nuevas.

**Estado (29 de octubre de 2026): código hecho, falta el despliegue.**
- Hecho:
  - El Worker firma con `LICENSE_SIGNING_KEY` + `LICENSE_SIGNING_CHAIN`; los retos usan `CHALLENGE_KEY`; la raíz sólo se usa en `scripts/sign-intermediate.mjs` y `scripts/sign-key-floor.mjs`.
  - Las actualizaciones necesitan dos firmas que coincidan: la autorización (intermedia de licencias) y el manifiesto del paquete (intermedia de paquetes, firmado en la estación con `scripts/sign-release.mjs` y pegado en CulinaryAdmin).
  - En lugar de una lista de revocadas, un piso de llaves por generación (`KEY_FLOOR`), que la sucursal guarda con la licencia.
  - Cada firma de licencia y de paquete queda en la auditoría de D1 con su `kid`.
  - Procedimiento en `CulinaryServer/docs/LLAVES.md`.
- Pruebas:
  - Worker: 63 de 63.
  - Sucursal: 11 nuevas en `--license-only`.
  - Panel: 9 de 9.
- Lo que falta, del proveedor:
  - Generar las intermedias y subir los secretos nuevos.
  - Borrar `ROOT_PRIVATE_KEY` del Worker y de `CulinaryServer/.dev.vars`.
  - Llevar `secrets/root-private.txt` fuera de línea.

### 2. Outbox sin descarte silencioso · `MesaNexoNativo`, `CulinaryOS`
**Lo que hay hoy.** `PostgresStore` ya no borra pendientes (comentario en la línea 20) y mueve los confirmados de más de 7 días a `events_archive`, sin borrarlos. La documentación técnica sigue describiendo el comportamiento del almacén SQLite. `runtime/mesanexo_ipc.py` aún tiene `PENDING_RETENTION`, que descarta pendientes sin central, y se instala en el appliance.

**Acciones.**
1. Confirmar si `mesanexo_ipc.py` sigue en la ruta de datos de una sucursal PostgreSQL. Si sólo sirve para migrar desde SQLite, quitar el descarte de pendientes; si ya no se usa, retirarlo del appliance.
2. Acuses duraderos por lote, con la última secuencia confirmada, y alerta (no descarte) cuando los pendientes pasen un umbral de tamaño o antigüedad.
3. Documentar qué es la «línea base» y qué historial conserva.

**Comprobación.** Prueba automática: 30 días simulados sin central, ventas y cambios. Al reconectar, cada evento llega una sola vez y en orden, y `events + events_archive` cuadra con el número de transacciones.

**Estado (30 de octubre de 2026): hecho.**
- `mesanexo_ipc.py` no está en la ruta de datos de una sucursal PostgreSQL. El backend sólo usa `PostgresBranchStore`. En el appliance, `culinaryos-data` sólo responde `health`. Como la extensión se sigue entregando y la usan las pruebas de contrato, se quitó `PENDING_RETENTION`. Sin central ya no descarta nada, y sólo un almacén que la versión anterior hizo descartar pasa a línea base. El SHA256 de esa versión (entregada en las entradas r13 y r14 del appliance) quedó en `extension-history.json`.
- `PostgresBranchStore.Acknowledge` recibe lotes. Cada lote se guarda en una transacción con `outbox_acked_seq` y se rechaza completo si deja atrás un pendiente anterior. `CentralSyncWorker` confirma de 16 en 16.
- `outbox_status_v1` responde `bytes`, `acked_seq` y `alert` (`size` arriba de 2 GiB, `age` con central y más de un día sin confirmar). `OutboxWorker` registra `COS-OUTBOX-*` y la barra de estado lo muestra. Nada se recorta.
- La línea base y la retención quedaron documentadas en `docs/ARQUITECTURA.md`.
- Pruebas:
  - `StoreTests.OfflineMonth`: 450 transacciones en 30 días sin central. Al reconectar, una central simulada pierde un acuse y rechaza una conexión. Cada evento queda confirmado una sola vez, en el orden de la sucursal, y `events + events_archive` = 450. Evidencia en `evidence/store-outbox.json` (35 de 35).
  - Extensión Python: 28 de 28.
  - Instalador: 44 de 44.
  - Central: 37 de 37.
- El backend es el único remitente. La ISO ya no incluye `mesanexo_ipc.py` y enmascara `culinaryos-sync`; `install.py` enmascara lo mismo en una instalación fuera de la ISO. El backend recibe las credenciales `central-*` sólo cuando existen las tres. Falta probarlo en systemd y con una central real.

### 3. Restauración completa en otro equipo · `CulinaryOS`
**Lo que hay hoy.** `backup.py` respalda archivos de configuración y tiene `--restore-staging`, que restaura a un directorio nuevo, no a un servidor en marcha. Hay instantáneas `pg_dump` en `/var/lib/culinaryos-app/snapshots`. El llavero `branch.keys.json` y la identidad (TPM o huella) se guardan aparte.

**Acciones.**
1. Inventario de llaves: cuáles se respaldan (llavero de datos, CA local, configuración) y cuáles se reaprovisionan (identidad TPM, licencia, que pasa por la transferencia del hallazgo 7).
2. Comando `culinaryos-restore` que, en un equipo recién instalado, carga el respaldo cifrado, el llavero y el `pg_dump`, verifica la cadena de auditoría y el outbox, y deja la licencia pendiente de transferencia.
3. RTO y RPO objetivo (ver preguntas) y documento de simulacro.

**Comprobación.** Simulacro en una VM nueva: instalar ISO, restaurar, transferir licencia y vender. Verificar ventas, recibos, caja, inventario, bitácora, facturas y outbox contra el origen. Se mide el tiempo.

### 4. Pagos inciertos · `MesaNexoNativo`
**Lo que hay hoy.** Existe el estado `uncertain` para cobros de terminal, con `terminal.reconcile` (consulta al proveedor en modo real) y bloqueo de cierre de cuenta mientras haya un cobro incierto. Faltan estados explícitos de reverso y reembolso, el registro de cada intento con referencia del proveedor, y una tabla de capacidades por proveedor (Clip, Mercado Pago Point, BBVA semi-integrado, Stripe).

**Acciones.**
1. Máquina de estados única: `pendiente → enviado → {aprobado, rechazado, incierto}`, `incierto → {aprobado, rechazado}` sólo con evidencia del proveedor o captura manual autorizada, y `aprobado → {reverso pendiente → reversado, reembolsado}`. Transiciones validadas en un solo lugar.
2. Cada intento guarda id interno, referencia del proveedor, importe, moneda, marcas de tiempo y respuesta cruda.
3. Capacidades declaradas por adaptador (idempotencia, consulta por referencia, reverso). Si un proveedor no permite consultar, el cobro incierto queda en revisión humana.

**Comprobación.** Inyección de fallos: el proveedor aprueba, se pierde la respuesta, el POS se reinicia y se reintenta. Resultado: un solo cargo y el corte cuadra. Se repite por adaptador con su simulador.

## Fase 2 · Resiliencia de la sucursal

### 5. Licencia sin dejar a la sucursal sin vender
**Hoy.** Gracia sin conexión de 3 días. Con licencia vencida, revocada o no validable sólo se muestra la pantalla de bloqueo, sin exportación (decisión del 24 de octubre). Ya se distingue el reloj atrasado y se corrige con hora NTS.
**Acciones** (según la decisión que se tome): avisos en la app a 14, 7, 3 y 1 días del vencimiento y cuando el servicio de licencias no responde; separar «servicio caído» de «licencia vencida o revocada»; garantía escrita de que un problema de licencia nunca borra, altera ni vuelve a cifrar datos.
**Comprobación.** Pruebas: Worker caído 4 días, reloj incorrecto, renovación fallida y revocación, con el comportamiento definido en cada caso.

### 6. Certificados que se renuevan solos · `CulinaryOS`
**Hoy.** El instalador genera en el propio servidor una CA de 10 años (su llave queda en el servidor) y un certificado de servidor de 365 días. No hay renovación ni avisos.
**Acciones.** Temporizador de systemd que renueva el certificado de servidor 30 días antes de vencer con la CA local, reinicia el backend y registra el evento. Las terminales ya confían en la CA, así que el certificado nuevo no requiere tocar los clientes. Avisos en la app a 30, 14 y 7 días si la renovación falla. Procedimiento de rotación de la CA para el año 9.
**Comprobación.** Prueba con reloj adelantado: renueva, las terminales se conectan y un certificado de otra CA sigue rechazándose.

### 7. Transferencia de licencia por reparación · `CulinaryServer`, `MesaNexoNativo`
**Acciones.** Flujo de transferencia en CulinaryAdmin (aprobación del proveedor y auditoría) que libera el equipo ligado y permite reactivar con el mismo código en el equipo nuevo. La huella se documenta como señal, no como secreto. El cambio de disco o de tarjeta de red no debe perder datos: la llave de datos no depende de la identidad de licencia.
**Comprobación.** Pruebas: cambio de MAC, de disco y fallo de TPM, con migración autorizada.

### 8. Puntos de control externos de la bitácora
**Acciones.** Cada hora (y en cada cierre de caja) la sucursal firma el hash de la cabeza de la bitácora y lo envía a un destino con permisos separados (ver preguntas). Un verificador independiente compara la cadena local con los puntos publicados. Ante una discrepancia: alerta, preservación de evidencia y bloqueo de nuevas autorizaciones.
**Comprobación.** Prueba: reescribir un registro antiguo y recalcular la cadena con todas las llaves locales; el verificador lo detecta.

### 9. Servidor único
**Hoy.** Hay `ups.py` con NUT, salud y respaldos.
**Acciones.** Alertas de disco (SMART), espacio, temperatura, servicios caídos y respaldo fallido visibles en la app del gerente; copia externa cifrada automática (destino en preguntas); guía de sustitución del servidor basada en el hallazgo 3.
**Comprobación.** Simulacro de servidor caído con medición del tiempo hasta volver a vender.

### 10. Inventario concurrente
**Hoy.** Reservas al enviar a cocina, consumo al cobrar y recetas versionadas. Se valida existencia al reservar.
**Acciones.** Guardar versión de receta y costo en cada movimiento; movimientos inmutables con compensaciones; política explícita de existencias negativas; idempotencia de reservar, liberar y consumir ante reintentos.
**Comprobación.** Pruebas: última unidad pedida desde dos terminales a la vez, cancelación parcial de comanda preparada, devolución de una venta antigua y conciliación de compras, ventas, mermas y ajustes.

### 11. Política monetaria y fiscal
**Acciones.** Documento único de redondeo (centavos enteros, IVA incluido, reparto de residuos en divisiones y descuentos); funciones centralizadas; trazabilidad venta → pago → devolución → CFDI.
**Comprobación.** Pruebas de propiedades (FsCheck o generador propio): la suma de partes siempre es igual al total, en divisiones, descuentos, propinas y pagos mixtos.

### 12. Separación de funciones del gerente
**Acciones.** Perfiles de autorización (supervisor con límites) y segunda aprobación arriba de un importe para reembolsos, cancelaciones y ajustes (umbral en preguntas). Registro de quién solicita, quién autoriza, motivo y resultado. Bloqueo de PIN por intentos y alertas.
**Comprobación.** Pruebas de escalamiento, PIN bloqueado, sesión revocada y operación fuera de límite.

### 13. Fallos en cada frontera
**Acciones.** Id de correlación desde la interfaz hasta el almacén; errores tipados y tiempos límite; distinguir «no ejecutado» de «ejecutado con respuesta perdida» (ya existe en parte con `command.send` y el recibo IPC). Pruebas de caída del puente, del backend y de PostgreSQL antes y después del commit.
**Comprobación.** Por cada frontera, una sola transición financiera y una sola operación persistida.

### 14. Versiones y migraciones
**Acciones.** Versión en el contrato de la API y de los eventos (`culinaryos-events-v1` ya existe); matriz de compatibilidad publicada; prueba de actualización desde 0.2.x y de reversión; respaldo automático antes de una migración destructiva.
**Comprobación.** Actualizar una copia realista con terminales antiguas conectadas y eventos pendientes, y revertir.

## Fase 3 · Validación real y lanzamiento

- **15. Hardware e integraciones.** Matriz proveedor, modelo, sistema y versión. Sandbox de Clip, Mercado Pago Point, Stripe y Facturama; impresoras y cajones físicos; piloto controlado. Sólo se publica como compatible lo validado. Requiere cuentas y equipos.
- **16. Evidencia de pruebas.** CI en GitHub Actions con informe por commit (versiones, entorno, duración y resultados), etiquetas por riesgo (dinero, inventario, seguridad, restauración y sincronización) y cobertura de ramas en `MesaNexo.Core`.
- **17. Estado comercial.** Tabla por plataforma y función con cuatro estados: implementado, probado automáticamente, validado en real y habilitado comercialmente. Corregir el resumen ejecutivo.
- **18. Privacidad.** Inventario de datos personales, finalidades y plazos; exportación y eliminación por cliente; respuesta a incidentes. Revisión con asesoría jurídica mexicana del aviso de privacidad (ya hay un borrador en el escritorio).

## Criterios para autorizar el piloto

Los de la auditoría, cada uno con su prueba o simulacro de la Fase 1 y la evidencia guardada en `evidence/`.
