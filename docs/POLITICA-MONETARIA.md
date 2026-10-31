# Política monetaria y fiscal

Reglas únicas del dinero en Culinary Restaurant. Están implementadas en `src/MesaNexo.Core/Cents.cs`; ningún otro código redondea importes de venta. Requieren revisión de un contador antes de un piloto con facturación real.

## Reglas

| Tema | Regla |
|---|---|
| Unidad | Centavos enteros de MXN, en `long`. Nunca flotantes. |
| Precios | Incluyen IVA. La tasa se congela en cada unidad vendida, y cambiar el producto no toca lo vendido. |
| Redondeo | Al centavo más cercano. Medio centavo se aleja del cero. |
| IVA incluido | `importe × tasa / (10000 + tasa)`, por unidad, sobre su parte neta de descuento. El IVA de la cuenta es la suma de sus unidades. |
| Descuento | Se calcula sobre el bruto de la cuenta, incluido el envío, y se reparte entre las unidades en proporción a su precio. |
| Reparto | Nunca se redondea pieza por pieza. Cada parte recibe su piso, y los centavos restantes van a los residuos mayores; en empate, a la primera parte. Las partes siempre suman el total. |
| División | Partes iguales del total, con el mismo reparto. IVA y descuento se reparten igual. |
| Propina | Va aparte: no entra en la venta ni en el IVA ni en el CFDI. Sí entra en la caja de su método de pago. |
| Pagos mixtos | Cada pago descuenta del saldo. El saldo nunca es negativo. |
| Recompensas | Se redondean hacia abajo. |
| CFDI | Un concepto por unidad, con la misma fórmula de IVA, para que el documento coincida con el ticket. |

## Devoluciones

Un pago no se edita. Una devolución es un registro inmutable que apunta a él, y sólo se aplica a cuentas cerradas.

| Pago | Qué pasa |
|---|---|
| Efectivo | Sale de la caja abierta como un egreso ligado a la devolución. Puede ser parcial. |
| Terminal o liga de pago en vivo | El cargo pasa a `refund-queued` y el proveedor lo devuelve completo. Si no tiene API, queda `refund-manual`. |
| Otros | Sólo se registra; una persona entrega el dinero. |

Las mercancías se devuelven aparte, con `stock.return`. `visit.trail` muestra una venta completa: unidades, pagos, cargos, devoluciones y CFDI.

## Pendiente de asesoría contable

- **Nota de crédito.** Devolver dinero de una venta con CFDI timbrado exige un CFDI de egreso relacionado. Hoy la devolución queda con `creditNote: pending` y `visit.trail` lo cuenta, pero **el sistema no lo emite**.
- **IVA del envío.** El concepto de envío sale con tasa 0. Hay que confirmar la tasa correcta, porque un restaurante con reparto propio normalmente lo grava.
- **Corte con devoluciones de días anteriores.** El corte las muestra en el día de la devolución, no en el de la venta.
