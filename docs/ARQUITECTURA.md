# Arquitectura

## Autoridad y separación

El backend de cada sucursal es la única autoridad de mesas, cuentas, unidades de cocina, pagos manuales, caja, personal y permisos. Los clientes contienen interfaz, conexión TLS, credencial de dispositivo y cola local de solicitudes; nunca contienen llaves maestras del almacén. La central mantiene contratación, paquetes, soporte y espejo de eventos, sin sustituir la autoridad local. El proveedor no obtiene acceso operativo por ser propietario comercial.

CulinaryOS conserva sus servicios, SQLite cifrado, XChaCha20-Poly1305, llaveros, identidad, snapshots y emisor de outbox. `runtime/mesanexo_ipc.py` es una extensión explícita del protocolo local; no se atribuyen al IPC original capacidades que no tenía. Los registros se separan por entidad `mn_<tipo>_<id>`, con `mn_meta_site` como barrera CAS. No se guarda todo el negocio en un único JSON de estado.

## Confirmación, concurrencia y red

Cada mutación lleva UUID, actor y huella del contenido. Registro, revisión, recibo cifrado, auditoría y evento outbox se confirman en la misma transacción SQLite. Una repetición devuelve el recibo previo; reutilizar UUID con otro contenido se rechaza. El backend reintenta conflictos CAS contra estado actualizado. El cliente persiste la solicitud antes de enviarla; si pierde la respuesta, conserva su UUID y consulta el recibo antes de permitir nuevas mutaciones. Un estado incierto no se muestra como éxito.

El listener HTTPS de sucursal mantiene el nombre TLS aunque el descubrimiento encuentre una IP. Se comprueban cadena, nombre, vencimiento y raíz aprovisionada; no hay opción de aceptar cualquier certificado. El alta de dispositivo usa un código de un solo uso; las sesiones caducan y se pueden revocar. Contraseñas Argon2id. El descubrimiento no concede confianza ni credenciales. Windows permite dirección manual; Linux integra el descubridor existente, Android NSD e iOS Bonjour tienen adaptadores nativos.

La operación local no requiere central ni Internet. Su disponibilidad se muestra por separado; no se infiere Internet a partir de la LAN. El worker original es el único emisor de outbox. La central requiere mTLS para sucursales y solo devuelve ACK después del commit durable con deduplicación por sucursal/evento. El refresco de política es un canal separado, firmado y verificado localmente; no envía el outbox.

El outbox no crece sin límite. Un evento con ACK de la central se conserva 7 días y luego se borra (la central ya lo comprometió). Si `/etc/culinaryos/branch.json` no configura central, nadie confirmará nunca los eventos: los pendientes se conservan 7 días y luego se descartan. Los registros, recibos y auditoría locales no se tocan. Al configurar una central después, `outbox_maintain_v1` (llamado por `OutboxWorker`) encola primero una línea base `mesa-nexo-baseline-v1`: todos los registros `mn_` en su versión actual, auditoría incluida, en eventos de 56 KiB como máximo. La central la proyecta con su upsert por versión, así que el orden respecto a los eventos retenidos no importa. Lo único que la central no recibe es la secuencia transacción por transacción anterior a la conexión. Sin `branch.json` legible no se descarta nada. Un almacén que nunca descartó eventos no necesita línea base.

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
