# Respaldo y recuperación

## Sucursal

Usar snapshots y procedimientos de CulinaryOS existentes; conservar el llavero requerido y su identidad aparte del disco de datos. El paquete no contiene una clave maestra ni implementa un cifrado alternativo. `vendor/culinaryos` es referencia inmutable de laboratorio, no un segundo servicio de producción.

Antes de mantenimiento: detener el acceso operativo, conciliar caja, comprobar snapshot consistente y posibilidad de recuperar sus llaves. Usar el servicio original `culinaryos-snapshot.service` y las instrucciones RECUPERACION/CIFRADO-V1 del proyecto CulinaryOS para ubicar y restaurar la copia real. No intercambiar snapshots de distintas identidades. Restaurar con servicios detenidos y usuario/permisos originales; validar salud, recibos, outbox y cuentas antes de reabrir. La extensión de código debe corresponder al esquema almacenado.

Una pérdida de respuesta no exige restaurar: el cliente consulta el recibo del UUID persistido. Restaurar un snapshot antiguo sí retrocede estado y recibos; requiere conciliación con cobros manuales y ACK/eventos que la central ya recibió. No se garantiza rollback distribuido ni resurrección automática de todas las operaciones posteriores a la copia.

## Central

La clave AES central de 32 bytes se custodia por separado del SQLite; no es la clave de ninguna sucursal. El servicio incluye exportación de snapshot por proveedor autenticado y CLI administrativa. Ejemplo con rutas/identidad reales, bajo el mismo usuario y credenciales del servicio:

```sh
MesaNexo.Central --provider-id ID --directory /var/lib/mesanexo-central --data-key /ruta/privada/data.key --snapshot-to /respaldo/nuevo.sqlite
```

Guardar SHA-256 del archivo fuera de la copia. Para restaurar, detener el servicio y confirmar identidad/clave/huella:

```sh
MesaNexo.Central --provider-id ID --directory /var/lib/mesanexo-central --data-key /ruta/privada/data.key --restore-from /respaldo/copia.sqlite --snapshot-sha256 HASH --confirm RESTAURAR
```

Se valida integridad, clave, proveedor y huella, se crea copia previa y se usa SQLite backup; se invalidan sesiones. No se copia encima de un SQLite activo. Los paquetes publicados son archivos separados: respaldar también `packages/`, configuraciones TLS y clave de firma mediante custodia administrativa; el snapshot de SQLite no incluye esos archivos. Un snapshot central antiguo puede perder deduplicación ya ACK: conciliar espejos con sucursales y no asumir reenvío de eventos ya confirmados.

Las pruebas locales validaron snapshot/restore y clave incorrecta. Recuperación tras daño físico, copia externa, corte eléctrico y entorno Linux completo siguen pendientes.

## Ampliación DEVMap — 0.2.0

Guardar snapshot cifrado, hash, identidad de sitio, configuración de servicio y anillo original bajo custodia separada. Una base sin sus claves no es recuperable. Recuperar primero en un entorno aislado de staging con las claves correctas y el backend detenido; comprobar identidad, readiness, cuentas/pagos, recetas/versiones, reservas/jobs, caja, auditoría, recibos y outbox antes de cualquier cambio de producción.

No copiar sólo SQLite de un proceso activo en WAL, no generar claves nuevas para intentar abrir un respaldo, no limpiar recibos/outbox para resolver diferencias. Tras reinicio InventoryWorker continúa jobs por avance durable; una venta reabierta no vuelve a consumir unidades ya consumidas. Para impresión incierta se requiere revisión explícita: reintentar crea trabajo separado y nunca repite cobro. Un resultado fiscal incierto no se presenta como timbre.

Las sesiones centrales se revocan al restaurar. Los pedidos nube mantienen id/digest/acuse; no anunciar aceptación si falta commit de sucursal. La cadena de auditoría debe verificarse también contra respaldos/anclas independientes. Ensayo real de recuperación y corte eléctrico permanece pendiente.
