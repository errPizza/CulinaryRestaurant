# Migración explícita desde copias

No se abre ni migra automáticamente `SoftwarePrototipo/data`. No se copiaron credenciales ni se importaron los datos reales del usuario. El exportador rechaza la ruta de datos original. Preparar una copia consistente mediante el mecanismo de respaldo del prototipo; no copiar solo el .sqlite de una base activa en WAL.

Ejemplo sobre una copia ya entregada, en un directorio nuevo y protegido:

```powershell
python -B tools/migrate_prototype.py --source-copy C:\Migracion\copia.sqlite --output C:\Migracion\plan-nuevo --restaurant-id r1 --branch-id r1-b1 --site-id identidad-real
```

Los identificadores son ejemplos: deben existir en la copia. El exportador usa conexión read-only y backup SQLite consistente, valida esquema, referencias e importes y produce `import-plan.json`, `report.json` y copia consistente. Revisar cantidades, saldos e identidades antes de importar. El informe contiene SHA-256 del plan. Cada sucursal requiere plan y servidor propios.

Los respaldos AES-256-GCM antiguos necesitan la clave ORIGINAL de 32 bytes; no son el formato XChaCha de CulinaryOS:

```powershell
dotnet src/MesaNexo.Api/bin/Debug/net10.0/MesaNexo.Api.dll --decrypt-backup C:\Migracion\respaldo-copia.json --legacy-key C:\Custodia\clave-original-copia --output C:\Migracion\estado-descifrado.json
```

La salida queda en claro y debe custodiarse con permisos privados; no se imprime su contenido ni se sobrescribe. Después usar ese JSON como `--source-copy`. Sin la clave original no es posible descifrar; no se intenta recuperar ni sustituir la clave.

## Importación en runtime

Inicializar una sucursal nueva sin actividad ni sesiones. Con backend operativo detenido, snapshot verificado y usuario autorizado al socket, ejecutar:

```sh
/opt/culinaryos-app/backend/MesaNexo.Api --site identidad-real --import-plan /ruta/privada/import-plan.json --plan-sha256 HASH_DEL_INFORME
```

El importador comprueba identidad y huella, marca mantenimiento, inserta lotes transaccionales con checkpoint y reanuda únicamente el mismo plan. Valida referencias/saldos antes de activar. Si falla la validación permanece en mantenimiento: conservar evidencia y volver al snapshot bajo procedimiento controlado; no editar contadores para forzar activación. El laboratorio usa además las opciones `--lab --python ... --lab-script ... --lab-directory ...`, nunca en producción.

Usuarios antiguos se importan desactivados, sin contraseñas; sesiones, secretos de dispositivo y accesos de proveedor no se trasladan. El gerente de aprovisionamiento conserva acceso para asignar credenciales nuevas. Se mantienen precios históricos por unidad; cobros siguen siendo registros manuales. Consentimientos previos quedan revocados. Los eventos de importación entran en outbox: planificar volumen inicial.

Los registros mayores de 40 KiB se rechazan, sin truncar. En una copia de trabajo externalizar imágenes grandes a HTTPS de confianza y revisar el plan generado. La migración masiva con datos reales, restauración productiva y conciliación con la central siguen pendientes; solo se probaron fixtures sintéticos.

## Ampliación DEVMap — 0.2.0

Las sucursales nuevas se inicializan en schemaVersion 2. Las existentes no se reinicializan. Para schema 1: conciliar/cerrar cuentas abiertas, completar stockjobs, cerrar o expirar sesiones, detener el backend y custodiar un respaldo cifrado y el anillo ORIGINAL mediante el mecanismo CulinaryOS. Verificar primero que la extensión instalada tenga snapshot_v1.

En mantenimiento, ejecutar el mismo API/identidad/socket autorizados con `--upgrade-schema` (sin levantar listener). UpgradeSchema comprueba condiciones, pide snapshot_v1 al Store y sólo entonces compromete schemaVersion 2 y metadatos de respaldo mediante CAS. Si ya está en 2 devuelve alreadyApplied. No cambia precios/impuestos históricos ni borra usuarios, recibos o outbox.

La central crea tablas tenant en una transacción; cuando hay datos anteriores y falta el esquema tenant, obtiene snapshot cifrado antes del DDL. La incorporación de device_id revoca sesiones anteriores. No asignar una empresa deduciéndola de entradas públicas: aprovisionarla con administración local y vinculación de certificado/sitio revisada.

SchemaTests prueba mantenimiento, snapshot e idempotencia y restaura una copia sintética con sus claves originales, verificando usuarios/outbox/readiness. Esa evidencia no demuestra que una restauración productiva haya ocurrido.
