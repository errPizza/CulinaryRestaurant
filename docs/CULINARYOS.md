# Integración con CulinaryOS existente

Este procedimiento está preparado y no fue ejecutado en la VM. Requiere mantenimiento, administrador Linux y revisión de los archivos reales antes de aplicarlo. No se reinstala CulinaryOS ni se cambian identidad, red, llaveros o certificados.

1. Revisar los documentos originales RUNTIME-SUCURSAL, CIFRADO-V1, SINCRONIZACION-V1, RED-LOCAL y CERTIFICADOS-LOCALES. Comprobar la identidad real en `/etc/culinaryos/site.json`, salud del almacén y backup recuperable con sus llaves custodiadas aparte.
2. Copiar e instalar el paquete con `sudo apt install ./MesaNexo-0.1.0-dev-amd64.deb` en Debian 13 amd64. Coloca cliente y backend en `/opt/culinaryos-app/{client,backend}`; no habilita servicios ni aprovisiona llaves automáticamente.
3. Ejecutar, dentro de la VM, `sudo python3 /opt/culinaryos-app/integration/deploy/install-runtime-extension.py`. El script verifica hashes exactos del runtime base, exige el entrypoint conocido, solicita snapshot original, respalda código, detiene servicios existentes y añade el despachador de la extensión. Si la versión base difiere se detiene: auditar antes de adaptarlo. No ejecutar a ciegas sobre otra revisión. Si la sucursal ya tiene una extensión anterior, el mismo script con `--upgrade` la reemplaza solo si su SHA256 figura en `deploy/culinaryos/extension-history.json` (las entregas cd, r2, r3 y r4). Antes pide el snapshot original y copia la extensión y `dataio.py` a `/var/backups/mesanexo-runtime-*`; si la comprobación de salud falla, restaura la versión anterior. `install.py` hace lo mismo al instalar o reanudar sobre una extensión anterior conocida. Al publicar una extensión nueva, agrega el SHA256 de la anterior a ese archivo.
4. Revisar `application.server.example.json`, sustituir IDENTIDAD-REAL y configurar el manifiesto de aplicación que consume el apphost original, según RUNTIME-SUCURSAL. Conservar el wrapper y `culinaryos-backend.service`; no arrancar una segunda autoridad. El backend usa `/run/culinaryos-data/store.sock` y responde readiness por `/run/culinaryos-app/ready.sock` solo con almacenamiento/identidad válidos. Mantener `/opt/culinaryos-app` como ubicación ejecutable.
5. Aprovisionar certificado y clave TLS mediante `LoadCredential` del mecanismo existente. Inicializar una sola vez, durante mantenimiento, mediante `/opt/culinaryos-app/backend/MesaNexo.Api --site <site-real> --initialize <archivo-privado.json>`. Basarse en branch-init.example.json; cambiar todos los placeholders y usar contraseña propia y código aleatorio de al menos 20 caracteres. Ejecutar como el usuario autorizado del runtime. Retirar de forma controlada ese archivo secreto; el comando rechaza una sucursal ya inicializada.
6. Verificar salud, permisos del socket, reinicio del servicio, readiness y arranque de interfaz con Secret Service de la sesión desbloqueado. Provisionar en el cliente solo CA pública/huella, identidad, nombre HTTPS y código de alta. Probar acceso gerente/mesero/cocina desde otro equipo y caída de WAN sin deshabilitar TLS.

## Central opcional

La central se despliega aparte con usuario dedicado y `deploy/mesanexo-central.service`. Requiere identidad de proveedor, TLS y una clave central aleatoria de 32 bytes distinta de los llaveros de sucursal; clave RSA privada para firmas solo en central. No hay credenciales predeterminadas. Registrar en central la identidad y huella del certificado mTLS de cada sucursal.

Para política comercial/soporte usar `application.policy.example.json`: mapea los certificados centrales originales como credenciales `central-ca`, `central-cert`, `central-key`; aprovisiona `/etc/culinaryos/provider-public.pem` por canal administrativo. Los originales permanecen en `/etc/culinaryos/secrets/central-ca.crt`, `central-client.crt`, `central-client.key`. Mantener el emisor `culinaryos-sync` existente y su endpoint `/v1/events`; el refresco de política no lo sustituye.

La terminal usa el manifiesto terminal de ejemplo y no inicia almacenamiento ni backend. El adaptador Linux llama `/usr/local/sbin/culinaryos-discover`; un anuncio solo propone direcciones, sin conceder confianza. No modificar firewall o certificados para resolver errores de descubrimiento sin revisar su causa.

## Aceptación pendiente en VM

Comprobar snapshot/restore real, permisos POSIX, presión de disco, socket obsoleto, reinicio/corte eléctrico, outbox con central caída y posterior ACK, renovación/caducidad de TLS, dos terminales simultáneas y Secret Service. No presentar las pruebas Windows como evidencia de systemd o Avahi.

## Ampliación DEVMap — 0.2.0

Se preservan los cuatro archivos vendor y sus hashes, CIFRADO-V1, claves y tablas originales. La extensión mantiene transaction_v1 y sus límites. Se corrige el límite superior de scan para incluir tipos que empiezan con z y se añade snapshot_v1 controlado por servicio: destino restringido a schema-backups junto al Store, nombre UUID, snapshot cifrado y SHA-256. No ofrece un destino arbitrario al cliente.

La actualización de extensión en una instalación existente requiere mantenimiento y respaldo de la versión previa; el instalador de primera instalación no debe forzarse para sobrescribir una extensión ya instalada. Revisar capacidades y permisos de la versión desplegada, detener backend, sustituir sólo la extensión revisada mediante el procedimiento de administración de CulinaryOS y reiniciar el servicio correspondiente. Verificar health/identidad/snapshot antes de migrar esquema; ante fallo, restaurar la versión respaldada. Este procedimiento no se ejecutó en la VM.

El API puede consumir propuestas nube por salida HTTPS/mTLS (`--cloud-url`) con credenciales del servicio y clave pública del proveedor revisada; no envía outbox ni abre otro listener. Señala central inaccesible por separado de LAN o servidor local caído. No se cambian firewall, arranque, mDNS de confianza ni claves maestras.

## Impresora de tickets y cajón

El backend imprime cuentas, cortes Z y aperturas de cajón en una impresora térmica ESC/POS cuando existe `/etc/culinaryos/printer.json` (o se pasa `--printer-config <archivo>`); sin ese archivo no imprime nada y los trabajos quedan pendientes, igual que antes. Ejemplo en `deploy/printer.example.json`:

- `"uri": "tcp://10.77.0.20:9100"`: impresora de red en su puerto RAW (9100 en la mayoría de las térmicas Ethernet). Darle IP fija dentro de la red operativa.
- `"uri": "file:///dev/usb/lp0"`: impresora USB. El usuario `culinaryos-app` necesita el grupo `lp` (`usermod -aG lp culinaryos-app` y reiniciar el backend).
- `"columns"`: 48 para papel de 80 mm (fuente A), 32 para 58 mm.

El cajón se conecta al puerto RJ11 de la impresora; el pulso usa el pin 2 (ESC p 0 25 250).

Comportamiento:

- Antes de reclamar un trabajo se consulta el estado de la impresora (DLE EOT). Sin papel, con la tapa abierta, fuera de línea o sin conexión, el trabajo queda pendiente sin tocarse y se reintenta cada pocos segundos; el cajón se abre aunque falte papel.
- Un trabajo reclamado que se interrumpe (corte de conexión, papel agotado a la mitad) queda `uncertain` y nunca se reimprime solo: el gerente revisa el papel y usa reimprimir.
- Una impresora USB o una de red que no responde a DLE EOT imprime igual; la evidencia del trabajo dice que el estado no se confirmó.
- `/api/state` informa el estado en `connection.printer` (`lista`, `sin papel`, `tapa abierta`, `sin conexión`…).
- Con la integración `printer` en modo simulado, los trabajos se marcan como simulados y no sale papel.

Probado contra una impresora ESC/POS simulada por TCP (`tests/MesaNexo.Tests/PrintTests.cs`); falta probarlo con una impresora y un cajón físicos.

## Facturación CFDI 4.0 (sandbox de Facturama)

Con `/etc/culinaryos/fiscal.json` (o `--fiscal-config <archivo>`), el backend envía las solicitudes fiscales al sandbox de Facturama y registra el timbre o el rechazo; sin ese archivo las solicitudes quedan pendientes como antes. El archivo contiene la contraseña del PAC: dueño `root`, grupo `culinaryos-app`, permisos `0640`. Ejemplo en `deploy/fiscal.example.json`:

- `"environment": "sandbox"` es el único valor aceptado. Los timbres de prueba no tienen validez fiscal y se marcan `SANDBOX`. La facturación real queda rechazada hasta tener el CSD del emisor cargado en Facturama, contrato con el PAC y revisión de las claves SAT.
- `issuer.expeditionPlace`: código postal de expedición. `issuer.product`: clave SAT por defecto para todos los productos (en restaurantes, típicamente `90101501` con unidad `E48` y objeto de impuesto `02`); `issuer.products` admite excepciones por id de producto. El IVA de cada concepto es el que quedó congelado en la venta. Las claves SAT (productos y `issuer.delivery` para el cargo de envío) deben revisarse con el contador del emisor antes de facturar en producción.
- La forma de pago se toma de los cobros del ticket (efectivo 01, tarjeta 04, transferencia 03, monedero 05; con varias formas, la de mayor importe). `issuer.paymentForms` cambia la clave de un método, por ejemplo `"card": "28"` para débito.

Garantías:

- Un CFDI nunca se timbra dos veces. Si Facturama no responde o se pierde la respuesta sin su folio, la solicitud queda «Pendiente de conciliación» con el folio a buscar en Facturama. Si no aparece ahí, el gerente la reintenta (`fiscal.retry`) con el mismo folio y fecha; si aparece, no se reintenta.
- Una respuesta sin timbre pero con el folio del proveedor se resuelve consultándolo, sin reenviar.
- Una solicitud que no pasa la validación (clave SAT o tasa) queda bloqueada hasta que cambie la configuración, sin detener las demás.
- Con la simulación del PAC activa en Integraciones, el sandbox no toca ninguna solicitud.
- `/api/state` informa el servicio en `connection.fiscal` (`sandbox` o `error`).

Probado contra un Facturama simulado (`tests/MesaNexo.Tests/FiscalTests.cs`); falta probarlo con una cuenta sandbox real.
