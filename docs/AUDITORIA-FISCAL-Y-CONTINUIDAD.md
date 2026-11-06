# Auditoría fiscal, CSD, terminales y continuidad (6 de noviembre de 2026)

Revisión del pedido «hardening» en cinco pilares fiscales, el módulo de terminales bancarias y tres preguntas de operación. Abarca cuatro repositorios:

| Repositorio | Qué cambió | Estado |
|---|---|---|
| MesaNexoNativo (sucursal) | Recepción cifrada del CSD, validación, carga en Facturama, bitácora de timbrado, estado de factura del ticket, catálogos SAT del receptor | Con commit en `auditoria-cr`, sin push |
| CulinaryServer | Datos fiscales obligatorios en la licencia, relevo cifrado del CSD, rutas sólo para el dueño | Con commit en `main`, **sin push** (un push despliega a producción) |
| CulinaryAdmin | Campos fiscales en el alta y la edición de licencias | Con commit, sin compilar ni instalar |
| CulinaryDueño | Sección «Facturación», sólo para el dueño, que cifra los sellos en el equipo | Sin commit: el repositorio tiene el diseño v3 de otra sesión sin confirmar |

Decisiones del usuario (6 de noviembre): el CSD vive **sólo en el PAC**; los datos fiscales se exigen en licencias **nuevas** y en códigos de activación nuevos; la configuración fiscal se hace **sólo desde Culinary Dueño**.

## Las tres preguntas

### 1. ¿Se recuperan las operaciones tras un reinicio forzado?

Sí, sin perder lo confirmado. Lo que ya estaba:

- **Durabilidad.** PostgreSQL de CulinaryOS corre con `fsync = on`, `synchronous_commit = on` y `full_page_writes = on` (`CulinaryOS/os/overlay/etc/postgresql/17/main/conf.d/culinaryos.conf`). Un commit confirmado sobrevive al corte de luz.
- **Atomicidad.** Cada mutación (comanda, cobro, cierre) escribe registro, recibo, auditoría y evento outbox en una sola transacción. O quedó todo o no quedó nada.
- **Respuesta perdida.** El cliente guarda el UUID de la operación antes de enviarla. Si se cae a mitad, al volver consulta el recibo de ese UUID y no repite la operación.
- **Comandas y cocina.** Las comandas son registros del almacén: al volver, el KDS y las terminales releen el estado.
- **Trabajos a medias.** Inventario sigue por avance durable. Un timbrado que quedó «enviando» pasa a `pending-reconciliation` y nunca se manda dos veces. Un cobro con terminal sin respuesta queda `uncertain` y bloquea otro cobro en esa cuenta. Una impresión incierta pide revisión.

Lo que falta: el **simulacro real de corte eléctrico** sigue pendiente desde 0.2.0 (`docs/RECUPERACION.md`). La guía de UPS está en `CulinaryOS/docs/UPS-Y-APAGONES.md`.

### 2. ¿Puede cambiar el equipo que hospeda todo si el principal se apaga?

**No, y es a propósito.** La sucursal tiene una sola autoridad: un solo PostgreSQL, el llavero de la sucursal (credencial de systemd) y la licencia ligada al TPM o a la huella de **ese** equipo. Si se apaga, las terminales conservan su cola pero no venden: el catálogo local sólo sirve de consulta y no crea autoridad sin servidor.

Hoy se cambia de equipo a mano: kit de recuperación (`culinaryos-recovery-kit` y `culinaryos-restore`) y liberar la licencia desde el panel (`reset-activation`). Son minutos a horas.

Una elección automática de líder no conviene en un POS: dos servidores aceptando cobros a la vez (cerebro dividido) duplican folios y descuadran la caja. Lo viable es un **respaldo en caliente con promoción manual de un botón**:

1. Réplica de PostgreSQL por streaming (`wal_level = replica` ya está puesto).
2. Promoción manual con «fencing»: el principal debe estar apagado antes de promover.
3. Licencia con dos equipos autorizados, o traspaso automático firmado por CulinaryServer.
4. Custodia del llavero también en el equipo de respaldo.
5. El nombre TLS se mueve al nuevo equipo. Los clientes ya validan nombre, no IP, así que el cambio de IP no rompe la confianza.

No está implementado. Es trabajo aparte.

### 3. ¿Puede un celular, tableta, laptop o PC sin CulinaryOS ser el servidor local?

**Para producción, no.** El backend .NET es multiplataforma y ya corre en Windows en modo laboratorio (`tools/Start-Lab.ps1`, con PostgreSQL privado en `.lab`), pero producción depende de CulinaryOS:

- credenciales de systemd (llavero, llave TLS, código de alta);
- PostgreSQL local con autenticación peer por socket Unix;
- el servicio root que lee el TPM o la huella del equipo;
- actualizaciones `.deb` y el modo kiosco.

| Equipo | ¿Servidor? | Por qué |
|---|---|---|
| Celular o tableta (Android/iOS) | No | El sistema mata los servicios en segundo plano, no hay PostgreSQL y la app es sólo cliente |
| Laptop o PC con Windows | Hoy no; posible a futuro | Faltaría un servicio de Windows, la llave en el TPM con CNG, PostgreSQL para Windows y la custodia del llavero con DPAPI |
| Laptop o PC con CulinaryOS en máquina virtual | Sí | `docs/CULINARYOS-INSTALACION.md` (VirtualBox). Sin TPM pasado a la VM, la licencia se liga por huella |

## Archivo 1: facturación CFDI 4.0 y CSD

### Pilar 1. Licencia con datos fiscales obligatorios

**Antes:** la licencia sólo tenía cliente y sucursal. Se podía crear y activar sin RFC.

**Ahora** (CulinaryServer `src/sat.ts`, `src/fiscal.ts`, `migrations/0010_datos_fiscales.sql`):

- **RFC:** forma de 12 o 13 caracteres, fecha real, dígito verificador del SAT (módulo 11) y sin los genéricos `XAXX010101000`/`XEXX010101000`.
- **Razón social:** se pasa a mayúsculas y espacios simples. Se **rechaza** (no se recorta) si trae régimen societario (S.A. de C.V., S. de R.L., S.C., A.C., S.A.P.I., S.A.S.), para que nadie adivine.
- **Régimen fiscal:** catálogo `c_RegimenFiscal`, según aplique a persona moral o física. Se excluyen 605, 611, 614, 615 y 616, con los que no se facturan ventas.
- **Código postal fiscal:** 5 dígitos y no `00…`.
- **Alta atómica:** licencia, cobro y bitácora en un solo `batch` de D1. Si algo falla no queda una licencia a medias; antes eran tres escrituras separadas.
- **Activación y códigos nuevos:** `/branch/v1/activate` y `reset-activation` exigen los datos fiscales completos (`409 fiscal_incomplete`). Una licencia ya activa sigue validando, y el panel la marca «Incompletos».
- **Licencia firmada:** el JWS lleva `fiscal`. La sucursal compara contra ese RFC el del CSD que manda el dueño.

**Límites:**

- La coincidencia con la Constancia de Situación Fiscal es de **forma**: no se consulta al SAT. La comprobación fuerte llega con el CSD, cuyo RFC debe ser el de la licencia.
- El catálogo completo `c_CodigoPostal` (unos 95 000 códigos) no se incrustó. El PAC lo valida al timbrar.

### Pilar 2. Configuración fiscal exclusiva del dueño

- **Dónde:** la sección «Facturación» de Culinary Dueño (`src/fiscal.tsx`), separada del alta de licencia.
- **Quién:** sólo una cuenta con rol `owner`. El menú no la muestra a un gerente, y el servidor responde `403` a `GET /owner/v1/fiscal` y a `POST /owner/v1/fiscal/:licenseId/csd` aunque se llame directo. Un dueño de otro negocio recibe `404`.
- **En la sucursal no hay ruta** para cargar sellos: ni un gerente local puede hacerlo.
- **Validación antes de cargar** (`src/MesaNexo.Api/FiscalIntake.cs`, `CsdCheck`):
  - el `.cer` es un certificado X.509 con el RFC en `x500UniqueIdentifier`;
  - el RFC es el de la licencia firmada;
  - está vigente (ni vencido ni futuro);
  - es CSD y no e.firma: se rechaza el uso de llave `keyAgreement` o `dataEncipherment`;
  - en producción, el emisor es el SAT;
  - la contraseña abre el `.key` (PKCS#8 con PBES2 y 3DES, como los entrega el SAT);
  - el `.key` firma algo que el `.cer` verifica, o sea que son par.

### Pilar 3. Hardening del CSD

| El pedido | Lo implementado | Por qué |
|---|---|---|
| Guardar `.cer`/`.key` en un volumen aislado o en un Key Vault | **No se guardan.** Se cargan en Facturama (`PUT TaxEntity/UploadCsd`) y se descartan | Lo que no se guarda no se puede filtrar. Facturama necesita el CSD de todos modos para sellar |
| Contraseña con AES-256-GCM, IV dinámico y llave maestra en variables de entorno | La contraseña **viaja** dentro de un sobre AES-256-GCM con IV aleatorio, y la llave sale de un ECDH efímero, no de una llave maestra | No existe una llave maestra que robar. Las variables de entorno se leen en `/proc/<pid>/environ` y quedan en volcados; una llave por envío no |
| Responder sólo «Sellos cargados con éxito» y la fecha de vencimiento | `GET /owner/v1/fiscal` devuelve `loaded`, RFC, número de certificado y vigencia | El número de certificado y el RFC salen en cada CFDI; no son secretos |
| No permitir descargar el `.key` | No existe ninguna ruta que lo devuelva; para cambiarlo se envía otro | — |

**El sobre** (`IntakeEnvelope` en la sucursal, `src/csd.ts` en Dueño):

- La sucursal genera una llave ECDH P-256 en su directorio de licencia (archivo 0600) y la registra con una petición firmada por su llave de licencia (TPM o archivo).
- Dueño cifra con WebCrypto: ECDH efímero, HKDF-SHA-256 con sal aleatoria de 32 bytes, AES-256-GCM con IV de 12 bytes y etiqueta de 128 bits.
- El dato asociado es `culinary-csd-v1|licencia|envío|sha256-de-la-llave`: un sobre no se abre para otra licencia, otro envío ni otra llave.
- CulinaryServer guarda sólo el texto cifrado, como máximo 24 horas, y lo borra en cuanto la sucursal responde. Su bitácora registra quién envió y qué resultó, nunca el contenido.
- La sucursal pone en ceros los arreglos de bytes al terminar. Las cadenas de .NET no se pueden borrar: la contraseña vive en memoria administrada lo que dura la llamada a Facturama.

**Riesgo residual:** un CulinaryServer comprometido podría cambiar la llave de recepción por la suya antes de que el dueño cifre. Para cerrarlo, el dueño tendría que comprobar la huella de la llave fuera de banda (por ejemplo, en la pantalla de la sucursal). Queda como mejora.

### Pilar 4. Facturación híbrida

- **En caja** (ya existía): `fiscal.request` → `FiscalWorker` en segundo plano → Facturama `3/cfdis` → UUID guardado.
  - Un envío sin respuesta nunca se repite solo: el gerente reintenta con el mismo folio y fecha.
  - Facturama manda el PDF y el XML por correo si `emailReceiver` está activo.
  - El gerente descarga el PDF desde la pantalla fiscal para imprimirlo o reenviarlo (`GET /api/fiscal/{id}/pdf` → Facturama `GET cfdi/pdf/issued/{id}`). Se pide a Facturama cada vez y no se guarda en el almacén: un PDF suele pasar de los 40 KiB por registro.
- **Autofactura** (ya existía): token aleatorio de 32 bytes, guardado como SHA-256, vigente 24 horas, en el portal público. La central lo consume una sola vez (`invoice_capability`).
  - Con portal configurado, liquidar una cuenta encola su ticket. Al imprimirse se emite el token y se dibuja el QR (ESC/POS `GS ( k`, modelo 2, corrección M). El token sólo existe en el papel; en el almacén queda su hash. Un ticket facturado o en proceso sale sin QR. Una reimpresión autorizada lleva un token nuevo.
- **Estado de facturación del ticket** (nuevo): cada cuenta trae `invoice` = `none` (No facturado), `processing` (En proceso) o `invoiced` (Facturado), derivado de sus solicitudes fiscales.
- **Doble timbre:** bloqueado en caja y en web, con prueba. Corregido de paso un error: un CFDI cancelado en sandbox (`sandbox-cancelled`) seguía bloqueando el ticket.
- **Receptor:** ahora se valida con el catálogo SAT (`src/MesaNexo.Core/Sat.cs`):
  - dígito verificador del RFC;
  - régimen según persona moral o física;
  - usos D01–D10 y CN01 sólo para persona física;
  - el RFC genérico sólo con régimen 616 y uso S01;
  - el nombre se guarda en mayúsculas.

### Pilar 5. Bitácora de timbrado

Se agregó un registro `pacattempt` por **cada** llamada al PAC: timbre, consulta y cancelación. Guarda:

- ID interno (`fiscalId`), folio y operación;
- ambiente;
- código HTTP del PAC (`0` = sin respuesta, por tiempo o red);
- resultado, RFC receptor, UUID, mensaje y hora.

Se escribe **en la misma transacción** que el resultado, así que no hay timbre sin bitácora ni bitácora sin timbre. `AuditChain` rechaza cualquier edición de un `pacattempt` (es inmutable, como `audit` y `stockmovement`), y la cadena se ancla cada hora en R2 con bucket lock. Sólo la ven los gerentes.

No es un middleware HTTP aparte porque todas las llamadas al PAC pasan por `ProcessFiscal`. Un interceptor fuera de la transacción podría registrar un intento cuyo resultado no se guardó, o al revés.

## Archivo 2: terminales bancarias

El pedido supone Avalonia + Tauri con una API central en Python. Este sistema es otro:

- la sucursal es .NET (`MesaNexo.Api`);
- la central de licencias es un Worker de Cloudflare en TypeScript;
- Tauri habla con `MesaNexo.Bridge` por pipes privados, y el puente con la sucursal por HTTPS/SignalR;
- Avalonia queda como cliente de compatibilidad.

No se reescribió nada para ajustarse a ese supuesto. Lo pedido ya existe así:

| Pedido | En Culinary | Dónde |
|---|---|---|
| `IPaymentTerminalDriver` asíncrono | `TerminalService` con adaptadores `MercadoPagoPoint` y `ClipPinpad`, que declaran si son idempotentes, consultables y reversibles | `src/MesaNexo.Api/LiveTerminals.cs`, `src/MesaNexo.Integrations/TerminalAdapters.cs` |
| Fábrica de controladores | `TerminalService.From(terminal.json)` arma cada terminal por proveedor | ídem |
| No congelar la interfaz | El cobro es un trabajo durable (`terminaltxn`) que procesa un `BackgroundService`. La interfaz nunca espera al banco | `LiveIntegrations.cs` |
| Estados intermedios hacia la interfaz | Por la instantánea de SignalR (`waiting`, mensaje del proveedor), no con `IProgress` entre procesos | — |
| Timeouts y «doble ciego» | `HttpClient` de 20 s. Si la respuesta se pierde, se pregunta al proveedor: Mercado Pago responde la orden ya creada gracias a su llave de idempotencia. Sin respuesta queda `uncertain`, que bloquea otro cobro en la misma cuenta hasta resolverlo | `Integrations.cs:225`, `LiveIntegrations.cs:60-93` |
| PCI-DSS | Los datos de tarjeta nunca tocan el software: sólo marca, últimos 4 y autorización | — |
| Credenciales cifradas | En `/etc/culinaryos/integrations/terminal.json` (root y `culinaryos-app`). No viajan a los clientes ni a la base | — |

**Brechas reales:**

- **TCP/IP local y serial (COM):** no existen porque ningún proveedor elegido los ofrece. BBVA no tiene API pública y se cobra en el equipo con captura del voucher.
- **Clip:** si se pierde la respuesta sin el id de la solicitud, queda incierto hasta revisarlo a mano. No hay consulta por referencia documentada; probarlo en su sandbox.
- **Subestados del PIN pad** («Insertando tarjeta», «Digitando NIP»): las APIs en la nube de Mercado Pago y Clip no los exponen; sólo dan el estado de la orden.
- **Alta de terminales por el dueño:** hoy se definen en `terminal.json` al instalar. Podría hacerse desde Dueño con el mismo sobre cifrado del CSD.

## Flujo de datos y rutas

```
Culinary Dueño (dueño)                CulinaryServer                         Sucursal (CulinaryOS)                 Facturama
                                                                 PUT /branch/v1/fiscal/intake-key  ◄──── firmado con la llave de licencia
GET  /owner/v1/fiscal  ─────────────► llave de recepción + estado
cifra .cer/.key/contraseña (WebCrypto)
POST /owner/v1/fiscal/{lic}/csd ────► guarda el sobre (≤ 24 h, opaco)
                                                                 GET  /branch/v1/fiscal/submissions ◄─── cada minuto
                                                                 abre, valida RFC/vigencia/par/CSD ──── PUT TaxEntity/UploadCsd ──► 204
                                      borra el sobre, guarda resultado ◄── POST /branch/v1/fiscal/submissions/{id}/result
GET  /owner/v1/fiscal  ─────────────► «Sellos cargados con éxito», vence …

Caja ── fiscal.request ──► sucursal (registro fiscal + recibo + outbox) ── FiscalWorker ──► POST 3/cfdis ──► UUID
                                                     └── pacattempt en la misma transacción
Cliente ── QR/enlace ──► portal público ──► central (token de un solo uso) ──► sucursal (misma ruta que caja)
```

## Pruebas

- **CulinaryServer:** 132/132, de ellas 7 nuevas en `test/fiscal.spec.ts`:
  - RFC, razón social, régimen y C.P. inválidos;
  - alta atómica;
  - licencia antigua que sigue validando pero no se activa;
  - sólo el dueño ve y envía, y un gerente recibe 403;
  - el sobre sólo llega a la sucursal que registró la llave;
  - reemplazo de envíos y llave nueva.
- **Sucursal, `--csd-only`:** 23 comprobaciones:
  - catálogos SAT;
  - CSD válido con llave PBES2/3DES generada con `openssl pkcs8 -v2 des3`;
  - contraseña equivocada, par equivocado, e.firma, vencido, otro RFC, emisor no SAT;
  - sobre alterado, de otra licencia o de otra llave;
  - vector de interoperabilidad cifrado por el código real de Dueño (`tests/vectors/csd-envelope.json`);
  - contrato de `UploadCsd`.
- **Sucursal, `--fiscal-only`:** la suite anterior y 23 comprobaciones nuevas:
  - bitácora por timbre, respuesta perdida, rechazo, consulta y cancelación;
  - estado del ticket y bloqueo de doble timbre en caja y por QR;
  - receptor inválido;
  - recepción del CSD de punta a punta;
  - ni la contraseña ni el `.key` llegan al almacén ni a la auditoría;
  - un CSD vencido detiene el timbrado y los sellos nuevos lo liberan.
- **Culinary Dueño:** prueba de Playwright: un gerente no ve «Facturación», el dueño envía, y el sobre se abre con la llave de la sucursal.

## Qué falta para producción

1. **Facturama sandbox real:** cargar el CSD de prueba del SAT con `UploadCsd`. La especificación no define `Rfc` en el cuerpo aunque su ejemplo lo trae; se envía sin él. Después, timbrar.
2. **Desplegar CulinaryServer** (push a `main`). Antes del despliegue, la sucursal recibe 404 en las rutas fiscales y sólo registra advertencias.
3. **Compilar e instalar Culinary Admin.** No abrirlo desde Claude.
4. **Confirmar Culinary Dueño** cuando el diseño v3 tenga su commit.
5. **Pendientes de código:**
   - comparar la razón social del CSD con la de la licencia;
   - huella de la llave de recepción fuera de banda;
   - catálogo completo de códigos postales.
6. **Revisión del contador** de los regímenes excluidos y de las reglas del receptor.
