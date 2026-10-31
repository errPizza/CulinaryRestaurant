# Culinary Restaurant: sesión kiosco en CulinaryOS

El kiosco inicia una sesión gráfica exclusiva para `operador`. No inicia XFCE, sus paneles, su dock ni su escritorio. La app usa pantalla completa sin decoración y rechaza el cierre normal. Un supervisor vuelve a abrirla si el proceso termina o si la interfaz deja de enviar señales durante 45 segundos.

La pantalla de carga grafito permanece hasta que React carga las fuentes, presenta la ventana y confirma dos fotogramas más un margen de dibujo. No se confunde la disponibilidad del backend con la visibilidad de la interfaz. La presentación no espera al llavero: su diálogo de desbloqueo puede aparecer sobre el login.

## Instalación incremental en la VM existente

Este CD es de mantenimiento: no ejecutar el instalador inicial, no volver a crear sucursal, usuario, contraseña ni código de alta.

1. Antes de cambiar el CD desde VirtualBox, desmontar ambos montajes del invitado:

   ```sh
   sudo umount /mnt/culinary-app /media/cdrom0
   ```

2. Con el CD nuevo conectado, desde la cuenta administradora `laboratorio`:

   ```sh
   sudo mkdir -p /mnt/culinary-app
   sudo mount -t iso9660 -o ro /dev/sr0 /mnt/culinary-app
   sudo bash /mnt/culinary-app/activar-kiosco.sh
   ```

   Si estás en la cuenta `operador`, primero cambia a `laboratorio` con `su - laboratorio`.

3. El instalador verifica las sumas, la app registrada, los permisos y que LightDM ya inicia automáticamente `operador`. Conserva esa política de autenticación. Instala las dependencias gráficas y compila la interfaz como la cuenta administradora, sin root, reutilizando Rust 1.99.1 y los paquetes/caché de la compilación anterior en `/var/tmp/culinary-restaurant-build.*`. Cargo trabaja con `--locked --offline`; APT requiere acceso a los repositorios si faltan paquetes.

4. Antes de modificar la app instalada, ejecuta una prueba real en un Xvfb y D-Bus privados, con HOME y llavero desechables. Comprueba interfaz visible, pantalla completa, rechazo de cierre por el gestor de ventanas y un segundo heartbeat. No inicia sesión en el restaurante ni cambia sus datos.

5. Sólo si esa prueba pasa, respalda el ejecutable y los destinos de configuración y sustituye la interfaz. La configuración de LightDM se escribe al final. No cierra la app actual ni reinicia automáticamente. Cuando aparezca `Kiosco preparado`, ejecuta:

   ```sh
   sudo reboot
   ```

La compilación y la prueba real dentro de Debian deben completarse en la VM; las pruebas ejecutadas en Windows no las sustituyen.

## Qué se conserva

Backend, almacén cifrado, sucursal, cuentas, credenciales del dispositivo, llavero, certificados TLS, cortafuegos y sincronización existentes. No habilita adaptadores externos. Las sesiones normales de mantenimiento y la consola TTY2 siguen disponibles. Windows conserva su ventana normal.

El kiosco bloquea el cierre habitual del operador, no las acciones de un administrador del sistema ni el apagado de la máquina virtual desde Windows.

## Mantenimiento y restauración

Desde el kiosco, `Ctrl+Alt+F2` abre la consola de administración. En VirtualBox puede usarse el menú **Entrada → Teclado** para enviar esa combinación al invitado. Inicia sesión como `laboratorio`.

El instalador imprime la ruta exacta de su respaldo, por ejemplo `/var/backups/culinaryos/restaurant-kiosk-<fecha>`. Para volver a la app y a la sesión anteriores:

```sh
sudo python3 /opt/culinaryos-app/kiosk/install-kiosk.py --restore /var/backups/culinaryos/restaurant-kiosk-<fecha>
sudo reboot
```

Usa la ruta real impresa, sin escribir literalmente `<fecha>`. También puede ejecutarse el mismo `install-kiosk.py` desde el CD. La restauración se limita a archivos de interfaz/sesión respaldados. No restaura ni modifica datos o claves del restaurante.

## Si aparece «Recuperando Culinary Restaurant»

Esa pantalla sólo sale cuando la app termina o deja de enviar señales durante 45 segundos. Cada vez queda la causa (código de salida, señal o falta de señales) y el final de la salida de la app en `/home/operador/.local/state/culinary-restaurant/kiosk.log`. Desde TTY2:

```sh
sudo tail -n 60 /home/operador/.local/state/culinary-restaurant/kiosk.log
```

En X11, Ctrl+C es una tecla y no llega como SIGINT: la app corre en su propia sesión, sin terminal. Una señal `SIGSEGV` o `SIGABRT` apunta a WebKitGTK o al controlador gráfico. La sesión desactiva el renderizador DMA-BUF de WebKitGTK por eso.

## Pila y UPS

Mientras el equipo funciona sin corriente, la barra superior muestra el porcentaje y, si el hardware lo informa, el tiempo de servicio. Con cable no se muestra nada. El dato sale de `/sys/class/power_supply` en una laptop y de `upsc culinaryos@localhost` con un UPS configurado por `culinaryos-ups`; si ambos están sin corriente, manda el UPS. Se comprueba sólo en el equipo local: un UPS conectado a otro equipo de la sucursal no aparece aquí. Si el UPS no informa `battery.runtime`, o la laptop no informa consumo, se muestra sólo el porcentaje.

## Marco exterior de VirtualBox

Las barras de VirtualBox pertenecen al anfitrión Windows. Para ocupar toda la pantalla, usa **Ctrl derecha + F** (Host+F con la tecla Host predeterminada). La minibarra puede ocultarse en la configuración de interfaz de usuario de la VM. El kiosco del invitado no modifica la configuración del anfitrión.

## Referencias de implementación

- [LightDM: selección de autologin-session](https://github.com/canonical/lightdm/blob/main/data/lightdm.conf).
- [LightDM: selección de sesión por usuario y autologin](https://github.com/canonical/lightdm/blob/main/src/seat.c).
- [Openbox: configuración y atajos](https://openbox.org/help/Configuration).
- [Tauri: configuración de ventanas](https://v2.tauri.app/reference/config/).

## Corrección del CD de mantenimiento r7

La revisión r6 se detenía antes de instalar porque consultaba únicamente stdout de `lightdm --show-config`. LightDM imprime la configuración en stderr incluso cuando termina con éxito. La revisión r7 combina ambos canales tanto en la comprobación inicial como en la validación final, sin cambiar la política de autenticación. Las pruebas reproducen el caso con un proceso real que escribe sólo en stderr y también comprueban que un comando fallido se rechaza.

## Recuperar clic y teclado en una sesión ya instalada

La configuración inicial vaciaba las reglas de ratón de Openbox. Se restaura únicamente el foco y la elevación de ventanas al pulsar un botón dentro del cliente; continúan desactivados los atajos y menús del gestor. Desde TTY2 (`Ctrl+Alt+F2`), con la cuenta `laboratorio`, ejecutar:

```sh
sudo cp -n /opt/culinaryos-app/kiosk/openbox.xml /opt/culinaryos-app/kiosk/openbox.xml.antes-foco
sudo sed -i 's|<mouse/>|<mouse><context name="Client"><mousebind button="Left Middle Right" action="Press"><action name="Focus"/><action name="Raise"/></mousebind></context></mouse>|' /opt/culinaryos-app/kiosk/openbox.xml
sudo reboot
```

El comando conserva una copia del XML y no requiere recompilar. Se comprobó su sintaxis, su resultado XML y su repetición sobre la configuración exacta del CD r7. La recuperación del foco y la escritura en la VM requieren confirmación del usuario; no se han ejecutado estos comandos en el invitado desde el anfitrión.

Además, el código nativo actual restaura el foco del WebView cuando la ventana del kiosco recupera el foco, y la prueba gráfica de futuras instalaciones exige que un clic real reactive la ventana tras enfocar otra. Estos cambios de código no forman parte del binario del CD r7 ya instalado: el ajuste de configuración anterior se aplica por separado a la VM actual.
