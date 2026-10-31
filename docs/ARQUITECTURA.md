# Arquitectura

## Autoridad y separación

El backend de cada sucursal es la única autoridad de mesas, cuentas, unidades de cocina, pagos manuales, caja, personal y permisos. Los clientes contienen interfaz, conexión TLS, credencial de dispositivo y cola local de solicitudes; nunca contienen llaves maestras del almacén. La central mantiene contratación, paquetes, soporte y espejo de eventos, sin sustituir la autoridad local. El proveedor no obtiene acceso operativo por ser propietario comercial.

Desde la versión con PostgreSQL, los datos de la sucursal viven en el PostgreSQL 17 de Debian que trae CulinaryOS. Sólo se accede por el socket Unix local con autenticación peer, como la cuenta de servicio `culinaryos-app`. `PostgresBranchStore` (`src/MesaNexo.Api/PostgresStore.cs`) responde las mismas operaciones que el almacén SQLite de CulinaryOS con su extensión `runtime/mesanexo_ipc.py`. Antes de escribir, sella cada registro y cada evento con AES-256-GCM bajo el anillo de la sucursal, que llega por la credencial de systemd `keyring`: un volcado de la base no contiene datos legibles. El backend envía el outbox a la central (`CentralSyncWorker`). Los eventos pendientes nunca se borran por antigüedad; los confirmados pasan a `events_archive`. Una sucursal que ya corría sobre SQLite se migra una sola vez con `runtime/export_store.py`, mediante un pipe a `--migrate-from-export`. Los registros se separan por entidad `mn_<tipo>_<id>`, con `mn_meta_site` como barrera CAS. No se guarda todo el negocio en un único JSON de estado.

## Confirmación, concurrencia y red

Cada mutación lleva UUID, actor y huella del contenido. Registro, revisión, recibo cifrado, auditoría y evento outbox se confirman en la misma transacción de PostgreSQL, serializada con un candado de escritura. Una repetición devuelve el recibo previo; reutilizar UUID con otro contenido se rechaza. El backend reintenta conflictos CAS contra estado actualizado. El cliente persiste la solicitud antes de enviarla; si pierde la respuesta, conserva su UUID y consulta el recibo antes de permitir nuevas mutaciones. Un estado incierto no se muestra como éxito.

El listener HTTPS de sucursal mantiene el nombre TLS aunque el descubrimiento encuentre una IP. Se comprueban cadena, nombre, vencimiento y raíz aprovisionada; no hay opción de aceptar cualquier certificado. El alta de dispositivo usa un código de un solo uso; las sesiones caducan y se pueden revocar. Contraseñas Argon2id. El descubrimiento no concede confianza ni credenciales. Windows permite dirección manual; Linux integra el descubridor existente, Android NSD e iOS Bonjour tienen adaptadores nativos.

La operación local no requiere central ni Internet. Su disponibilidad se muestra por separado; no se infiere Internet a partir de la LAN. El worker original es el único emisor de outbox. La central requiere mTLS para sucursales y solo devuelve ACK después del commit durable con deduplicación por sucursal/evento. El refresco de política es un canal separado, firmado y verificado localmente; no envía el outbox.

El outbox nunca descarta un evento pendiente, tenga la antigüedad que tenga y haya o no central en `/etc/culinaryos/branch.json`. Una central conectada después recibe la historia completa, transacción por transacción.
- **Entrega y acuses.** `CentralSyncWorker` envía los pendientes del más antiguo al más nuevo y deja de enviar en el primero que falla. Guarda los acuses por lote (hasta 16) en una sola transacción, junto con la última secuencia confirmada (`outbox_acked_seq`). Un lote que dejaría atrás un evento anterior sin confirmar se rechaza completo. Si un lote se pierde por una caída, la central vuelve a recibir esos eventos y responde con el mismo commit.
- **Archivo.** Un evento con ACK de hace más de 7 días pasa de `events` a `events_archive`, unos cuantos por transacción. No se borra: `events + events_archive` siempre suma el número de transacciones confirmadas.
- **Alerta.** `outbox_status_v1` y `outbox_maintain_v1` responden `alert`: `size` si la tabla `events` pasa de 2 GiB, `age` si hay central configurada y el pendiente más antiguo tiene más de un día. `OutboxWorker` lo registra como `COS-OUTBOX-SIZE` o `COS-OUTBOX-AGE` y la barra de estado del cliente lo muestra. La alerta nunca recorta el outbox.
- **Línea base (`mesa-nexo-baseline-v1`).** Sólo existe para sucursales migradas desde SQLite cuya extensión anterior descartó pendientes sin central (`standalone-dropped`). Al configurar la central, `outbox_maintain_v1` encola en pasos acotados todos los registros `mn_` en su versión actual, auditoría incluida, en eventos de 56 KiB como máximo. La central la proyecta con su upsert por versión, así que el orden respecto a los eventos retenidos no importa. Lo que la línea base no recupera es la secuencia de las transacciones descartadas. Sólo queda su resultado final y el rastro before/after de la auditoría. `event_status_v1` responde `untracked` para esos eventos. Un almacén que nunca descartó eventos no necesita línea base.
- **Extensión SQLite.** `runtime/mesanexo_ipc.py` ya no está en la ruta de datos de una sucursal PostgreSQL. CulinaryOS la sigue enganchando al servicio `culinaryos-data`, que en el appliance sólo responde `health`, y sirve para el laboratorio y las pruebas de contrato. Tampoco descarta pendientes.

## Puntos de control de la bitácora

La cadena de auditoría detecta un cambio aislado, pero quien tenga todas las llaves locales puede reescribir un registro y recalcular la cadena. Por eso `CheckpointWorker` firma la cabeza cada hora y en cada cierre de caja, y la publica en un bucket de Cloudflare R2 con credenciales propias, separadas de CulinaryServer.

- **Por qué no se puede borrar lo publicado.** R2 no tiene tokens de sólo escritura. La protección es un bucket lock, que rechaza sobrescribir o borrar durante el plazo. El nombre del objeto incluye secuencia y hash: una segunda cabeza para la misma secuencia queda junto a la primera.
- **Quién compara.** La sucursal, al arrancar y a diario. Si no coincide, avisa al gerente, detiene las autorizaciones con PIN y deja evidencia en `alerts/`. La venta sigue. Un atacante con root puede apagar esa revisión local, así que la que cuenta es la externa: `--export-audit` en la sucursal y, en otro equipo, `--verify-checkpoints bitacora.json --checkpoints-dir copia-del-bucket --site SITIO`, que no usa base de datos ni llaves.
- **Restauraciones.** Una copia anterior a lo publicado también diverge. `--accept-checkpoints "motivo"` declara esos objetos en `accepted/`. La declaración queda en el bucket y el verificador externo la lista, porque cada una debe corresponder a una restauración real.

Cada sucursal lleva su propio bucket, porque los tokens de R2 se limitan por bucket, y sus credenciales van en `/etc/culinaryos/integrations/checkpoints.json` (ver `deploy/integrations/checkpoints.example.json`). Para crear uno:

```bash
npx wrangler r2 bucket create culinary-bitacora-SITIO
npx wrangler r2 bucket lock add culinary-bitacora-SITIO --name retencion --retention-indefinite
```

## Límites explícitos

IPC original: tramas de 128 KiB y eventos de 64 KiB. La extensión limita transacciones a 56 KiB, 256 cambios y páginas a 32 registros/~100 KiB. La importación rechaza registros mayores de 40 KiB, incluidas imágenes inline grandes; deben externalizarse en una copia hacia HTTPS de confianza. No se truncan datos. Operaciones demasiado grandes se rechazan y requieren dividir el trabajo.

La vista operativa incluye cuentas abiertas y hasta 500 cerradas recientes; informes históricos tienen paginación de 100. La carga interna recorre páginas hacia memoria: no se ha medido rendimiento con grandes volúmenes. No se implementó distribución automática de CRL ni renovación de certificados. No se evaluó seguridad mediante auditoría externa.

## Cliente React/CSS y Tauri (16 de octubre de 2026)

La etapa Windows utiliza `MesaNexo.Web` (React/CSS empaquetado), Tauri/WebView2 y `MesaNexo.Bridge` por pipes privados. `MesaNexo.Transport` compila el transporte existente sin Avalonia. El servidor y la autoridad de datos conservan su arquitectura. El recibo queda protegido hasta el ACK del renderer; no se permite otra mutación mientras falte ese reconocimiento. Consulta [MIGRACION-TAURI.md](MIGRACION-TAURI.md) para instrucciones, pruebas y límites actuales por plataforma. Avalonia permanece como compatibilidad y cliente móvil durante la transición.

## Plataformas y actualizaciones anteriores

Avalonia dibuja controles nativos sin motor web; diseño grafito/ámbar adaptable, controles táctiles, foco y Escape. Windows protege secretos con DPAPI CurrentUser; Linux requiere Secret Service desbloqueado; Android Keystore AES-GCM; iOS Keychain. No hay fallback de credenciales en texto plano. El cierre de la interfaz no detiene el backend.

Las publicaciones verifican firma RSA-PSS, destinatario, plataforma, esquema, versión, vencimiento y SHA-512 del archivo. La instalación requiere cuentas cerradas y sesiones operativas cerradas. Estas comprobaciones se probaron con un archivo fixture, no con un ciclo real de actualización. La firma del manifiesto no reemplaza Authenticode ni las firmas de las tiendas móviles.

## Ampliación DEVMap — 0.2.0

Flujo local: cliente Avalonia → HTTPS/SignalR autenticado → BranchService/Restaurant → RuntimeStore IPC → transaction_v1 → Store cifrado, recibo y outbox atómicos. Sólo después del commit se confirma y emite pista de revisión a un grupo SignalR del sitio autorizado. Al reconectar se relee snapshot consistente. El catálogo protegido local sirve de consulta; no crea autoridad offline sin servidor.

InventoryWorker procesa stockjobs creados en el cierre o merma dentro de la misma transacción comercial. Reserva antes de cocina, cantidades/versiones congeladas, CAS y avance durable por ingrediente/capa. Ningún conector escribe SQL operativo. La impresión reclama estado incierto antes del posible efecto físico; un envío a spooler no confirma papel ni permite volver a cobrar.

Central conserva el receptor/outbox existente y añade tablas tenant/branch/membership/session/mirror/order/command, FK compuestas e índices. El cifrado de los payloads incluye tenant/branch/kind/id. Autorización se recalcula del lado servidor para el usuario y dispositivo. Alta mediante administración local, no mediante tenant_id enviado por el navegador.

PWA corre en MesaNexo.Public, proceso HTTPS distinto y loopback, sobre la central aprovisionada. Un proxy administrado sería necesario para exposición exterior, que no se realiza aquí. No contiene rutas privadas de sucursal. Checkout calcula precios en servidor y crea propuesta; conexiones salientes mTLS reciben sobres RSA-PSS, los almacenan pendientes y sólo reconocen la decisión autenticada del gerente después del commit local. No hay un nuevo puerto entrante en CulinaryOS ni un segundo remitente de outbox.

MesaNexo.Integrations es biblioteca de servidor, no referenciada por clientes. PAC, pago, push y hub se mantienen aislados. PrintDriver/Telephony/FiscalProcessing tampoco tienen rutas ni comandos de activación. La cola de avisos al dueño es durable aunque no haya proveedor de envío.

La auditoría concatena SHA-256 de antes/después y ancla la cabeza en meta, preservando legado. Detecta alteración respecto de esas anclas; no promete inmutabilidad absoluta. Se requieren anclas independientes y custodia para amenazas de administrador.
