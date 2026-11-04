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
- Desplegado el 31 de octubre de 2026: intermedias de generación 1, secretos nuevos y `ROOT_PRIVATE_KEY` fuera del Worker.
- Lo que falta, del proveedor:
  - Borrar `ROOT_PRIVATE_KEY` de `CulinaryServer/.dev.vars`.
  - Llevar `secrets/root-private.txt` y `secrets/release-g1-private.txt` fuera de línea, y borrar del equipo `license-g1-private.txt`.

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

**Estado (30 de octubre de 2026): código hecho, falta el simulacro.**
- Hecho:
  - Inventario de llaves y procedimiento en `CulinaryOS/docs/SIMULACRO-RESTAURACION.md`.
  - `culinaryos-recovery-kit`: guarda en un archivo cifrado con contraseña (Argon2id) el llavero, la identidad, la CA y las credenciales.
  - `culinaryos-restore`: carga el `pg_dump` en una base temporal y la verifica con el llavero del kit. Sólo si pasa, reemplaza la sucursal. Rechaza un equipo que ya tenga restaurante.
  - `MesaNexo.Api --verify-store`: comprueba todos los registros, la cadena de auditoría y `events + events_archive` contra las transacciones.
- Pruebas:
  - `StoreTests.Restore`, con `pg_dump` y `pg_restore` reales en el laboratorio.
  - `CulinaryOS/tests/test_recovery.py`: 15 de 15, con servicios y PostgreSQL simulados.
- Falta:
  - El simulacro en VM: `culinaryos-restore` no se ha ejecutado en Linux, y el RTO no se ha medido.
  - El RPO de 15 minutos: no hay temporizador de copias ni envío externo. Depende del hallazgo 9.
  - La transferencia de licencia es manual, con **Restablecer activación**. El flujo con aprobación es el hallazgo 7.

### 4. Pagos inciertos · `MesaNexoNativo`
**Lo que hay hoy.** Existe el estado `uncertain` para cobros de terminal, con `terminal.reconcile` (consulta al proveedor en modo real) y bloqueo de cierre de cuenta mientras haya un cobro incierto. Faltan estados explícitos de reverso y reembolso, el registro de cada intento con referencia del proveedor, y una tabla de capacidades por proveedor (Clip, Mercado Pago Point, BBVA semi-integrado, Stripe).

**Acciones.**
1. Máquina de estados única: `pendiente → enviado → {aprobado, rechazado, incierto}`, `incierto → {aprobado, rechazado}` sólo con evidencia del proveedor o captura manual autorizada, y `aprobado → {reverso pendiente → reversado, reembolsado}`. Transiciones validadas en un solo lugar.
2. Cada intento guarda id interno, referencia del proveedor, importe, moneda, marcas de tiempo y respuesta cruda.
3. Capacidades declaradas por adaptador (idempotencia, consulta por referencia, reverso). Si un proveedor no permite consultar, el cobro incierto queda en revisión humana.

**Comprobación.** Inyección de fallos: el proveedor aprueba, se pierde la respuesta, el POS se reinicia y se reintenta. Resultado: un solo cargo y el corte cuadra. Se repite por adaptador con su simulador.

**Estado (30 de octubre de 2026): código hecho, salvo el reverso de un cobro ya aprobado.**
- Hecho:
  - `src/MesaNexo.Core/PaymentStates.cs`: tabla única de transiciones. Corre en cada commit, así que ningún código puede sacar un cobro de un estado final.
  - Cada cambio queda en `trail`: id de intento, referencia del proveedor, importe, moneda, hora, actor y respuesta del proveedor sin datos de tarjeta ni de cliente. El mesero no recibe `trail`.
  - Capacidades declaradas por adaptador: `idempotent`, `lookup` y `reverse`. Mercado Pago las tiene todas. Clip sólo consulta por el id que ya dio. BBVA no tiene ninguna. Stripe Checkout las tiene todas.
  - La captura manual de un cobro incierto ahora la hace sólo un gerente.
  - Se corrigieron dos respuestas ambiguas, que ahora quedan como inciertas: un 2xx de Clip sin id se marcaba rechazado, y un 2xx de Mercado Pago sin id se quedaba en espera para siempre.
- Pruebas (`LiveIntegrationTests`, «A charge across a crash», con simuladores HTTP):
  - Mercado Pago: el proceso muere tras enviar el cobro. Al volver repite con la misma llave, encuentra la orden y queda un pago.
  - Clip: no se reenvía nada, queda incierto, bloquea otro cobro y la captura del gerente aplica un solo pago.
  - BBVA no pasa por esta ruta: se registra desde el voucher.
- Falta:
  - Reverso de un cobro aprobado: el dominio ya lo hace con `payment.refund` (hallazgo 11). Falta su pantalla y la segunda aprobación del hallazgo 12.
  - Validar contra los sandbox reales (hallazgo 15). Las capacidades de Clip y Mercado Pago salen de su documentación, no de una prueba real.

## Fase 2 · Resiliencia de la sucursal

### 5. Licencia sin dejar a la sucursal sin vender
**Hoy.** Gracia sin conexión de 3 días. Con licencia vencida, revocada o no validable sólo se muestra la pantalla de bloqueo, sin exportación (decisión del 24 de octubre). Ya se distingue el reloj atrasado y se corrige con hora NTS.
**Acciones** (según la decisión que se tome): avisos en la app a 14, 7, 3 y 1 días del vencimiento y cuando el servicio de licencias no responde; separar «servicio caído» de «licencia vencida o revocada»; garantía escrita de que un problema de licencia nunca borra, altera ni vuelve a cifrar datos.
**Comprobación.** Pruebas: Worker caído 4 días, reloj incorrecto, renovación fallida y revocación, con el comportamiento definido en cada caso.

**Estado (30 de octubre de 2026): código hecho, falta desplegar el Worker.**
- Hecho:
  - `OFFLINE_GRACE_MS` sube a 7 días en `CulinaryServer`. La sucursal no fija la cifra: lee el `offlineUntil` firmado. Una licencia ya emitida conserva sus 3 días hasta su siguiente renovación.
  - `LicenseView.Warning` calcula un solo aviso, el del bloqueo más cercano: `expiry` desde 14 días antes del vencimiento y `service` tras un día sin validar. Es crítico en los últimos 3 días.
  - Se muestra como franja dentro de la app, en la interfaz web y en la nativa. El gerente lo ve siempre; el resto del equipo, sólo cuando es crítico.
  - La cuenta regresiva es diaria y corre desde 14 días antes. Cubre los avisos de 14, 7, 3 y 1 días sin marcas de «ya avisado».
- Comportamiento por caso:

| Caso | Resultado |
|---|---|
| Servicio caído o sin internet, hasta 7 días | Sigue vendiendo, con cuenta regresiva |
| 7 días sin validar | Bloqueo `offline`; al renovar vuelve solo |
| Renovación fallida con el vencimiento cerca | Sigue vendiendo hasta `expiresAt`; el aviso muestra lo que ocurra primero |
| Pasó `expiresAt` | Bloqueo `expired`, aunque quede gracia: la gracia no alarga el plazo contratado |
| Aviso firmado de revocación o vencimiento | Bloqueo inmediato |
| Reloj atrasado | Bloqueo `clock`, sin cuenta regresiva |

- Garantía: un problema de licencia sólo rechaza solicitudes. Nunca borra, altera ni vuelve a cifrar datos, y al renovar la sucursal vuelve con los mismos datos. Los datos siguen sin exportarse mientras dure el bloqueo (decisión del 29 de octubre).
- Límite conocido: la sucursal no puede saber por qué falla la validación. La gracia también cubre su propia falta de internet y una renovación rechazada por hardware distinto (`binding_mismatch`). Acortarla en ese caso no serviría: quien copió la llave puede cortar la conexión al Worker.
- Pruebas (`--license-only`): 4 días sin servicio con la venta abierta, los avisos a 14, 7, 3 y 1 días, rol mesero contra gerente, renovación fallida, reloj atrasado, revocación y un bloqueo que deja intactos todos los registros y archivos de llaves. Worker: `licenses.spec.ts`.
- Falta, del proveedor: desplegar el Worker. Hasta entonces las licencias siguen saliendo con 3 días, y la pantalla de bloqueo dirá 7.

### 6. Certificados que se renuevan solos · `CulinaryOS`
**Hoy.** El instalador genera en el propio servidor una CA de 10 años (su llave queda en el servidor) y un certificado de servidor de 365 días. No hay renovación ni avisos.
**Acciones.** Temporizador de systemd que renueva el certificado de servidor 30 días antes de vencer con la CA local, reinicia el backend y registra el evento. Las terminales ya confían en la CA, así que el certificado nuevo no requiere tocar los clientes. Avisos en la app a 30, 14 y 7 días si la renovación falla. Procedimiento de rotación de la CA para el año 9.
**Comprobación.** Prueba con reloj adelantado: renueva, las terminales se conectan y un certificado de otra CA sigue rechazándose.

**Estado (30 de octubre de 2026): código hecho, falta probarlo en systemd.**
- Hecho:
  - `deploy/culinaryos/renew-tls.py`, con `culinaryos-renew-tls.timer` a diario a las 04:00. `install.py` crea las unidades, y la ISO también en su primer arranque. Renueva a 35 días, no a 30: así hay cinco intentos antes del primer aviso.
  - Conserva la llave del servidor. Sólo cambia un archivo, de forma atómica, y un fallo en cualquier paso deja el par anterior funcionando y no reinicia el backend.
  - El backend avisa con la fecha del certificado que tiene en uso, sin archivo de estado: con 30 días o menos, la renovación falló. El gerente lo ve desde 30 días, y en los últimos 7 es crítico y lo ve todo el equipo.
  - Procedimiento de rotación de la CA en `CulinaryOS/docs/CERTIFICADOS-LOCALES.md`. Cada terminal fija la huella de una sola CA, así que la rotación se hace terminal por terminal.
- Pruebas:
  - `test_tls_renewal`, con OpenSSL real y `-attime` a 300 días: el certificado nuevo valida, el viejo no, y el de otra CA tampoco. También una llave de CA dañada y una CA por vencer.
  - `--license-only`: avisos a 40, 14 y 7 días.
- Falta:
  - Ejecutar el temporizador en systemd, en la VM.
  - Comprobar una terminal real tras el reinicio del backend. Lo probado es la cadena con `openssl verify`, no el cliente.
  - Las ISO ya aprovisionadas no reciben el temporizador: `tls_renewal()` corre sólo en el primer arranque. Hoy ninguna sucursal está en producción.

### 7. Transferencia de licencia por reparación · `CulinaryServer`, `MesaNexoNativo`
**Acciones.** Flujo de transferencia en CulinaryAdmin (aprobación del proveedor y auditoría) que libera el equipo ligado y permite reactivar con el mismo código en el equipo nuevo. La huella se documenta como señal, no como secreto. El cambio de disco o de tarjeta de red no debe perder datos: la llave de datos no depende de la identidad de licencia.
**Comprobación.** Pruebas: cambio de MAC, de disco y fallo de TPM, con migración autorizada.

**Estado (30 de octubre de 2026): código hecho, falta desplegar y probar en TPM real.**
- Hecho:
  - **Liberar el equipo** en CulinaryAdmin ahora exige un motivo, que queda en la auditoría con la llave liberada. El panel exige TOTP, y esa es la aprobación del proveedor. El código de activación es nuevo, no el mismo: uno gastado no debe volver a servir.
  - El equipo liberado recibe un aviso firmado (`transferred`) en su siguiente contacto y se bloquea. Sin esto, con 7 días de gracia dos equipos podían vender a la vez una semana.
  - La huella guardada sigue a cada cambio aceptado, y el cambio queda auditado. Antes, cambiar la tarjeta de red y meses después el disco terminaba en rechazo.
  - Si rechazan el hardware, la app pide la transferencia en lugar de decir «revisa el internet».
  - Un TPM averiado muestra la pantalla de bloqueo, no un error 503 en cada solicitud.
  - La huella queda documentada como señal, no como secreto, en `CulinaryServer/README.md`.
  - La llave de datos no depende de la identidad de licencia, y no hizo falta cambiarla: el llavero vive en `/etc/culinaryos/secrets` y la licencia sólo firma solicitudes.
- Pruebas:
  - `--license-only`: tarjeta de red, disco, dos piezas a la vez, equipo liberado y llave perdida con reactivación, con los datos intactos.
  - Worker: transferencia con auditoría y cambios sucesivos de hardware.
  - Panel: el motivo es obligatorio.
- Falta:
  - Desplegar el Worker. La migración `0004_llave_liberada.sql` se aplica sola al arrancar.
  - Probar el fallo de TPM en hardware real. La prueba elimina el archivo de llave; `TpmLicenseKey` no corre en Windows.
  - Sólo se recuerda el último equipo liberado. Uno anterior, o uno sin internet, se bloquea al terminar la gracia.

### 8. Puntos de control externos de la bitácora
**Acciones.** Cada hora (y en cada cierre de caja) la sucursal firma el hash de la cabeza de la bitácora y lo envía a un destino con permisos separados (ver preguntas). Un verificador independiente compara la cadena local con los puntos publicados. Ante una discrepancia: alerta, preservación de evidencia y bloqueo de nuevas autorizaciones.
**Comprobación.** Prueba: reescribir un registro antiguo y recalcular la cadena con todas las llaves locales; el verificador lo detecta.

**Estado (30 de octubre de 2026): código hecho, falta crear los buckets y probar contra R2 real.**
- Hecho:
  - `CheckpointWorker` firma la cabeza con la llave de la sucursal y la publica cada hora y en cada cierre de caja. Si el envío falla, la siguiente cabeza cubre lo anterior.
  - La sucursal compara al arrancar y a diario. Una discrepancia avisa al gerente, detiene las autorizaciones con PIN y deja evidencia en el bucket. La venta sigue.
  - Verificador externo: `--export-audit` y `--verify-checkpoints`, sin base de datos ni llaves.
  - `culinaryos-restore` declara la restauración antes de arrancar el backend, para evitar una alerta falsa.
  - Diseño, comandos y límites en `docs/ARQUITECTURA.md`.
- Cambio respecto a la decisión del 29 de octubre: **R2 no ofrece tokens de sólo escritura**. Son de lectura y escritura por bucket. La inmutabilidad la da un bucket lock indefinido, que cubre el mismo riesgo: la credencial de la sucursal no puede sobrescribir ni borrar, y quitar el candado requiere un permiso de administración que la sucursal no tiene. El comportamiento del bloqueo sale de la documentación de Cloudflare. Los tipos de token no se confirmaron ahí: hay que revisarlos al crear el primero.
- Pruebas (`--checkpoint-only`):
  - Firma SigV4 contra el ejemplo publicado por Amazon.
  - Reescritura de un registro con toda la cadena recalculada: la revisión local la da por buena y los puntos de control la detectan, también fuera de la sucursal.
  - Bucket simulado con bloqueo, caída del bucket y aceptación tras restaurar.
  - `test_recovery.py`: la declaración ocurre antes de arrancar el backend.
- Falta:
  - Crear el bucket y el token de cada sucursal. Sin `checkpoints.json` el trabajador no corre.
  - Probar contra R2 real, incluido `If-None-Match` y el rechazo del bucket lock.
  - Programar la verificación externa. Hoy es un comando que alguien debe correr, y sin él un root malicioso sólo deja de enviar. El verificador muestra `lastAt`, pero nada avisa todavía si deja de llegar.

### 9. Servidor único
**Hoy.** Hay `ups.py` con NUT, salud y respaldos.
**Acciones.** Alertas de disco (SMART), espacio, temperatura, servicios caídos y respaldo fallido visibles en la app del gerente; copia externa cifrada automática (destino en preguntas); guía de sustitución del servidor basada en el hallazgo 3.
**Comprobación.** Simulacro de servidor caído con medición del tiempo hasta volver a vender.

**Estado (30 de octubre de 2026): código hecho, falta el simulacro y crear los buckets.**
- Hecho:
  - `culinaryos-health` agrega SMART y temperatura. Un equipo sin esos sensores, como una VM, no alarma.
  - El gerente ve en la app los avisos de disco, espacio, sólo lectura, temperatura, servicios y respaldo, leídos de `/run/culinaryos/status.json`. Un diagnóstico que deja de llegar también es un aviso. El resto del diagnóstico se queda en la consola.
  - `OffsiteBackupWorker` envía un `pg_dump` a R2 cada 15 minutos si hubo cambios, con su `.sha256`, y no deja la copia en disco. Una hora sin copias avisa, y cuatro son críticas.
  - Guía de sustitución en `CulinaryOS/docs/SIMULACRO-RESTAURACION.md`.
- Decisiones de implementación:
  - **Sin una capa de cifrado adicional.** Las filas ya van selladas con AES-GCM bajo el llavero, que nunca llega al bucket. Otra capa exigiría custodiar otra llave y cambiar la restauración. En R2 sólo se ven identificadores, tamaños y horas.
  - **Copia completa, no incremental.** Es lo que `culinaryos-restore` ya sabe cargar y verificar. El costo crece con la base: una de 200 MB son unos 2 Mbit/s de subida mientras hay actividad. Si una sucursal no da abasto, lo siguiente es archivar WAL de PostgreSQL.
- Pruebas:
  - `--checkpoint-only`: `pg_dump` real hacia el bucket simulado, vuelta por `pg_restore` a una base nueva y verificación de cadena y outbox; fallos de envío y su alerta.
  - `test_diagnostics.py`: SMART y temperatura, incluido que `smartctl` informa la falla con un código de error.
- Falta:
  - Crear el bucket y el token de cada sucursal, con ciclo de vida de 7 días. Sin `backups.json` el trabajador no corre.
  - Probar SMART en hardware real y correr `culinaryos-health` con `PrivateDevices=no` en systemd.
  - Medir el RTO: el simulacro sigue pendiente desde el hallazgo 3. Mientras no se mida, 4 horas y 15 minutos son objetivos.
  - Si el backend cae, la app no puede avisar por sí misma; el cliente sólo muestra que perdió la conexión.

### 10. Inventario concurrente
**Hoy.** Reservas al enviar a cocina, consumo al cobrar y recetas versionadas. Se valida existencia al reservar.
**Acciones.** Guardar versión de receta y costo en cada movimiento; movimientos inmutables con compensaciones; política explícita de existencias negativas; idempotencia de reservar, liberar y consumir ante reintentos.
**Comprobación.** Pruebas: última unidad pedida desde dos terminales a la vez, cancelación parcial de comanda preparada, devolución de una venta antigua y conciliación de compras, ventas, mermas y ajustes.

**Estado (30 de octubre de 2026): hecho.**
- Ya estaba bien, y ahora tiene prueba: reservas atómicas, receta congelada al enviar, movimientos inmutables, liberación idempotente y consumo una sola vez.
- Hecho:
  - Cada movimiento de venta y de merma preparada guarda su versión de receta, además del costo.
  - `stock.return` admite devolución parcial, con costo proporcional y sin superar lo consumido. Antes, una venta de tres porciones sólo podía devolverse completa.
  - `InventoryBalance`: cada 6 horas compara existencias contra movimientos, capas y reservas, y avisa al gerente si no cuadran.
  - Política de existencias negativas, explícita: no hay. Está en `docs/ARQUITECTURA.md`.
- Pruebas (`--inventory-only`): última unidad desde dos terminales a la vez, reintento idempotente, cancelación parcial de una comanda preparada, devolución de una venta anterior, y conciliación tras compra, venta, merma, conteo, traspaso y devolución. También detecta una existencia alterada.
- Falta: en interfaz, la conciliación sólo se ve como aviso; no hay pantalla con el detalle, que queda en el journal.

### 11. Política monetaria y fiscal
**Acciones.** Documento único de redondeo (centavos enteros, IVA incluido, reparto de residuos en divisiones y descuentos); funciones centralizadas; trazabilidad venta → pago → devolución → CFDI.
**Comprobación.** Pruebas de propiedades (FsCheck o generador propio): la suma de partes siempre es igual al total, en divisiones, descuentos, propinas y pagos mixtos.

**Estado (30 de octubre de 2026): código hecho, falta la nota de crédito y la revisión contable.**
- Hecho:
  - `docs/POLITICA-MONETARIA.md`, documento único, y `src/MesaNexo.Core/Cents.cs`, funciones centralizadas. Los importes de venta, descuentos, promociones, comisiones, recompensas y CFDI ya no redondean por su cuenta. El comportamiento no cambió. El costo de inventario sigue en sus propias funciones, porque no es un importe de venta.
  - `payment.refund`: devolución como registro inmutable que apunta al pago. En efectivo sale de la caja y puede ser parcial. Con un proveedor en vivo devuelve el cargo completo por la ruta que ya existía. Lo demás sólo se registra. Esto cierra el reverso pendiente del hallazgo 4.
  - `visit.trail`: una venta completa, de unidades a pagos, cargos, devoluciones y CFDI.
  - El corte muestra las devoluciones del periodo.
- Pruebas (`--money-only`, semilla fija, generador propio):
  - 50,000 repartos aleatorios: suman el total y cada parte queda a menos de un centavo de su proporción.
  - 40 cuentas aleatorias en la app real, con IVA de 0, 8 y 16 %, descuentos, divisiones, propinas y pagos mixtos: partes, saldo y CFDI coinciden con el ticket, y la caja cierra al centavo con devoluciones.
  - `--live-only`: devolución real por el simulador de Mercado Pago.
- Falta:
  - **No hay botón.** La devolución y el rastro son acciones del dominio sin pantalla. La interfaz va con la segunda aprobación del hallazgo 12, que debe protegerlas.
  - **Nota de crédito (CFDI de egreso).** Una devolución sobre una venta timbrada queda marcada `creditNote: pending`, pero el sistema no la emite.
  - Revisión de un contador, incluido el IVA del envío, que hoy sale con tasa 0.

### 12. Separación de funciones del gerente
**Acciones.** Perfiles de autorización (supervisor con límites) y segunda aprobación arriba de un importe para reembolsos, cancelaciones y ajustes (umbral en preguntas). Registro de quién solicita, quién autoriza, motivo y resultado. Bloqueo de PIN por intentos y alertas.
**Comprobación.** Pruebas de escalamiento, PIN bloqueado, sesión revocada y operación fuera de límite.

**Estado (30 de octubre de 2026): hecho.**
- Ya existía: PIN con hash, bloqueo de 5 minutos tras 5 intentos y registro `pinauth`.
- Hecho:
  - Doble aprobación desde 1,000 MXN en devoluciones, cancelaciones, descuentos, cortesías, ajustes de caja y faltantes de conteo. La da otro gerente con su PIN. Una solicitud de un mesero decidida por un gerente ya tiene dos personas.
  - El umbral se cambia con **Doble aprobación**, en Caja, y siempre entre dos. Es la versión posible de «lo configura el dueño»: el rol `dueno` sólo ve la central y no opera en la sucursal.
  - Perfil de supervisor: un gerente con `approvalLimit` no autoriza por encima de ese importe, y nadie cambia su propio límite.
  - `pinauth` guarda quién pidió, quién autorizó, motivo, importe y resultado. Un PIN bloqueado genera un aviso al dueño.
  - **Devolver un pago** en Caja, en las dos interfaces. Cierra la pantalla pendiente de los hallazgos 4 y 11.
  - La retención de autorizaciones del hallazgo 8 también detiene `authorize.pin`; antes sólo frenaba la acción directa.
- Pruebas: `--approval-only`, con escalamiento, autoaprobación, PIN equivocado y bloqueado, límite de supervisor, cancelación, ajuste, conteo, cambio de umbral y sesión cerrada.
- Falta: dos gerentes con PIN en cada sucursal. Con uno solo, nadie puede autorizar por encima del umbral.

### 13. Fallos en cada frontera
**Acciones.** Id de correlación desde la interfaz hasta el almacén; errores tipados y tiempos límite; distinguir «no ejecutado» de «ejecutado con respuesta perdida» (ya existe en parte con `command.send` y el recibo IPC). Pruebas de caída del puente, del backend y de PostgreSQL antes y después del commit.
**Comprobación.** Por cada frontera, una sola transición financiera y una sola operación persistida.

**Estado (30 de octubre de 2026): hecho, salvo la caída real de PostgreSQL.**
- Ya existía, y es la base: el cliente guarda la intención en su bóveda antes de enviar, con un `operationId` que llega hasta el recibo del almacén. `api/operations/{id}` distingue «no consta» de «confirmada», y un reintento devuelve el primer resultado. Los tiempos límite también estaban: 10 s de conexión a la base y el del cliente HTTP.
- Hecho:
  - El identificador sirve de correlación: viaja en cada error de `/api/action` y en el log `COS-API`, y la app muestra su referencia en un resultado incierto.
  - Errores tipados: `code`, y `outcome`, que dice si el cliente puede dar por no ejecutada la operación (`not-executed`) o debe reconciliar (`unknown`).
- Pruebas (`--boundary-only`): un pago con el almacén fallando antes del commit, con la respuesta perdida después, y con un backend nuevo tras la caída. Siempre queda un solo pago, y la cadena y el outbox verifican. La caída del puente ya la cubría `hybrid.spec.ts` («lost-ipc»).
- Falta: matar PostgreSQL de verdad a mitad de una transacción. Lo probado es la pérdida de respuesta del almacén, no un apagón del proceso; encaja en el simulacro de la VM.

### 14. Versiones y migraciones
**Acciones.** Versión en el contrato de la API y de los eventos (`culinaryos-events-v1` ya existe); matriz de compatibilidad publicada; prueba de actualización desde 0.2.x y de reversión; respaldo automático antes de una migración destructiva.
**Comprobación.** Actualizar una copia realista con terminales antiguas conectadas y eventos pendientes, y revertir.

**Estado (30 de octubre de 2026): hecho, salvo la prueba con binarios 0.2.x reales.**
- Ya existía: migración explícita e idempotente, con `pg_dump` automático antes del cambio, y una prueba que restaura esa copia. Se niega con cuentas o sesiones abiertas, así que no hay terminales conectadas durante la migración.
- Hecho:
  - `src/MesaNexo.Core/Contract.cs`: API, esquema y eventos en un solo lugar. `api/identity` los publica, y el cliente pide actualizarse si la API no coincide, antes de enviar credenciales.
  - Un backend no queda listo sobre una base más nueva que él (`COS-SCHEMA-NEWER`). Antes, revertir sólo el programa corría sobre datos que no entiende.
  - `docs/COMPATIBILIDAD.md`: matriz, cuándo sube cada número y cómo actualizar y revertir.
- Pruebas (`SchemaTests`): la actualización conserva los eventos pendientes, la copia restaura el esquema anterior, y un backend rechaza una base futura.
- Falta: la matriz para 0.2.x sale de que el contrato no cambió. No se ha probado con un binario 0.2.x real ni sobre una copia de tamaño real.

## Fase 3 · Validación real y lanzamiento

- **15. Hardware e integraciones.** Matriz proveedor, modelo, sistema y versión. Sandbox de Clip, Mercado Pago Point, Stripe y Facturama; impresoras y cajones físicos; piloto controlado. Sólo se publica como compatible lo validado. Requiere cuentas y equipos.
- **16. Evidencia de pruebas.** CI en GitHub Actions con informe por commit (versiones, entorno, duración y resultados), etiquetas por riesgo (dinero, inventario, seguridad, restauración y sincronización) y cobertura de ramas en `MesaNexo.Core`.
  - **Estado (30 de octubre de 2026): escrito, sin ejecutar.** `.github/workflows/ci.yml` corre las baterías, y `tools/evidence_report.py` genera `evidence/INFORME.md` por riesgo, y falla si una suite no dejó evidencia. El informe se probó aquí; el flujo no, porque nada se ha subido. Falta correrlo la primera vez, sobre todo por el Python del laboratorio, y la cobertura de ramas, que no está incluida.
  - La suite web ya no reutiliza un servidor en el puerto 1420: mientras otra sesión tuvo su `vite preview` ahí, las pruebas pasaban contra el código de ese worktree.
- **17. Estado comercial.** Tabla por plataforma y función con cuatro estados: implementado, probado automáticamente, validado en real y habilitado comercialmente. Corregir el resumen ejecutivo.
  - **Estado (30 de octubre de 2026): hecho.** La tabla está en `docs/ESTADO-COMERCIAL.md`. Nada está validado en real ni habilitado. El resumen decía que la CA se crea fuera de línea; hoy la genera el instalador en el servidor, y quedó corregido.
- **18. Privacidad.** Inventario de datos personales, finalidades y plazos; exportación y eliminación por cliente; respuesta a incidentes. Revisión con asesoría jurídica mexicana del aviso de privacidad (ya hay un borrador en el escritorio).
  - **Estado (30 de octubre de 2026): lo técnico hecho, falta la asesoría.** `docs/PRIVACIDAD-DATOS.md` tiene el inventario, los plazos propuestos y la respuesta a incidentes. `customer.export` y `customer.erase` funcionan en las dos interfaces, con `--privacy-only`. La eliminación anonimiza los registros vivos; no alcanza el historial sellado ni los CFDI, y el documento lo explica. Faltan la purga automática a 90 días, la revisión legal y definir al responsable, que sigue como `[POR DEFINIR]` en el borrador.

## Informe del 2 de noviembre de 2026 · CR-001 a CR-016

Respuesta al «Informe de problemas detectados y plan de soluciones técnicas» de 0.3.0. Se traslapa con los hallazgos de arriba, así que cada CR apunta a lo que ya estaba hecho y agrega sólo lo que faltaba. Trabajo del 3 de noviembre de 2026, rama `auditoria-cr` de `MesaNexoNativo`, más commits locales en `CulinaryOS` (sin subir).

Decisiones del 3 de noviembre: sólo código y pruebas locales. Lo que pide hardware real, TPM o LUKS físico, contador, abogado o cuentas queda **BLOCKED** con lo que falta. La purga de datos personales queda programada y apagada.

Estados: `PENDING`, `IN_PROGRESS`, `IMPLEMENTED` (hay código, falta validarlo), `TESTING`, `BLOCKED` (depende de algo externo) y `VERIFIED` (implementado y con evidencia). Ningún CR llega a `VERIFIED`: el criterio del informe pide validar en un entorno equivalente a producción, y aquí sólo hay pruebas locales.

| ID | Problema | Estado | Hecho | Falta |
|---|---|---|---|---|
| CR-001 | Servidor único | BLOCKED | Hallazgos 3 y 9: kit de recuperación, `culinaryos-restore`, copia externa cada 15 min, guía de sustitución | Simulacro en VM y en equipo físico, medir RTO y RPO reales, buckets R2 |
| CR-002 | Crecimiento del outbox | IMPLEMENTED | Hallazgo 2. Nuevo: disco al 60, 75, 85 y 95 %, proyección de días hasta el 95 %, reserva liberable a mano (`health.py --release-reserve`), exportar e importar pendientes sellados (`--export-outbox`, `--import-outbox`) | Simulacro de disco lleno en la VM |
| CR-003 | Pagos inciertos y reversos | IMPLEMENTED | Hallazgos 4, 11 y 12. Nuevo: una devolución por proveedor queda pendiente hasta que el proveedor confirma; fallida no cuenta y se puede pedir otra vez; la que el proveedor deja a mano la cierra un gerente con la referencia (`refund.settle`). El corte y el rastro separan devuelto de pendiente | Sandbox reales de Clip y Mercado Pago; métricas del dueño (`OwnerStats`) siguen contando la devolución al pedirla, porque comparten vectores con CulinaryServer |
| CR-004 | Cifrado del almacenamiento | BLOCKED | Registros sellados con AES-GCM bajo el llavero | LUKS con TPM y Secure Boot en el equipo de la sucursal, prueba de extracción del disco, custodia y rotación de la llave de recuperación |
| CR-005 | Cadena de confianza | BLOCKED | Hallazgo 1: raíz fuera de línea, intermedias por uso, piso de llaves, desplegado el 31 de octubre | Llevar `root-private.txt` y la intermedia de paquetes fuera del equipo; prueba de pérdida de una intermedia contra el Worker real |
| CR-006 | Restauración incompleta | BLOCKED | Hallazgo 3: el kit lleva llavero, identidad, CA y credenciales; `--verify-store` comprueba cadena y outbox | El simulacro en otro equipo |
| CR-007 | Eventos que bloquean la sincronización | IMPLEMENTED | La central responde por clase (temporal, autenticación, validación, conflicto, incompatible, acuse). Red y servidor esperan con espera exponencial; cinco rechazos del mismo evento avisan al gerente; soporte aparta o libera con motivo (`--outbox-list`, `--outbox-intervene`), con registro en `outbox_interventions`. Nada se omite ni se borra solo | Probar contra la central real |
| CR-008 | Rendimiento con PostgreSQL | TESTING | `MesaNexo.Load` ya corría sobre PostgreSQL, pero el informe seguía diciendo SQLite; ahora registra CPU, memoria del backend y de PostgreSQL y el tamaño de la base. En un equipo de desarrollo (8 núcleos, 32 GB, Windows) y con backend, PostgreSQL y clientes en la misma máquina: **sostenida**, 15 min con 6 meseros, 2 cocina y gerente en 24 mesas: 2,707 cuentas cerradas, 0 errores en 36,243 solicitudes, acciones p50/p95/p99 de 12/36/57 ms, estado 31/69/93 ms, CPU 4.8 % de media y 9 % de máximo, base de 18 a 297 MiB. **Pico**, 5 min con 12 meseros en 30 mesas y el doble de ritmo: 2,733 cuentas, 0 errores, acciones 26/61/92 ms, estado 32/55/73 ms, CPU 13 % de media y 19.5 % de máximo. En las dos la integridad cuadra al centavo, sin unidades duplicadas y con la bitácora válida (`evidence/load-postgresql-*.json`). Se vio: el backend llega a 1.3 GB de memoria y algunas solicitudes aisladas tardan 2.1 s | Repetir en el hardware de la sucursal; fijar los objetivos de rendimiento antes del piloto |
| CR-009 | Actualizaciones interrumpidas | IMPLEMENTED | `apply.py` es una transacción con bitácora y copia de la versión anterior; vuelve atrás si dpkg falla, si se va la luz a medio instalar (`culinary-update-recover`) o si el backend no queda listo tras reiniciar (`culinary-update-health`). 11 pruebas cortan cada etapa | Probarlo en la VM; la ISO todavía no copia las dos unidades nuevas (el primer .deb las instala) |
| CR-010 | Privilegios administrativos | BLOCKED | Panel con contraseña, TOTP y llave de equipo; paquetes firmados fuera del Worker con su propia intermedia; auditoría con `kid` | Decisión del 3 de noviembre: la segunda aprobación la dará otra persona, que creará su cuenta de administrador cuando todo esté listo para lanzar. Falta programar en CulinaryServer y CulinaryAdmin la aprobación por un segundo administrador para las operaciones de alto impacto, activa en cuanto exista esa segunda cuenta, y crearla antes del lanzamiento |
| CR-011 | Aislamiento multiempresa | IMPLEMENTED | Pruebas adversariales por la entrada mTLS: sitio suplantado, proyección hacia otra sucursal, certificados vencidos, sin uso de cliente o no aprovisionados, digest alterado, id de evento reutilizado, colas por sucursal y tokens inventados o cerrados | Repetir contra la central desplegada; los buckets R2 por sucursal se validan al crearlos |
| CR-012 | CFDI y notas de crédito | BLOCKED | Hallazgo 11: la devolución sobre venta timbrada queda `creditNote: pending` | Reglas fiscales aprobadas por un contador; luego el CFDI de egreso |
| CR-013 | Retención de datos | IMPLEMENTED | Purga por plazo de reservaciones, llamadas, mensajes y pedidos de plataforma, apagada hasta que `/etc/culinaryos/privacy.json` la encienda (`docs/PRIVACIDAD-DATOS.md`) | Revisión legal de los plazos y encenderla |
| CR-014 | Rotación de certificados | IMPLEMENTED | `rotate-ca.py` anuncia, cambia y retira (30 días de transición, bitácora y vuelta atrás). El backend publica las CA en `api/trust`; las terminales las aprenden al entrar y sólo adoptan un conjunto que conserve una CA conocida | Rotar en la VM con una terminal real; el cliente web del kiosco usa el perfil del sistema, que actualiza el propio script |
| CR-015 | Validación de hardware | BLOCKED | Matriz de dispositivos en `docs/ESTADO-COMERCIAL.md` | Llenarla con equipos físicos |
| CR-016 | CI y trazabilidad | IN_PROGRESS | CI corre en cada push y PR de `MesaNexoNativo` y sube la evidencia por commit. Se publican `d8fc1ca` y `060c556` con esta rama | Protección de ramas y PR obligatorio (configuración de GitHub); CI en `CulinaryOS`, `CulinaryServer` y `CulinaryAdmin`, que siguen con push directo |

Las pruebas nuevas están en `--store-only` (CR-007), `--live-only` (CR-003), `--tenant-only` (CR-011), `--privacy-only` (CR-013), `--setup-only` y `test_culinaryos_install.py` (CR-014), `test_update_apply.py` (CR-009) y `CulinaryOS/tests/test_diagnostics.py` (CR-002).

## Criterios para autorizar el piloto

Los de la auditoría, cada uno con su prueba o simulacro de la Fase 1 y la evidencia guardada en `evidence/`.
