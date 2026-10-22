# Integración con CulinaryOS existente

Este procedimiento está preparado y no fue ejecutado en la VM. Requiere mantenimiento, administrador Linux y revisión de los archivos reales antes de aplicarlo. No se reinstala CulinaryOS ni se cambian identidad, red, llaveros o certificados.

1. Revisar los documentos originales RUNTIME-SUCURSAL, CIFRADO-V1, SINCRONIZACION-V1, RED-LOCAL y CERTIFICADOS-LOCALES. Comprobar la identidad real en `/etc/culinaryos/site.json`, salud del almacén y backup recuperable con sus llaves custodiadas aparte.
2. Copiar e instalar el paquete con `sudo apt install ./MesaNexo-0.1.0-dev-amd64.deb` en Debian 13 amd64. Coloca cliente y backend en `/opt/culinaryos-app/{client,backend}`; no habilita servicios ni aprovisiona llaves automáticamente.
3. Ejecutar, dentro de la VM, `sudo python3 /opt/culinaryos-app/integration/deploy/install-runtime-extension.py`. El script verifica hashes exactos del runtime base, exige el entrypoint conocido, solicita snapshot original, respalda código, detiene servicios existentes y añade el despachador de la extensión. Si la versión base difiere se detiene: auditar antes de adaptarlo. No ejecutar a ciegas sobre otra revisión.
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
