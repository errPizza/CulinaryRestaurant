# Plan del panel del propietario

Objetivo: que el dueño vea desde su teléfono o su PC con Windows cómo va el negocio, en todas sus sucursales juntas o en cada una, con cifras que coincidan al centavo con los cortes de la sucursal.

## Decisiones tomadas

| Tema | Decisión |
|---|---|
| Aplicación | App propia para Android y Windows (Tauri + React). iOS después, porque requiere Mac y cuenta Apple |
| Datos | CulinaryServer: el Worker y D1 ya desplegados. `MesaNexo.Central` no se usa |
| Frescura | Entre 1 y 5 minutos, con «último dato hace X» por sucursal |
| Detalle en la nube | Sólo resúmenes: sin clientes ni tickets individuales |
| Primera versión | Ventas y operación, productos y rentabilidad, caja y personal, comparativas |
| Usuarios | Dueño y gerentes limitados a sus sucursales |
| Planes | Lite: reportes clave. Pro: módulo completo y bitácoras |
| Alertas push | Segunda fase |

## Punto de partida

- `license.tenant_id` ya agrupa las sucursales de un negocio, y `license.tier` distingue Lite de Pro.
- `/branch/v1/*` ya autentica cada sucursal con la llave de su licencia y un reto de un solo uso; `/branch/v1/faults` es el modelo a seguir para la subida.
- `Reports.cs` calcula reportes para el gerente sobre `visit` y `payment`, y `Domain.Totals` tiene las reglas de dinero. Los resúmenes deben reutilizarlas, no recalcular.
- `CommerceService.Analytics` sirve de referencia de métricas, pero exige `MesaNexo.Central`.
- CulinaryAdmin es la base de la app: Tauri, Android, Windows y compilación firmada.
- Falta todo lo propio del propietario: sus cuentas, los resúmenes y el almacenamiento en la nube.

## Arquitectura

```
Sucursal (PostgreSQL)            CulinaryServer (Worker + D1)          App del propietario
Eventos -> resúmenes por hora -> POST /branch/v1/stats  ->  stats_day <- GET /owner/v1/*
          cola durable           firmado con la licencia               cuenta y alcance
```

**Sucursal.** `OwnerStats` en `MesaNexo.Core`, función pura y probada, y `SendStats` en `MesaNexo.Api`, dentro de `LicenseWorker`.

- Agrupa por hora y por día de negocio, con la zona y la hora de corte de cada sucursal: una venta a la 1:00 cuenta en la noche anterior.
- Una reapertura o una devolución tardía marca su día como pendiente y el día se reenvía completo.
- Lo pendiente sobrevive sin internet y a reinicios, porque se deduce comparando cada día con lo ya entregado. Al volver, se pone al día en lotes acotados.

**Nube.** Migración `0006` con `stats_day`, `branch_heartbeat`, `owner_account`, `owner_membership`, `owner_session` y `owner_invite`.

- Una fila por sucursal y día, con los totales en columnas y las secciones en JSON que D1 suma con `json_each`. Se descartaron las filas por hora y producto porque cada envío reescribiría unas 200 filas: unas 96 000 escrituras al día por sucursal contra 480.

- La escritura es idempotente: cada día de negocio se reemplaza con número de revisión, y una revisión anterior se ignora.
- Dinero en centavos enteros, como en la sucursal.
- Retención: día por 25 meses y detalle de productos por 13, sin resumen mensual posterior. Estimado, no medido con datos reales: unos 30 KB por día de una sucursal con 150 productos vendidos, o unos 15 MB por sucursal en régimen. El límite de 10 GB de D1 alcanzaría para cientos de sucursales.
- Cada consulta se filtra en el servidor por el alcance de la cuenta. La app nunca recibe datos de sucursales ajenas.

**App.** Culinary Dueño, `mx.culinaryrestaurant.owner`, en `Desktop/CulinaryDueno`. Selector «Todas las sucursales» o una sola, y periodo: hoy, ayer, 7 días, este mes o fechas libres de hasta 366 días. Secciones Resumen, Productos, Caja y Personal, abajo en el teléfono y en la barra superior en escritorio.

- Hoy se compara con ayer hasta la misma hora del día de negocio. Ayer, con el mismo día de la semana pasada; 7 días y las fechas libres, con el tramo igual anterior; el mes, con los mismos días del mes pasado.
- Más de un día se ve por día, y por mes si pasa de 62 días, junto con las horas pico.
- Las fechas salen del día de negocio que reporta cada sucursal, no del reloj del teléfono.
- La sesión dura 30 días y se guarda en el almacenamiento privado del WebView. Sólo da acceso a resúmenes.
- Si el instalador no trae la dirección de CulinaryServer, la app la pide una vez.

- Consolidado: suma de sucursales. El ticket promedio se recalcula con totales, nunca promediando promedios.
- Si una sucursal está atrasada, el consolidado lo dice y la identifica.
- Interfaz de consulta, sin operaciones sobre la sucursal.

## Contenido por plan

| Área | Lite | Pro |
|---|---|---|
| Ventas, ticket promedio, comensales, cuentas y mesas abiertas | Sí | Sí |
| Horas pico, cancelaciones, descuentos | No | Sí |
| Ranking de sucursales y comparación con ayer | Sí | Sí |
| Comparación por semana y mes, rangos libres | No | Sí |
| Más y menos vendidos | Sí | Sí |
| Costo, margen, mermas, inventario bajo | No | Sí |
| Cortes, diferencias de arqueo, retiros, propinas, ventas por mesero | No | Sí |
| Bitácoras y exportación | No | Sí |

En un negocio con planes mixtos, cada función Pro incluye sólo las sucursales Pro y el consolidado lo indica. El servidor hace el recorte: Lite lee su mes en curso o sus últimos 7 días, sólo trae horas de un día para comparar con ayer, y queda fuera de canales, pagos, descuentos, cancelaciones, costo, margen, inventario, caja, cortes y personal. Cada respuesta dice qué sucursales Lite no entran (`lite`) y cuáles quedaron fuera por el periodo (`outOfRange`); la app compara periodos sólo con las sucursales presentes en ambos. Bitácoras y exportación aún no existen.

## Fases

Estimación para una persona a tiempo completo: unas 7 semanas hasta el piloto.

| Fase | Trabajo | Duración | Listo cuando |
|---|---|---|---|
| 0. Definiciones (hecha, [METRICAS-PANEL.md](METRICAS-PANEL.md)) | Fórmula, zona horaria y día de negocio de cada métrica; contrato JSON; datos de prueba | 3 días | Cada cifra del panel tiene una definición escrita |
| 1. Resúmenes (hecha, falta conectar el servidor en la fase 2) | `OwnerStats`, `SendStats`, zona y corte del día configurables, reproceso tardío y pendientes | 1 semana | El vector `owner-stats` y `report.z` reales cuadran, incluso con reaperturas |
| 2. Nube y cuentas (hecha, sin desplegar) | Migración, ingesta, cuentas, alcance y consultas | 1 semana | Una cuenta no puede leer otro `tenant_id` ni otra sucursal |
| 3. App base (hecha en `Desktop/CulinaryDueno`, sin probar en dispositivo) | Proyecto, acceso, selector, resumen y estado de sucursales | 1.5 semanas | Consolidado e individual en Android y Windows, instaladores firmados |
| 4. Detalle (hecha en la app, probada con servidor simulado) | Productos, rentabilidad, caja, personal y comparativas | 1.5 semanas | Las cuatro áreas funcionan a 360 px y en escritorio |
| 5. Comercial (hecha sin desplegar; el aviso es borrador, [AVISO-PRIVACIDAD-PANEL.md](AVISO-PRIVACIDAD-PANEL.md)) | Lite/Pro, pantallas de invitación en Culinary Admin, aviso de privacidad | 3 días | Una licencia Lite no recibe datos Pro desde el servidor |
| 6. Piloto | Negocio real con dos sucursales, conciliación diaria | 1 semana | Siete días seguidos cuadrando al centavo |
| 7. Alertas (fase 2) | Sucursal desconectada, cancelaciones, diferencias de caja, con FCM y APNs | Por estimar | Se planea tras el piloto |

## Pruebas clave

- Conciliación contra el corte: consolidado igual a la suma de sucursales e igual a cada corte.
- Corte de internet durante horas, servicio reiniciado, reenvío duplicado y fuera de orden.
- Venta que cruza medianoche, cancelación después del cierre, sucursales en distinta zona horaria.
- Aislamiento: gerente, otro negocio, licencia vencida o revocada, sesión caducada.
- Volumen: un negocio con 20 sucursales y un año de historia responde sin esperas notables.

## Riesgos

- **Cifras distintas al corte.** Es lo que más daña la confianza: por eso el piloto exige conciliación exacta. Una cuenta reabierta tras un corte queda en dos cortes Z; el panel la cuenta una vez y lo explica en cada corte.
- **Datos de empleados.** Las ventas por mesero suben un identificador y un nombre visible, que son datos personales aunque el resto sean resúmenes. Hay que reflejarlo en el aviso de privacidad.
- **Margen.** Sólo es tan bueno como las recetas y los costos capturados: se muestra como estimado y señala productos sin receta.
- **Despliegue.** Cada push a `main` de CulinaryServer va a producción, así que la migración debe ser compatible con sucursales que aún no suben datos.
- **Base sin validar.** Las sucursales 0.3.3 aún no se han probado de punta a punta contra el servidor. Conviene cerrar esa prueba antes de la fase 2.

## Decisiones pendientes

1. **Nombre de la app.** Se usa la propuesta, «Culinary Dueño». Cambiarlo es renombrar el producto y el instalador; el identificador `mx.culinaryrestaurant.owner` no cambia.
2. **Correo.** El acceso ya es correo, contraseña y TOTP. Falta elegir un servicio de correo para que cada persona recupere su cuenta; mientras tanto, el proveedor la elimina y la invita de nuevo.
3. **Distribución Android.** Propuesta: Google Play, por sus actualizaciones automáticas. El APK directo sirve para el piloto.
4. **Actualización en Windows.** Propuesta: actualizador de Tauri con firma propia, porque Culinary Admin demuestra lo incómodo que es no tenerlo.
5. **Piloto.** Qué negocio con dos sucursales lo prueba.
