# Culinary Restaurant en CulinaryOS

Este CD integra la app de escritorio Tauri con el backend .NET real y el almacén cifrado existente de CulinaryOS. Conserva React/CSS, animaciones, iconos y funciones del cliente actual. No abre un navegador ni instala Wine. El frontend ya está empaquetado; solo se compila el ejecutable Tauri Linux en la VM.

## Instalar en la VM CulinaryOs_Test

El CD de datos `CulinaryRestaurant-CulinaryOS-0.3.0.iso` contiene el instalador, el backend y transporte autocontenidos Linux x64, el frontend y el código Tauri bloqueado con Cargo.lock. Es un CD de aplicación, no una ISO para reinstalar el sistema.

Desde una terminal de tu cuenta administradora de Debian:

```bash
sudo mkdir -p /mnt/culinary-app
sudo mount -o ro /dev/sr0 /mnt/culinary-app
sudo bash /mnt/culinary-app/instalar.sh
```

Necesita internet durante la primera instalación para las bibliotecas oficiales de Debian, Rust 1.99.1 y las dependencias fijadas en Cargo.lock. Compila con dos trabajos en el disco Linux de la VM; la primera compilación puede tardar. No necesita Node.js ni el SDK .NET dentro de la VM.

La comprobación previa exige CulinaryOS instalado con escritorio, sitio/red de sucursal configurados y almacén disponible. Rechaza otra aplicación registrada o un runtime distinto de la versión auditada. No recrea el sitio, la base ni sus claves. La extensión transaccional se añade sobre el mismo almacén, después de una copia consistente.

Si aún no hay cuentas del restaurante, solicita nombre, gerente, usuario y contraseña propia, y muestra un código de alta nuevo una vez. No hay credenciales por defecto. Usa la configuración inicial local `licencia`, módulos opcionales vacíos y límites de 15 usuarios/30 mesas; no contrata ni activa servicios comerciales. Si ya existen cuentas o datos, se conservan; no se migra un esquema anterior automáticamente. No copia las cuentas ni los datos de Windows.

## Inicio del sistema

Al entrar en la sesión de CulinaryOS, el autostart existente inicia el cliente nativo maximizado una sola vez. El backend se inicia como servicio antes del escritorio y el registro exige readiness real, HTTPS y la identidad de la sucursal. Cerrar la app conserva el backend y el almacén. También se añade un acceso al menú de aplicaciones.

**Se conserva la política de inicio de sesión de Linux.** Si la VM ya inicia sesión automáticamente como operador, la app aparece tras arrancar. Si Linux pide contraseña, aparece después de entrar. Este instalador no habilita autologin ni elimina la autenticación del sistema.

Se instala un perfil público de conexión con CA, huella e IP del servidor. La identidad del dispositivo y las operaciones pendientes se guardan en Secret Service; Linux puede pedir crear o desbloquear el llavero. No se copian las claves privadas de datos/TLS a la interfaz. La CA TLS de la aplicación se crea únicamente si aún no existe material TLS de la app; no reemplaza una CA del sistema. Vigencia inicial del certificado de servidor: 365 días; renovar administrativamente antes de caducar.

Los adaptadores externos permanecen aislados. No se configura el central ni se envían datos a servicios externos. Los recursos se descargan de Debian/Rust/crates.io solo para instalar y compilar.

## Verificar dentro de la VM

```bash
systemctl is-active culinaryos-data.service culinaryos-backend.service
python3 /usr/local/lib/culinaryos/dataio.py health
```

Cierra la sesión y vuelve a entrar para comprobar la apertura automática. Reinicia cuando lo decidas para verificar el ciclo completo. No hay reinicio automático del invitado.

Ante un error, conserva la salida; no repitas init ni deploy-site. La copia de configuración se imprime bajo /var/backups/culinaryos/restaurant-*. Una instalación que haya llegado a crear cuentas no debe reinicializarse. El registro del runtime conserva su comprobación TLS y reversión de firewall si falla.

## Alcance comprobado desde Windows

Publicación autocontenida Linux del backend y puente, pruebas portátiles del instalador, certificados reales con OpenSSL, revisión de scripts con Bash/ShellCheck y pruebas del transporte existente. La compilación WebKitGTK/Tauri, las unidades systemd, el llavero y la apertura tras reiniciar deben comprobarse dentro de la VM; no se presentan como ejecutadas desde Windows.

Referencias: [requisitos oficiales de Tauri](https://v2.tauri.app/start/prerequisites/) y [WebKitGTK de Debian 13](https://packages.debian.org/trixie/libwebkit2gtk-4.1-0).

Verificación del paquete preparado: 20 pruebas portátiles del instalador y 21 del puente aprobadas, tres scripts con sintaxis comprobada y ShellCheck sin observaciones. Los 32 archivos del CD fueron extraídos y cotejados byte a byte contra SHA256SUMS. Evidencia: `evidence/culinaryos-integration.json`. El CD se conectó a `CulinaryOs_Test`; la instalación y el reinicio quedan pendientes de ejecutar en su terminal.


### Compatibilidad del runtime â€” revisión 2 del CD

El paquete original CulinaryOS 0.2.0 contiene un `store.py` con SHA256 `10a1e6e100312a17662d6f2eaf7e3d0dc8554d661dd58c8fedbd4cbc5a667213`. Frente a la copia actual `c9d2b7884dd4d3344fd522053f9d4c1788635b28bc2fb9189a107e664210fec7`, solo añade un comentario explicativo sobre FIFO: el AST Python coincide. Ambos validadores reconocen exactamente esas dos huellas, sin sustituir el archivo instalado. Cualquier otra versión sigue deteniendo la instalación. La revisión se conserva en `evidence/culinaryos-runtime-compatibility.json`. La nueva ISO lleva sufijo `-r2`; antes de cambiarla y volverla a montar deben desmontarse tanto `/mnt/culinary-app` como `/media/cdrom0`, si están activos.


### Reanudar después de compilar — revisión 3

La compilación Linux mostrada por el usuario terminó correctamente en 8m09s. Después falló la copia de configuración porque intentaba crear otra vez el directorio `etc` ya creado para los respaldos anteriores. Se corrigió usando `exist_ok=True` para los padres compartidos, manteniendo cada carpeta de respaldo independiente y sin sobrescribir respaldos previos.

La revisión 3 permite continuar con `sudo bash /mnt/culinary-app/instalar.sh --resume`. Busca el ejecutable ELF previo en `/var/tmp/culinary-restaurant-build.*`, exige que pertenezca a la cuenta administradora original y coteja sus fuentes, frontend y puente con el CD. Si no coincide, se detiene; no recompila ni borra la compilación. Si coincide, omite APT/Rust y continúa la instalación. El mensaje de copia fallida apareció antes de instalar binarios, extender el runtime o inicializar cuentas del restaurante.


### Permisos de las carpetas del código

La máscara privada `0077` hizo que las carpetas nuevas del código quedaran `0700` aunque mkdir solicitaba `0755`. La salida `namei -l` del usuario confirma `/opt/culinaryos-app` y `backend` como root `0700`, y el ejecutable `MesaNexo.Api` como root `0755`.

El instalador fuente fija ahora explícitamente `0755` solo en `/opt/culinaryos-app`, sus carpetas `backend`/`client` y sus cuatro ejecutables root. Verifica el acceso con los usuarios reales del servicio y operador antes de solicitar credenciales. No modifica el almacén, claves TLS, credenciales ni directorios de estado. Para la instalación parcial desde el CD revisión 3, se puede aplicar:

```bash
sudo chmod 755 /opt/culinaryos-app /opt/culinaryos-app/backend /opt/culinaryos-app/client
sudo bash /mnt/culinary-app/instalar.sh --resume
```

La extensión transaccional ya fue instalada según la salida del usuario; la reanudación la conserva. El backend no llegó a ejecutarse al crear la cuenta, por lo que todavía se deben introducir sus datos de alta. No es necesario recompilar ni cambiar el CD. Las 36 pruebas portátiles del instalador pasaron; la verificación de acceso con los UID reales se ejecuta dentro de Linux.


### Revisión 4: reanudación tras instalar la extensión

La revisión anterior llevaba `runtime/mesanexo_ipc.py` con CRLF, pero el instalador lo escribía con LF al leerlo como texto en Debian. La reanudación comparaba bytes distintos del mismo código y detenía el proceso. La revisión 4 empaqueta LF, coincidiendo con lo que ya está instalado, y las instalaciones futuras preservan los bytes del archivo al escribir en modo binario. La comparación SHA256 sigue siendo exacta: no se ignoran cambios de código ni se reemplaza una extensión previa. Se incluye la corrección permanente de permisos del código y de las carpetas públicas de iconos; datos y claves conservan sus permisos privados. La compilación Tauri existente se reutiliza con `--resume`.
