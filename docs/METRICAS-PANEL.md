# Métricas y contrato del panel del propietario

Fases 0 y 1 de [PLAN-PANEL-PROPIETARIO.md](PLAN-PANEL-PROPIETARIO.md). Cada cifra del panel se define aquí y `src/MesaNexo.Core/OwnerStats.cs` la calcula. El caso calculado a mano está en `tests/vectors/owner-stats/v1.json`: `tests/test_owner_stats_vectors.py` lo recalcula por separado y `OwnerStatsTests` lo compara con `OwnerStats`, junto con cortes `report.z` reales.

## Reglas generales

- **Fuente de verdad:** `ShiftSummary` en `src/MesaNexo.Core/Closings.cs`, el cálculo del corte X y Z. `Domain.Totals` da el importe de cada cuenta. `OwnerStats` debe reutilizarlos.
- **`Reports.cs` no sirve de base.** Filtra por `openedAt`, mientras el corte usa `closedAt`: una cuenta que cruza medianoche cambiaría de día.
- **Dinero:** centavos enteros en `long`, precios con IVA incluido, y redondeo de `Cents`. Ver `docs/POLITICA-MONETARIA.md`.
- **Moneda:** MXN.
- **Una venta cuenta al cerrarse**, por `closedAt`, y sólo si `status = closed`.
- **Un cobro cuenta cuando se registra**, por el `at` del pago. Por eso ventas y cobrado pueden diferir en un día: un pago de las 23:59 con cierre a las 00:01, si el corte del día cae entre ambos.

## Día de negocio

```
fecha local  = at convertido a la zona horaria de la sucursal
día negocio  = fecha de (fecha local - corte del día)
hora         = hora del reloj local, 0 a 23
```

Corte del día predeterminado: 04:00. Una venta a la 01:30 del sábado pertenece al viernes, en la hora 1. Una a las 04:30 pertenece al sábado.

`timeZone` y `businessDayCutoffMinutes` son ajustes de la sucursal, en Configurar restaurante, con `America/Mexico_City` y 240 por omisión. La zona rige también las promociones, las listas de precios, los descuentos de nómina, la fecha fiscal y el inicio del primer corte Z. Cambiarlas vuelve a fechar toda la historia del panel; los cortes Z ya hechos no cambian.

## Ventas y operación

| Métrica | Definición | Campo del corte |
|---|---|---|
| Cuentas | Número de `visit` cerradas | `tickets` |
| Comensales | Suma de `people` | `people` |
| Venta bruta | Unidades no canceladas ni `unavailable`, más `deliveryFee` | `gross` |
| Descuentos | `Totals.discount` | `discounts` |
| Ventas | Bruta menos descuentos. Incluye IVA y no incluye propinas | `net` |
| IVA incluido | `Totals.tax` | `tax` |
| Envío | Suma de `deliveryFee` | No aparece aparte |
| Ticket promedio | Ventas entre cuentas, truncado | `average` |
| Promedio por comensal | Ventas entre comensales, truncado | No aparece |
| Cancelaciones | Número e importe de unidades `cancelled` o `unavailable` en cuentas cerradas | `voidedCount`, `voidedAmount` |
| Cortesías | Cuentas con descuento del 100 % | `comps` |
| Comidas de empleado | Cuentas con `channel = staff` | `staffMeals` |
| Canal | `channel` de la cuenta. Vacío significa `table` | No aparece |
| Horas pico | Ventas y cuentas por hora del reloj local | No aparece |

Las devoluciones no reducen las ventas: se muestran aparte, igual que en el corte. Las comidas de empleado y las cortesías cuentan como cuentas y comensales; el panel las identifica porque distorsionan el ticket promedio.

## Cobros y caja

| Métrica | Definición |
|---|---|
| Cobrado | Suma de `amount` de los pagos, por método |
| Propinas | Suma de `tip` de los pagos, por método y por mesero de la cuenta. Cuentan el día del pago |
| Devoluciones | Suma de `refund.amount` por su `at`. Pueden incluir propina, porque devuelven lo cobrado |
| Movimientos | `movement` de tipo `income`, `expense` y `withdrawal`, por su `at` |
| Diferencia de arqueo | `counted - expected` de cada cierre, en efectivo, tarjeta y transferencia, sin recalcular |
| Ajuste de cierre | `cashadjustment`, mostrado junto al cierre. No cambia la diferencia original |

Una devolución en efectivo ya genera un `movement` de tipo `expense`. Sumar devoluciones y gastos la contaría dos veces, así que `expense` se envía sin los movimientos con `refundId`. Por eso puede ser menor que el `expense` del corte, que sí los incluye.

## Productos y rentabilidad

| Métrica | Definición |
|---|---|
| Cantidad e ingreso | Unidades vendidas y suma de `price`, como en `report.abc`. Antes de descuentos y sin envío |
| Más y menos vendidos | Orden por ingreso o cantidad, con clases ABC calculadas en la app |
| Costo unitario | `PortionCost`: receta vigente a costo de existencias. Sin receta, `null` |
| Margen estimado | Ingreso menos costo unitario por cantidad, sólo para productos con receta |
| Merma, faltante y sobrante | Costo de `stockmovement` de tipo `waste`, `count-shortage` y `count-surplus`, en positivo |
| Inventario bajo | Ingredientes con `disponible + entrante <= minStock`. Es una foto actual, no un dato del día |

El margen ignora descuentos y envío, por lo que el panel lo rotula «estimado». `PortionCost` usa el costo actual: el valor queda fijo cuando el día se marca `complete`, y un día anterior no se revalúa.

## Personal

Por `waiterId` de la cuenta: cuentas y ventas al cerrar, propinas al cobrar. Un mesero puede tener propinas de un día sin cuentas cerradas en él. Se envían `staffId` y nombre visible, que son datos personales: deben aparecer en el aviso de privacidad.

## Comparativas y consolidado

- **Consolidado:** se suman las cifras base, nunca los promedios. El ticket promedio es la suma de ventas entre la suma de cuentas.
- **Ranking:** por ventas del periodo, con sucursales sin datos al final y señaladas.
- **Contra periodo anterior:** el mismo tramo. Hoy hasta las 15:00 contra ayer hasta las 15:00 de día de negocio, usando filas horarias.
- **Comparación semanal:** mismo día de la semana anterior.
- **Sucursal atrasada:** más de 15 minutos sin contacto. El consolidado lo indica y su cifra se marca como parcial.

## Conciliación con el corte Z

Cada fila horaria lleva `z`: el número del corte que cubre esa venta, o el siguiente si aún no se hace. Así las horas de un corte se pueden sumar y comparar con él, aunque el corte no coincida con la medianoche. `z = 0` marca ventas anteriores al primer corte, que `LastZ` no incluye porque arranca en la medianoche de ese día.

**Reapertura.** `visit.reopen` devuelve a abierta una cuenta cerrada y al volver a cerrarla cambia su `closedAt`. El corte Z es inmutable, así que si la cuenta ya estaba en un corte, queda contada en dos:

| | Corte congelado | Panel |
|---|---|---|
| Corte original | Sigue incluyendo la cuenta | Ya no la incluye |
| Corte posterior | La incluye de nuevo | La incluye |
| Suma | Cuenta doble | Cuenta una vez |

Decisión: el panel muestra el estado actual y cada corte conserva sus cifras congeladas junto a la diferencia por reaperturas. Una diferencia sin reapertura que la explique es un error y debe alertar. Sumar cortes Z a mano seguirá dando más que el panel en estos casos.

## Contrato de subida

`POST /branch/v1/stats`, firmado con la llave de la licencia como `/branch/v1/faults`. Unidad de envío: el día de negocio completo, que reemplaza al anterior.

```json
{
  "schema": 1,
  "sentAt": "2026-11-01T09:05:00Z",
  "branch": { "timeZone": "America/Mexico_City", "businessDayCutoffMinutes": 240, "currency": "MXN", "appVersion": "0.3.3", "lastZ": 2 },
  "live": {
    "at": "2026-11-01T09:05:00Z", "openAccounts": 0, "occupiedTables": 0, "openAmount": 0,
    "pendingRequests": 0, "cashOpen": false,
    "lowStock": [{ "ingredientId": "...", "name": "...", "unit": "g", "available": 0, "min": 0 }]
  },
  "days": [{
    "businessDate": "2026-10-30", "revision": 42, "complete": true,
    "hours": [{ "hour": 20, "z": 1, "tickets": 1, "people": 3, "gross": 0, "discounts": 0, "net": 0, "tax": 0, "deliveryFees": 0, "voidedCount": 0, "voidedAmount": 0, "comps": 0, "staffMeals": 0 }],
    "payments": [{ "hour": 20, "method": "card", "amount": 0, "tips": 0, "count": 1 }],
    "refunds": [{ "method": "card", "amount": 0, "count": 1 }],
    "channels": [{ "channel": "table", "tickets": 1, "net": 0 }],
    "products": [{ "productId": "...", "name": "...", "category": "...", "quantity": 1, "revenue": 0, "unitCost": null }],
    "staff": [{ "staffId": "...", "name": "...", "tickets": 1, "net": 0, "tips": 0 }],
    "cash": { "income": 0, "expense": 0, "withdrawal": 0 },
    "cashCloses": [{ "cashId": "...", "closedAt": "...", "blind": true, "cashDifference": 0, "cardDifference": 0, "transferDifference": 0, "adjustments": 0 }],
    "inventory": { "waste": 0, "shortage": 0, "surplus": 0 },
    "zReports": [{ "number": 1, "from": "...", "to": "...", "tickets": 0, "people": 0, "gross": 0, "discounts": 0, "net": 0, "tax": 0, "payments": 0, "tips": 0, "refunds": 0, "digest": "..." }]
  }]
}
```

Reglas:

- `revision` crece por sucursal: son los segundos Unix o la última más uno, de modo que un respaldo restaurado o un registro perdido aún supera lo guardado. El servidor ignora una revisión menor o igual a la guardada, así que reenviar es seguro y el orden no importa.
- Respuesta: `{ "accepted": ["2026-10-30"], "ignored": [], "rejected": [] }`. Un día queda pendiente en la sucursal hasta aparecer en `accepted` o `ignored`. Uno en `rejected` no cuadró: el servidor abre un fallo y los demás días siguen.
- Máximo 10 días por petición, los más recientes primero, con una pausa de 2 s entre lotes. Un `zReports` pertenece al día de negocio de su `to`. Sólo se envían los últimos 762 días, los 25 meses de retención.
- Un día que se quedó sin actividad, por una reapertura o un cambio de corte, se envía vacío con `complete: true` para reemplazar lo publicado.
- Una ruta `404` no es un fallo: la sucursal conserva los días y reintenta. Así convive con un servidor que aún no tiene la fase 2 desplegada.
- Cuerpo máximo de 2 MiB. El servidor compara las identidades 1, 2, 3 y 5; la 4 necesita varios días y la comprueba la sucursal.
- `complete: true` significa que el día terminó y no tiene cuentas abiertas de ese día. Una reapertura lo vuelve a `false`.
- La sucursal envía cada 3 minutos, con `days` vacío si nada cambió: `live` demuestra que sigue viva.
- Cada envío suma los últimos 14 días y toda la historia una vez por hora, porque 100 000 cuentas tardan 5.6 s en Release. Un cambio en un día más viejo, por una reapertura o un ajuste, puede tardar hasta una hora en subir.
- `unitCost` queda fijo en cuanto el servidor recibe el día `complete`.
- Sólo pueden subir sucursales con licencia utilizable. Con licencia vencida, lo ya subido sigue consultable.

Nunca se envían clientes, teléfonos, direcciones, RFC, tickets individuales, notas, `trackingKey`, tarjetas ni referencias de pago.

## Identidades que debe cumplir un día

1. Suma de `hours.net` = suma de `channels.net` = suma de `staff.net`.
2. Suma de `products.revenue` + suma de `hours.deliveryFees` = suma de `hours.gross`.
3. `gross - discounts = net` en cada fila.
4. Por cada corte: horas con ese `z` + diferencia por reaperturas = cifras congeladas.
5. Suma de `payments.tips` = suma de `staff.tips`.

Sucursal y servidor las comprueban. Si fallan, el día no se publica y se abre un reporte de fallo. La identidad 4 se aplica así en la sucursal: un corte cuyas ventas y cuentas difieren del panel sin ninguna reapertura posterior a su inicio detiene el día de su `to`.

## Envío en la sucursal

`BranchService.SendStats`, en `src/MesaNexo.Api/OwnerStatsDelivery.cs`, corre dentro de `LicenseWorker`. No hay una cola aparte: `owner-stats.json` guarda el hash de cada día entregado, y todo día cuyas cifras difieran de ese hash está pendiente. Es un archivo y no un registro del almacén porque guardar cada envío llenaría la bitácora y despertaría a las terminales. Si se pierde, los días se reenvían, y los días terminados toman el costo actual en lugar del publicado.
