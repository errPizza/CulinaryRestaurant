# Plan: servidor de respaldo de la sucursal

6 de noviembre de 2026. Responde a la sección «2. ¿Puede cambiar el equipo que hospeda todo…» de `docs/AUDITORIA-FISCAL-Y-CONTINUIDAD.md`. Cada fase espera la aprobación del usuario. Un push a `main` de CulinaryServer despliega a producción, así que la fase de licencias se sube sólo con autorización expresa.

**Estado.** El usuario aprobó el 6 de noviembre las seis recomendaciones de abajo y la fase 1, que ya está hecha (ver «Fase 1: lo hecho»). Las fases 2 a 5 siguen pendientes. Todavía **no hay promoción**: si el principal muere, el respaldo tiene los datos al día, pero no puede vender.

## Fase 1: lo hecho

`deploy/culinaryos/ha/`: `ha.py`, el comando `culinaryos-ha` y la unidad `culinary-ha-watch.service`. Llegan a cada equipo en el `.deb` de actualización (`tools/package_update.py`) y con `install.py`. Instalar no activa nada; el par existe sólo después de emparejar.

Procedimiento, con los dos equipos ya instalados y la red de sucursal desplegada:

1. En el principal: `sudo culinaryos-ha primary --peer <IP del respaldo>`. Reinicia PostgreSQL (unos segundos sin vender), crea el rol `culinary_replica` y la ranura `culinary_standby`, abre el 5432 sólo a esa IP y muestra un código de 32 caracteres.
2. En el respaldo: `sudo culinaryos-ha standby --primary <IP del principal>` y escribir el código. El código lleva la contraseña de réplica y la huella de la CA del principal. El respaldo baja esa CA de `api/trust` y la acepta sólo si coincide con la huella. Comprueba código, TLS y firewall antes de cambiar nada. Después detiene su aplicación (la bloquea con `/etc/culinaryos/ha-standby`), guarda aparte su base propia sin borrarla y copia todo del principal con `pg_basebackup`. Si algo falla, incluso con Ctrl+C, vuelve a como estaba.
3. `culinaryos-ha status` en cualquiera de los dos.
4. Si el respaldo estuvo apagado tanto que perdió su lugar (más de 2 GB de WAL): `sudo culinaryos-ha rejoin` en el respaldo. El vigilante del principal ya recreó la ranura.
5. `sudo culinaryos-ha unpair` en el principal deshace el par.

Comportamiento del vigilante (`culinary-ha-watch`, corre como `postgres`):

- **Principal.** Activa la espera síncrona (`synchronous_standby_names = 'FIRST 1 (culinary_standby)'`) tras 3 s de ver al respaldo transmitiendo. La quita 5 s después de perderlo; el walsender lo da por perdido a los 5 s (`wal_sender_timeout`), así que una venta espera unos 10 s como máximo. Las ventas que estaban esperando se liberan en ese momento.
- **Escritura de estado.** Cada segundo escribe `/run/culinaryos-ha/status.json`. El backend lo lee y avisa al gerente si el respaldo no responde o si el vigilante dejó de informar (`ReadPairHealth` en `OffsiteBackup.cs`).

Cambios respecto al plan, por simplicidad:

- La réplica se autentica con contraseña SCRAM sobre TLS verificado contra la CA del principal (`verify-ca`), no con certificado de cliente. La contraseña viaja en el código de emparejamiento y nunca en argumentos de proceso.
- `listen_addresses = '*'`, filtrado por `pg_hba` y nftables: una IP fija haría fallar a PostgreSQL si la red sube tarde.
- La consola gráfica del respaldo pasa a la fase 4, junto con el botón de promoción. En la fase 1 el estado se ve con `culinaryos-ha status`.

Verificado:

- `tests/test_culinaryos_ha.py`, 16 pruebas. Una corre un principal y un respaldo reales con el PostgreSQL 17.11 del laboratorio:
  - un código equivocado no copia nada;
  - una venta confirmada ya está en el respaldo;
  - con el respaldo muerto, la venta espera y luego pasa;
  - el respaldo vuelve y se pone al día;
  - la recopia funciona;
  - el respaldo no acepta escrituras.
- Pruebas del instalador y del `.deb` sin regresiones.

Falta verificar:

- El simulacro con **dos VM de CulinaryOS**. Lo propio de Linux no se ha corrido: `runuser`, systemd, nftables con `culinaryos-firewall-apply`, rutas de Debian, `api/trust` real.
- `scripts/build-appliance.sh` de CulinaryOS no incluye todavía `ha.py` en la ISO: otra sesión tiene ese archivo con cambios sin commit. Los equipos lo reciben con el `.deb`.

## Meta y lo que no se busca

- **Meta:** si el equipo principal muere, un gerente en la sucursal pulsa un botón en el equipo de respaldo y en uno o dos minutos las terminales vuelven a vender, **sin perder operaciones confirmadas** (RPO 0) y **sin que haya dos servidores cobrando** al mismo tiempo.
- **No se busca:** conmutación automática. Lo descarta la auditoría: una elección automática de líder en un POS abre la puerta al cerebro dividido (folios duplicados, caja descuadrada). El respaldo nunca se promueve solo.
- **No se busca:** dos servidores activos. El respaldo sólo recibe; no atiende terminales hasta que lo promueven.

## Idea central

Para la aplicación, una promoción equivale a un **reinicio forzado del servidor**, caso que la pregunta 1 de la auditoría ya resuelve: el cliente guarda el UUID de cada operación, el timbrado a medias pasa a `pending-reconciliation`, el cobro sin respuesta queda `uncertain`, la impresión incierta pide revisión y el outbox reenvía al central, que deduplica por `(site_id, event_id)`. El trabajo nuevo está en cuatro piezas:

1. que el respaldo tenga **exactamente** lo que el principal confirmó (réplica síncrona);
2. que el principal **no pueda seguir vendiendo** cuando se promueve el respaldo (fencing);
3. que el respaldo tenga **las llaves** para abrir los datos (llavero) y una **licencia** que lo autorice;
4. que las terminales lleguen al nuevo equipo **por el mismo nombre TLS**.

## Topología propuesta

```
            LAN operativa (terminales, KDS, impresoras)
   ───────────┬───────────────────────────────┬───────────
              │ 10.77.0.11                    │ 10.77.0.12
       ┌──────┴──────┐  enlace del par  ┌──────┴──────┐
       │  PRINCIPAL  │◄════════════════►│  RESPALDO   │
       │ PostgreSQL  │ réplica síncrona │ hot standby │
       │ backend     │ + latido + llaves│ consola     │
       └──────┬──────┘                  └─────────────┘
              │ 10.77.0.10  ← IP de servicio (culinaryos.internal)
              │              sólo la tiene el equipo líder
```

- Cada equipo tiene su IP propia de administración (`.11` y `.12`) y **sólo el líder** tiene la **IP de servicio** (`.10`), que es a la que apunta `culinaryos.internal` en el `hosts` de las terminales (`RED-LOCAL.md`).
- **Enlace del par:** recomendado un cable directo entre los dos equipos (segunda tarjeta de red; sirve una USB) además de la LAN. Con dos caminos, que se corte uno no se confunde con que el otro equipo murió.
- El respaldo arranca CulinaryOS en **modo respaldo**: PostgreSQL en `hot_standby`, sin backend ni puerto 8443, sin anuncio DNS-SD y con una consola de estado en el kiosco.

## 1. Réplica de PostgreSQL 17 por streaming

`wal_level = replica` ya está en `CulinaryOS/os/overlay/etc/postgresql/17/main/conf.d/culinaryos.conf`. Hoy `listen_addresses = ''` (sólo socket). Se agrega un archivo aparte, `conf.d/culinaryos-ha.conf`, que sólo existe en un par:

| Parámetro | Valor | Por qué |
|---|---|---|
| `listen_addresses` | IP del enlace del par (y la de administración) | La réplica necesita TCP; las terminales siguen sin acceso a la base |
| `max_wal_senders` / `max_replication_slots` | 4 / 2 | Un respaldo y un margen para volver a clonar |
| Ranura física `culinary_standby` | | El principal no borra WAL que el respaldo no ha recibido |
| `max_slot_wal_keep_size` | 2 GB | Un respaldo apagado una semana no llena el disco del principal: pierde la ranura y se vuelve a clonar |
| `synchronous_standby_names` | `FIRST 1 (culinary_standby)` | Un commit confirmado ya está en los dos discos |
| `synchronous_commit` | `on` (ya está) | Con lo anterior, espera el `flush` del respaldo |
| `wal_log_hints` | `on` | Permite `pg_rewind` para reincorporar al antiguo principal sin clonar todo |
| `hot_standby` (respaldo) | `on` | La consola del respaldo lee estado y valida al gerente sin escribir |

- Autenticación de la réplica: `hostssl replication culinary_replica <IP del par>/32 cert` en `pg_hba.conf`, con certificado de cliente emitido por la CA de la sucursal. nftables abre 5432 **sólo** desde la IP del par y sólo en esas interfaces.
- Alta del par: `culinaryos-ha pair` en el respaldo hace `pg_basebackup -R -S culinary_standby` por el enlace, instala el `standby.signal` y deja chrony del respaldo siguiendo al principal (el reloj importa: `Licenses.cs` marca `clock` si la hora va 15 min atrás de lo ya firmado).
- Los registros llegan **sellados con el llavero**, como ya están en disco (`PostgresStore.cs`): la réplica no expone nada legible a quien robe el respaldo.

### Decisión clave: ¿qué pasa si el respaldo se cae?

Con réplica síncrona estricta, si el respaldo se apaga, **el principal deja de poder confirmar**: el restaurante se detiene por culpa del equipo que debía protegerlo. Alternativas:

| Opción | Pérdida posible (RPO) | Disponibilidad | Riesgo |
|---|---|---|---|
| A. Síncrona estricta | 0 | Una falla del respaldo detiene la venta | Inaceptable en un restaurante |
| **B. Síncrona con degradación automática (recomendada)** | 0 mientras el par está sano; segundos o más si el principal muere mientras está degradado | Una falla del respaldo no detiene la venta | La promoción debe detectar si el principal siguió vendiendo sin respaldo |
| C. Asíncrona siempre | Siempre unos segundos | Máxima | Folios repetidos, puntos de control R2 que no cuadran, cobros confirmados que desaparecen |

En B, un vigilante en el principal (dentro del backend o un servicio aparte) quita `synchronous_standby_names` si el respaldo no confirma en 10 s, lo anota en la bitácora (`ha.degraded`), avisa al gerente y al dueño («El equipo de respaldo está desconectado; hoy no hay respaldo») y lo restablece en cuanto el respaldo vuelve a estar al día (`ha.resynced`). Mientras está degradado, una promoción **puede perder** lo vendido después; por eso la promoción revisa la evidencia externa (sección 2).

## 2. Promoción manual de un botón, con fencing

### Quién y dónde

En el kiosco del respaldo, una consola de estado: «Respaldo al día · retraso 0,2 s · último dato 13:42:07». El botón **«Tomar el lugar del equipo principal»** pide usuario y contraseña de gerente. La consola los valida contra la réplica de sólo lectura (el respaldo tiene el llavero, sección 3). Opcional para una fase posterior: que el dueño lo autorice desde Culinary Dueño a través de CulinaryServer.

### Capas de fencing (todas, no una)

El cerebro dividido sólo ocurre si el principal **sigue vivo** cuando se promueve el respaldo. Ninguna señal sola lo descarta, así que se combinan:

1. **El principal no responde por ningún camino.** Ni por el enlace del par ni por la LAN (puerto de replicación, 8443 y ping). Si cualquiera responde, el botón se niega: «El principal sigue encendido; apágalo primero».
2. **La IP de servicio está libre.** `arping -D` sobre `10.77.0.10`: si alguien la contesta, el principal la sigue teniendo y la promoción se niega. Las terminales sólo llegan al líder por esa IP, así que un principal aislado que la conserve es exactamente el caso a impedir.
3. **Confirmación física.** La pantalla pide desconectar el cable de corriente del principal y escribir «APAGADO». En una sucursal los dos equipos suelen estar en el mismo mueble; es la capa que cubre el caso que las demás no ven (principal vivo detrás de un cable de red cortado).
4. **Opcional, fencing real (STONITH):** un enchufe inteligente con API local (sin nube) en la corriente del principal. La promoción lo apaga y comprueba el consumo en cero antes de seguir. Cuesta poco y elimina la confianza en el paso 3. Decisión del usuario: opcional o exigido.
5. **Evidencia externa, si hay internet.** Antes de promover, el respaldo compara su última secuencia de bitácora con:
   - el **último punto de control publicado en R2** (`Checkpoints.cs`): si R2 tiene una secuencia mayor que la del respaldo, el principal vendió algo que el respaldo no tiene;
   - la **última llamada del principal a CulinaryServer** (renovación cada 6 h, estadísticas, fallas): si fue posterior a la pérdida de la réplica, el principal seguía vivo.

   Si hay faltante, la pantalla lo dice («Perderías unas 14 operaciones registradas después de las 13:39») y exige una segunda confirmación con motivo, que queda en la bitácora.
6. **Época de liderazgo.** Cada promoción incrementa una época (`leaderEpoch`) guardada en la base y, si hay internet, firmada por CulinaryServer junto con el traspaso de licencia (sección 4). El backend la expone en `api/health`; cada cliente guarda la mayor que ha visto por sucursal y **rechaza** un servidor con época menor. Así una terminal que ya pasó al nuevo líder no vuelve al viejo aunque reaparezca.
7. **El principal que regresa no vende por su cuenta.** En un par, el backend no abre 8443 ni toma la IP de servicio hasta confirmar su liderazgo: pregunta al par por su época y a CulinaryServer por su licencia. Si el par tiene época mayor o la licencia dice `transferred` (ya existe: `released_spki` en `CulinaryServer/src/branch.ts`), se queda como **respaldo pendiente de reincorporar**. Si no puede preguntar a nadie (par apagado y sin internet), espera con una pantalla que pide a un gerente confirmar que el otro equipo está apagado.

### Secuencia de la promoción (`culinaryos-ha promote`)

1. Comprobaciones previas (capas 1, 2, 5) y confirmación (3 o 4).
2. Si hay internet: traspaso firmado en CulinaryServer (sección 4). Sin internet: promoción de emergencia con la autorización previa del respaldo.
3. `SELECT pg_promote(wait => true)`.
4. Arranca el backend con el llavero en custodia y su llave de licencia. Escribe `leaderEpoch = N+1` y la anota en la bitácora (`ha.promoted`, con quién, motivo y el faltante detectado).
5. Toma la IP de servicio y avisa con ARP gratuito (`arping -U`); arranca `culinaryos-publish` (DNS-SD).
6. Las terminales reconectan por `culinaryos.internal` (mismo nombre TLS, misma CA), ven la nueva época y consultan los recibos de sus UUID pendientes, como tras un reinicio.
7. Después: publica un punto de control R2 forzado, registra su propia llave de recepción fiscal (`/fiscal/intake-key`) y avisa al dueño («La sucursal cambió de servidor a las 13:44»).

**Cambio planeado** (mantenimiento, cambiar disco): el mismo botón en el principal hace la transición limpia. El principal deja de aceptar operaciones, espera que el respaldo esté al día, se degrada a respaldo y entonces el otro se promueve. Sin pérdida y sin riesgo; sirve también para regresar al equipo original.

**Reincorporar al antiguo principal:** `pg_rewind` contra el nuevo líder. Si tenía commits que el líder no tiene (sólo en el caso degradado), antes se guarda un `pg_dump` de esa rama como evidencia para soporte. **Nunca se fusiona automáticamente.**

## 3. Custodia del llavero en el respaldo

Hoy el llavero vive en `/etc/culinaryos/secrets/branch.keys.json` y llega al backend como credencial de systemd `keyring` (`LoadCredential=keyring:…`, leído en `src/MesaNexo.Api/Program.cs`). Sin él los registros replicados no se abren. El respaldo lo necesita **antes** del primer registro sellado con cada llave nueva.

| Opción | Cómo | A favor | En contra |
|---|---|---|---|
| A. Kit al emparejar | `culinaryos-recovery-kit` ya empaqueta `secrets/branch.keys.json` cifrado; el respaldo lo importa una vez | Reusa lo que existe | Una rotación (`culinaryos-rekey`) deja al respaldo sin la llave nueva hasta que alguien repita el kit: los registros nuevos no se abren tras promover |
| **B. Custodia por la réplica (recomendada)** | El respaldo genera al emparejar un par de llaves de custodia (en su TPM si lo tiene). El principal guarda en una tabla `ha_custody` el llavero **sellado a la llave pública del respaldo**, en la misma transacción o en una anterior a cualquier uso de una llave nueva | El orden del WAL garantiza que el respaldo recibe la llave antes que el primer registro sellado con ella. Las rotaciones llegan solas | Una tabla más; hay que excluirla de la copia externa |
| C. Canal propio entre equipos | Servicio mTLS que empuja el llavero | Independiente de PostgreSQL | Otro servicio, otro orden que coordinar con la rotación |

Detalles de B:

- Un servicio pequeño en el respaldo lee `ha_custody` (hot standby), la abre con su llave de custodia y escribe `/etc/culinaryos/secrets/branch.keys.json` con modo `0600` y dueño `root`, igual que en el principal. Al promover, `LoadCredential` lo entrega al backend sin cambios.
- La rotación pasa a dos fases cuando hay par: primero se agrega la llave nueva a `ha_custody` y se espera la confirmación del respaldo; luego se activa. Si no hay respaldo al día, la rotación se niega o pide confirmación.
- `pg_dump` de la copia externa (`OffsiteBackup.cs`) usa `--exclude-table-data=ha_custody`, para que R2 nunca tenga ni siquiera el llavero sellado.
- Viaja en la misma custodia lo que el respaldo necesita para operar: credenciales de R2 (`checkpoints.json` y el bucket de copias), mTLS del central (`central-client.key`) e integraciones (Facturama, terminales bancarias). La llave de la CA de la sucursal también viaja, para que el nuevo líder renueve su certificado (`renew-tls.py`).
- **No viaja** la llave de licencia: cada equipo tiene la suya, en su TPM o ligada a su huella. **Tampoco** la llave de recepción del CSD: las entregas del CSD que estén pendientes al promover se pierden y el dueño las reenvía desde Culinary Dueño (el CSD vive sólo en Facturama, así que no se pierde nada fiscal).

## 4. Licencia

Hoy la licencia se liga a **una** llave (`payload.key.spki` en `LicenseView.Evaluate`, `src/MesaNexo.Core/Licensing.cs`) y CulinaryServer guarda una sola (`key_spki`, más `released_spki` al reiniciar la activación).

| Opción | Cómo | A favor | En contra |
|---|---|---|---|
| A. Dos equipos autorizados | La licencia lista la llave del principal y la del respaldo | La promoción funciona sin internet | La licencia deja de ser un freno: con una sola licencia podrían operar dos sucursales. Sólo lo detecta la comparación de puntos de control y las renovaciones dobles |
| B. Traspaso firmado | Al promover, el respaldo pide a CulinaryServer que mueva la licencia a su llave; la vieja pasa a `released_spki` y se bloquea en su siguiente contacto (`license_transferred`, ya existe) | CulinaryServer es un árbitro independiente: emite la época firmada y deja constancia | Sin internet no hay promoción, y un apagón suele llevarse el internet |
| **C. Híbrido (recomendado)** | Al emparejar, el principal registra en CulinaryServer la llave del respaldo como **suplente**. Promoción en línea: traspaso firmado (B). Sin internet: el suplente puede operar como máximo **72 h** con una época local, y al volver el internet CulinaryServer formaliza el traspaso | Funciona en apagón; CulinaryServer sigue siendo el árbitro final | Más casos que probar |

Cambios para C:

- **CulinaryServer** (`src/branch.ts`, `src/admin.ts`, migración D1):
  - columnas `standby_alg`, `standby_spki`, `standby_binding…` y `leader_epoch` en `license`;
  - `PUT /branch/v1/standby`: la firma la llave del principal y registra la del respaldo con su vínculo (TPM o huella). Requiere que el plan lo permita (Admin);
  - `POST /branch/v1/failover`: la firma la llave del suplente. Intercambia `key_*` ↔ `standby_*`, mueve la vieja a `released_*`, sube `revision` y `leader_epoch` y devuelve la licencia nueva y la época firmada;
  - `reset-activation` limpia también al suplente;
  - el JWS de licencia agrega `standby` (spki y horas de operación sin conexión) y `epoch`.

  Las migraciones deben ser compatibles con las sucursales actuales y **el push a `main` despliega**: primero el servidor, con el visto bueno del usuario; después la sucursal.
- **Sucursal** (`Licenses.cs`, `Licensing.cs`): `Evaluate` acepta la llave del suplente sólo si la base tiene una promoción de emergencia con menos de 72 h; `Activate` y `Renew` sin cambios para el principal.
- **CulinaryAdmin:** mostrar el par, el estado del suplente y la última época; botón para revocar al suplente.
- **Comercial:** ¿el respaldo se cobra aparte? Afecta el plan y Admin; lo decide el usuario.

## 5. Mover el nombre TLS al equipo promovido

Los clientes validan nombre y CA, no IP (`CERTIFICADOS-LOCALES.md`, `MesaNexo.Client/Connection.cs`). Recomendado: **IP de servicio flotante** (sección 2). Así `culinaryos.internal` no cambia en ningún `hosts` ni en el DNS del router.

- **Certificado:** cada equipo tiene **su propia llave de servidor** y un certificado de la CA de la sucursal con los mismos SAN (`culinaryos.internal`, `10.77.0.10`). Para el cliente es el mismo servidor; si un día se roba la llave del respaldo, se revoca sólo esa.
- **Alternativa descartada:** cambiar la IP en el `hosts` de cada terminal o en el DNS del router. Toca cada equipo y deja de funcionar justo cuando el router es lo que falló.
- **Alternativa aceptable:** misma llave y mismo certificado en los dos equipos. Es más simple, pero una fuga afecta a ambos.
- DNS-SD (`culinaryos-publish`) sólo corre en el líder, como ya pasa: anuncia sólo un backend registrado y disponible.
- La CA en rotación (`rotate-ca.py`) debe emitir también para el respaldo; se agrega al procedimiento.

## Riesgos

| Riesgo | Cómo pasa | Mitigación |
|---|---|---|
| **Cerebro dividido** | Principal vivo pero aislado (cable de red cortado) y se promueve el respaldo | Capas 1 a 7 de fencing: el principal aislado conserva la IP de servicio y la promoción la ve ocupada (capa 2); confirmación física o enchufe inteligente; época en clientes; el principal que regresa no vende sin confirmar liderazgo; traspaso de licencia que bloquea la llave vieja |
| **Pérdida de operaciones confirmadas** | Principal degradado (respaldo caído) que luego muere | Aviso permanente mientras está degradado; la promoción compara contra R2 y CulinaryServer y exige confirmación con motivo; la rama vieja se guarda como evidencia |
| **Folios repetidos** | Consecuencia de la anterior: el nuevo líder reusa números que el viejo ya imprimió o timbró | RPO 0 en operación normal. Si hubo faltante, el nuevo líder adelanta sus contadores de folio con un margen fijo y lo anota (el punto de control R2 trae la secuencia de bitácora, no los folios). Los timbrados se concilian con Facturama antes de emitir |
| **Outbox duplicado** | El principal entregó un evento y murió antes de confirmar su marca de entregado | Ya cubierto: entrega «al menos una vez» y deduplicación por `(site_id, event_id)` en el central (`SINCRONIZACION-V1.md`). Verificar que **todos** los destinos del outbox deduplican, no sólo el central |
| **Outbox perdido** | Eventos de commits que el respaldo no recibió (caso degradado) | Mismos que la pérdida de operaciones; el outbox nace en la misma transacción que la operación, así que nunca hay operación sin evento ni evento sin operación |
| **Puntos de control R2 que no cuadran** | Promoción con faltante: R2 tiene cabezas que el nuevo líder no tiene, y `CompareCheckpoints` detiene autorizaciones | Esperado y correcto: la promoción lo anticipa y pide aceptarlo con `AcceptCheckpoints` y motivo. Con RPO 0 la cadena continúa igual y nada salta. El punto de control cambia de `spki` (la llave del suplente) y `Checkpoints.Signed` lo acepta |
| **Respaldo sin llave nueva** | Rotación del llavero sin respaldo al día | Custodia por la réplica (opción B) y rotación en dos fases |
| **Disco lleno en el principal** | Ranura de réplica con el respaldo apagado días | `max_slot_wal_keep_size` y alerta; el respaldo se vuelve a clonar |
| **Reloj del respaldo atrasado** | Licencia en estado `clock` al promover | chrony del respaldo sigue al principal; la consola alerta si se desvía más de 1 min |
| **Robo del respaldo** | Es otro equipo con datos y llaves | Igual que el principal: LUKS, llavero sellado a su TPM o a su llave de custodia, licencia propia revocable |
| **Cobro con terminal bancaria a medias** | El principal murió esperando respuesta de la terminal | Ya cubierto: queda `uncertain` en la base replicada y bloquea otro cobro en esa cuenta |

## Fases (cada una se aprueba por separado)

| Fase | Repositorio | Contenido | Prueba que la cierra |
|---|---|---|---|
| 0 | — | Decisiones de la lista de abajo | — |
| 1 | CulinaryOS | `culinaryos-ha.conf`, `pg_hba`, nftables, `culinaryos-ha pair`, modo respaldo y consola de estado | Dos VM: réplica síncrona, degradación en 10 s, reincorporación |
| 2 | MesaNexoNativo + CulinaryOS | `ha_custody`, servicio de custodia, rotación en dos fases, exclusión en la copia externa, certificado del respaldo | Rotar el llavero con el par activo y promover: todo abre |
| 3 | CulinaryServer, MesaNexoNativo, CulinaryAdmin | Suplente, `failover`, época y JWS extendido. **Despliegue del servidor sólo con aprobación** | Traspaso en línea y de emergencia; el viejo se bloquea con `license_transferred` |
| 4 | CulinaryOS + MesaNexoNativo | `culinaryos-ha promote`, comprobaciones de fencing, IP flotante, liderazgo al arrancar, cambio planeado, `pg_rewind` | Simulacros de la tabla de abajo |
| 5 | MesaNexo.Client, Android | Época en `api/health`, rechazo de época menor, reconexión con recibos de UUID | Terminal a mitad de un cobro durante la promoción |

Simulacros en VM antes de aprobar para producción: apagar el principal a mitad de un cobro; cortar sólo el cable del par; cortar sólo la LAN del principal; apagar el respaldo (degradación) y luego el principal; rotar el llavero; regresar al principal viejo sin internet; cambio planeado de ida y vuelta. Conviene hacer en la misma tanda el simulacro de corte eléctrico que sigue pendiente desde 0.2.0.

## Decisiones

El usuario aprobó el 6 de noviembre la opción recomendada en cada una. La 6 (comercial) sigue abierta y hace falta antes de la fase 3.

1. **Política si cae el respaldo:** síncrona con degradación automática (recomendada), estricta o asíncrona.
2. **Licencia:** híbrido con 72 h de emergencia (recomendado), sólo traspaso en línea o dos equipos siempre autorizados.
3. **Enchufe inteligente:** opcional (recomendado) o exigido para habilitar el par.
4. **Enlace del par:** cable directo además de la LAN (recomendado) o sólo LAN.
5. **Quién promueve:** gerente en sitio (recomendado para empezar); el dueño a distancia, en una fase posterior.
6. **Comercial:** ¿el respaldo es parte del plan o se cobra aparte?

## Hallazgo aparte (no es parte de este plan)

`Checkpoints.Signed` (`src/MesaNexo.Core/Checkpoints.cs`) verifica cada punto de control con la `spki` que trae el propio objeto. Quien tenga las credenciales del bucket puede publicar puntos de control con cualquier llave. No falsifica una cadena (una cabeza distinta dispara la alerta), pero sí permite provocar alertas y detener autorizaciones. Conviene ligar la verificación a las llaves que la licencia autoriza (principal y suplente).
