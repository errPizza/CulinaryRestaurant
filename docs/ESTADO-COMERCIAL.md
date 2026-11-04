# Estado comercial por plataforma y función

Qué se puede prometer hoy. Fecha: 30 de octubre de 2026, versión 0.3.0.

Cada fila avanza por cuatro estados, y **sólo «Habilitado» se puede ofrecer a un cliente**:

- **Implementado:** el código existe.
- **Probado:** pasa pruebas automáticas, con simuladores cuando el servicio es externo.
- **Validado en real:** funcionó con el hardware o la cuenta real.
- **Habilitado:** se autorizó para vender.

Hoy nada está validado en real ni habilitado. La venta espera el piloto.

## Plataformas

| Plataforma | Implementado | Probado | Validado en real | Habilitado |
|---|---|---|---|---|
| Servidor CulinaryOS (Debian 13 amd64) | Sí | Parcial: instalador y unidades simulados | No: falta el simulacro en VM y en equipo físico | No |
| Cliente Windows (Tauri) | Sí | Sí | No | No |
| Cliente Linux en kiosco | Sí | Parcial | No | No |
| Android | Compila, APK con firma de depuración | No: sin correr en teléfono ni emulador | No | No |
| iOS | Sólo código | No: sin compilar | No | No |
| Central multiempresa | Sí | Sí, con mTLS real local | No | No |
| Portal de licencias (Worker y CulinaryAdmin) | Sí | Sí | No: la cadena de intermedias sin desplegar | No |

## Operación del restaurante

| Función | Implementado | Probado | Validado en real | Habilitado |
|---|---|---|---|---|
| Mesas, comandas y cocina | Sí | Sí | No | No |
| Caja, arqueo ciego y cortes | Sí | Sí, al centavo con cuentas aleatorias | No | No |
| División, descuentos y pagos mixtos | Sí | Sí | No | No |
| Devoluciones y doble aprobación | Sí | Sí | No | No |
| Inventario, recetas y compras | Sí | Sí | No | No |
| Reservaciones, lealtad y monedero | Sí | Sí | No | No |
| Bitácora y puntos de control | Sí | Sí, con bucket simulado | No: sin bucket de R2 | No |
| Copia externa y restauración | Sí | Sí, con `pg_dump` real y bucket simulado | No: RTO y RPO sin medir | No |

## Integraciones

Todas tienen adaptador real y simulador. Ninguna se ha probado contra el sandbox del proveedor.

| Integración | Implementado | Probado | Validado en real | Habilitado |
|---|---|---|---|---|
| Facturama, CFDI 4.0 | Sí, sin nota de crédito | Sí, con simulador HTTP | No | No |
| Mercado Pago Point | Sí | Sí, con simulador HTTP | No | No |
| Clip | Sí | Sí, con simulador HTTP | No | No |
| BBVA semi-integrado | Registro desde el voucher | Sí | No | No |
| Stripe Checkout | Sí | Sí, con simulador HTTP | No | No |
| WhatsApp y correo | Sí | Sí, con simulador | No | No |
| Deliverect | Sí | Sí, con simulador | No | No |
| Cloudbeds y Oracle OPERA | Sí | Sí, con simulador | No | No |
| Google Maps | Sí | Sí, con simulador | No | No |
| Asterisk | Sí | Sí, con simulador | No | No |
| Impresoras y cajón ESC/POS | Sí | Sí, con impresora simulada | No: sin impresora física | No |
| Notificaciones FCM y APNs | Biblioteca aislada, sin activar | No | No | No |

## Dispositivos (CR-015)

Una fila por modelo probado. Un dispositivo sólo se declara compatible con todas sus columnas llenas y el resultado «Funciona». Hoy no hay ninguno probado en físico: las filas son los modelos que el código ya contempla.

| Tipo | Modelo | Sistema y versión | Controlador o conexión | Funciones probadas | Errores encontrados | Resultado | Limitaciones |
|---|---|---|---|---|---|---|---|
| Impresora de tickets | ESC/POS 80 mm (genérica) | CulinaryOS / Windows | USB o red, ESC/POS | Sólo simulador | — | Sin probar | — |
| Cajón de dinero | Por puerto de la impresora | — | Pulso ESC/POS | Sólo simulador | — | Sin probar | — |
| Terminal de pago | Mercado Pago Point | — | API de órdenes | Sólo simulador HTTP | — | Sin probar | — |
| Terminal de pago | Clip PinPad | — | API de pinpad | Sólo simulador HTTP | — | Sin probar | Sin reverso por API |
| Terminal de pago | BBVA semiintegrada | — | Captura del voucher | Flujo manual | — | Sin probar | Sin consulta ni reverso |
| UPS | Compatible con NUT | CulinaryOS | USB, `ups.py` | Sólo simulado | — | Sin probar | — |
| Servidor | Equipo de sucursal | Debian 13 amd64 | — | Sólo VM | — | Sin probar | TPM, SMART y LUKS sin validar |

Para llenar una fila: fecha, versión de Culinary Restaurant, firmware del dispositivo y la evidencia (foto del ticket, registro del journal o captura) en `evidence/`.

## Antes de vender

1. Simulacro completo en VM y en un equipo real, midiendo RTO y RPO.
2. Sandbox de Facturama y de la terminal elegida.
3. Impresora y cajón físicos.
4. Desplegar el Worker con las intermedias y sacar la llave raíz de línea.
5. Revisión legal del aviso de privacidad y contable de `POLITICA-MONETARIA.md`.
6. Piloto controlado con dinero real.
