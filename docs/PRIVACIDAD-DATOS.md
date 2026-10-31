# Privacidad y datos personales

Inventario técnico para acompañar el aviso de privacidad. Los plazos son una propuesta: necesitan revisión de un abogado mexicano junto con el borrador `CR-PRIV-0.1`, que sigue sin estar listo para publicarse.

## Inventario

| Dato | Registro | Finalidad | Dónde vive | Plazo propuesto |
|---|---|---|---|---|
| Nombre, teléfono y notas del cliente | `customer` | Pedidos, entrega, lealtad | Sucursal | Hasta pedir su eliminación |
| Dirección | `address`, `delivery.addressSnapshot` | Entrega | Sucursal; Google Maps si se activa | Hasta pedir su eliminación |
| Monedero | `walletmove` | Lealtad | Sucursal | Lo exija la contabilidad; sin datos de identidad tras eliminar |
| Reservación: nombre y contacto | `reservation` | Reservar mesa | Sucursal | 90 días tras la fecha |
| Llamadas y mensajes | `call`, `message` | Atención y avisos | Sucursal; WhatsApp o correo si se activan | 90 días |
| Pedido de plataforma | `aggregatororder` | Preparar el pedido | Sucursal; la plataforma es responsable de lo suyo | 90 días |
| RFC, razón social, régimen y correo | `fiscal.receiver` | CFDI | Sucursal; PAC | 5 años como mínimo, por obligación fiscal |
| Personal: nombre, usuario, PIN con hash | `user` | Acceso y responsabilidad | Sucursal | Duración de la relación y lo que exija la ley laboral |
| Bitácora | `audit` | Evidencia | Sucursal; sólo su hash sale a R2 | 5 años como mínimo |
| Proveedores | `supplier` | Compras | Sucursal | Lo exija la contabilidad |

No se guardan números de tarjeta, CVV ni NIP: la API rechaza esos campos. Las notas son texto libre, y el personal no debe escribir ahí datos de salud ni bancarios.

## Derechos del titular

- **Acceso.** En Clientes y monedero, el botón de exportar descarga todo lo que la sucursal tiene ligado a esa persona (`customer.export`).
- **Eliminación.** `customer.erase`, con motivo, deja los datos anónimos en su lugar. Las ventas, la caja y la bitácora conservan sus cifras. Requiere que no tenga cuentas abiertas ni saldo en el monedero. Queda el registro `erasure`, sin datos personales.
- **Rectificación.** Editando el cliente.

## Lo que la eliminación no alcanza

Esto debe decirse con precisión en el aviso:

1. **CFDI emitidos.** Se conservan por obligación fiscal.
2. **Historial sellado.** Los eventos ya confirmados (`events`, `events_archive`) conservan la versión anterior, cifrada bajo el llavero de la sucursal. Una eliminación real exigiría rotar llaves y re-sellar.
3. **Copias.** Las copias externas caducan a los 7 días; las de la central y los kits siguen su propio plazo.
4. **Central.** La eliminación viaja como un cambio más. Lo que la central ya recibió se reemplaza, no se borra de su historial.
5. **Bitácora.** Es inmutable. No guarda clientes ni direcciones, pero sí puede conservar el nombre de un registro de llamada.
6. **Terceros.** Plataformas, PAC, WhatsApp y Google tienen sus propias copias.
7. **Reservaciones y pedidos de plataforma.** No están ligados al cliente, así que `customer.erase` no los toca. Tampoco existe todavía la purga automática a 90 días de la tabla de arriba.

## Respuesta a incidentes

1. **Contener.** Revocar sesiones y credenciales afectadas. Si es la bitácora, no apagar ni restaurar el servidor.
2. **Preservar.** Conservar journal, `alerts/` del bucket y una copia del estado antes de tocar nada.
3. **Evaluar.** Qué datos, de quién y desde cuándo. `--verify-checkpoints` indica si se alteró la bitácora.
4. **Avisar.** Informar a los titulares si el incidente afecta de forma significativa sus derechos, con la naturaleza del incidente, los datos, las recomendaciones y las medidas tomadas. El plazo y la redacción los fija el abogado.
5. **Corregir.** Rotar llaves, documentar la causa y registrar el incidente.

El responsable y el contacto de privacidad siguen como `[POR DEFINIR]` en el borrador.
