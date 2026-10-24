# DEVMap — matriz de implementación 0.2.0

Referencia: `Ecosistema_Completo_SoftRestaurant_DevMap (1).pdf`, cinco páginas, y petición de ampliación del usuario. Revisión: 15 de octubre de 2026. La línea base pasó 190 comprobaciones de integración/interfaz y 17 de runtime; sus evidencias se conservaron en `.lab/devmap-baseline`. Los resultados actuales reproducibles están en `evidence/integration.json` y `evidence/runtime.json`.

«Implementado» describe código integrado y comprobado localmente. No implica despliegue productivo, ejecución en todos los sistemas ni pruebas de hardware. «Parcial» identifica funcionalidad utilizable con límites explícitos; los adaptadores aislados no se presentan como conexiones terminadas.

| ID / página | Requisito y estado real | Archivos principales | Pruebas y dependencia pendiente |
|---|---|---|---|
| 1.1-A / 1 | Implementado: zonas, libre/ocupada/impresa/sucia; bloqueo de cuenta emitida y reapertura autorizada con motivo | Core/Restaurant.cs, PosExtensions.cs; Client/PrototypeService.cs | BusinessTests, DevMapTests; interfaz a 360/834/1440 |
| 1.1-B / 1 | Implementado con límites: selección manual accesible, por persona y N subfolios; impuestos/importes enteros y centavos residuales | Core/PosExtensions.cs, Restaurant.cs; Client/EcosystemPages.cs | Split 303→152+151, conservación de unidades y pagos. Mover/unir exige resolver pagos/descuentos y retirar subfolios; no redistribuye silenciosamente cobros existentes |
| 1.1-C / 1 | Parcial: venta rápida sin mesa, cobro trazable, denominaciones/cambio y trabajos persistentes. Driver CUPS/cajonera aislado | Core/PosExtensions.cs; Api/PrintDriver.cs; Client/PrototypeService.cs | DevMapTests con driver sintético. Falta impresora/cajonera y autorización futura de conexión; no hay pulso físico automático |
| 1.1-D / 1 | Parcial: CRM, historial, varias direcciones manuales, delivery/pickup, polígonos/tarifas locales y responsabilidad del repartidor | Core/Delivery.cs; Client/EcosystemPages.cs; Api/Telephony.cs | DevMapTests: asignación no cobra, conciliación autorizada. AMI TLS y Google Geocoding aislados; sin llamadas ni consultas reales |
| 1.2-A / 2 | Implementado: recetas/subrecetas versionadas, ciclos, rendimiento y conversiones exactas; versión congelada por unidad | Core/Inventory.cs; Client/EcosystemPages.cs | DevMapTests: ciclos, dimensiones incompatibles, stock insuficiente e histórico |
| 1.2-B / 2 | Implementado: reserva al enviar a cocina; cierre crea trabajo durable en el mismo commit; consumo recuperable e idempotente por ingrediente | Core/Inventory.cs; Api/InventoryWorker.cs | DevMapTests: reinicio, dos workers, consumo único, cancelación preparada/no preparada; runtime transaction_v1 |
| 1.2-C / 2 | Implementado: múltiples almacenes, recepción por otra persona identificada, promedio ponderado/PEPS, capas e históricos | Core/Inventory.cs | DevMapTests: doble identidad, cancelación, promedio frente a PEPS. La aprobación es autenticación, no firma criptográfica personal |
| 2.1 / 3 | Parcial: arqueo ciego en UI/API/exportación, cierre transaccional inalterable, diferencias restringidas y aviso durable al dueño | Core/BlindCash.cs, Reports.cs; Client/EcosystemPages.cs | DevMapTests: filtrado, cierres concurrentes, ajuste separado. No se envía correo/webhook al dueño; push externo aislado |
| 2.2 / 3 | Implementado a nivel aplicación: autorizaciones reales, motivo, antes/después, cadena SHA-256 y verificación de alteración | Api/AuditChain.cs, BranchService.cs; Core/Restaurant.cs; Client/Administration.cs | DevMapTests altera registro cifrado y detecta cadena inválida. Anclas/respaldos independientes necesarios contra administrador con todas las claves |
| 2.3 / 3 | Parcial: solicitud fiscal pendiente, validación básica, QR/token opaco con vigencia, portal separado, estados y solicitud de cancelación | Core/Fiscal.cs; Api/FiscalProcessing.cs; Central/PublicInvoice.cs; Integrations/ExternalAdapters.cs | DevMapTests/TenantTests/AdapterTests. PAC Facturama con transporte sintético; sin activación, CSD, timbre real ni certificación de catálogos SAT |
| 3.A / 4 | Parcial por plataforma: cliente nativo, catálogo protegido, SignalR TLS por sitio autorizado, reconexión a snapshot durable | Client/Connection.cs, LiveConnection.cs; Api/LiveHub.cs | Program/UiTests y TLS real en loopback; Android compilado, ejecución en dispositivo pendiente; iOS fuente sin compilar |
| 3.B / 4 | Parcial: contrato propio Culinary Hub v1 autenticado HMAC, deduplicación y traducción al dominio | Central/DeliveryHub.cs; Integrations/PaymentAdapters.cs | TenantTests/AdapterTests con datos sintéticos. Contratos Uber/Rappi/DiDi/Deliverect y activación no disponibles; no se afirma compatibilidad |
| 3.C / 4 | Parcial: PWA por ruta/tenant, catálogo/carrito/extras/cocción/cupón, entrega/pickup, estado pendiente y revisión local | Public/wwwroot; Central/CommerceService.cs, TenantStore.cs; Api/CloudInbox.cs, CloudPull.cs; Core/CloudProposals.cs | CloudScenario: HTTPS+mTLS reales locales, aceptación de gerente y ACK tras commit. Pago online no habilitado: adaptador Stripe test aislado; banco real pendiente |
| 3.D / 4 | Implementado localmente: estaciones, unidad, semáforos configurables y reloj; pendientes recuperados desde Store | Core/Delivery.cs, Restaurant.cs; Client/PrototypeService.cs; Api/LiveHub.cs | Program/DevMapTests/UiTests; prueba de pantalla industrial, suspensión y carga real pendiente |
| 3.E / 4 | Parcial: vista nativa del dueño, ventas/mesas/incidencias/cortes, fuente/hora/retraso | Central/CommerceService.cs, TenantEndpoints.cs; Client/EcosystemPages.cs | TenantTests/UiTests; FCM/APNs contratos aislados y dobles HTTP, sin registro/envío a dispositivos reales ni promesa de worker móvil continuo |
| 4.1 / 5 | Implementado localmente: autoridad LAN cifrada, outbox/ACK existente, WAN no bloquea commit local | runtime/mesanexo_ipc.py; Api/BranchService.cs; Central/CentralStore.cs | Program/test_runtime_extension.py: central caída, respuesta perdida, reinicio, disco/permisos. Sin LAN/servidor se conserva pendiente, no éxito ficticio |
| 4.2 / 5 | Implementado localmente: central compartida tenant/branch, FK/índices, ciphertext contextual, membresía/dispositivo y mTLS; portal separado | Central/TenantStore.cs, TenantEndpoints.cs, CentralStore.cs; Public/Program.cs | TenantTests/CloudScenario: cruce de empresas, webhook, contexto cifrado, API privada ausente del portal. Hosting/proxy productivo pendiente |
| 4.3 / 5 | Implementado localmente: CAS de revisión, recibos/outbox atómicos y deduplicación, jobs acotados | Api/BranchService.cs, InventoryWorker.cs; runtime/mesanexo_ipc.py | Program/DevMapTests/runtime: concurrencia, rollback, doble procesamiento. No es una medición de capacidad productiva ni corte eléctrico físico |

## Decisiones verificadas

Se conservan .NET 10 LTS, Avalonia 12.1.4, ASP.NET Core y CIFRADO-V1. Referencias: [política .NET](https://dotnet.microsoft.com/en-us/platform/support/policy), [Avalonia 12](https://docs.avaloniaui.net/docs/avalonia12-breaking-changes). SignalR transporta avisos de revisión; no sustituye Store, relectura consistente ni autorización. No se añade Redis/MQTT sin necesidad medida.

No se adopta la inyección SQL del PDF. Toda mutación local pasa por dominio autorizado y la extensión original: máximo 256 cambios y evento de 56 KiB. Los trabajos de inventario avanzan por ingrediente/capas acotadas. No se duplica el sincronizador de outbox; CloudPull sólo recibe propuestas firmadas y devuelve acuses de decisiones ya comprometidas.

Política elegida por el usuario: **reservar al enviar y consumir al liquidar; cancelar libera sólo lo no preparado**. Si ya se preparó, la cancelación genera consumo como merma. Reabrir no devuelve stock ni borra pagos; la devolución física requiere operación explícita con motivo y movimiento trazable (consumo completo, no devolución parcial automática).

Dinero en centavos enteros; impuestos incluidos configurables por unidad y tasa histórica. Registros históricos sin tasa mantienen cero; no se les asigna IVA retrospectivamente. División por mayor residuo y desempate estable. Cupones explícitos no se aplican automáticamente a ventas ordinarias; se revalidan localmente antes de aceptar un pedido público.

El pedido público es una **propuesta pendiente**, no una venta aceptada. El gerente local revisa; precios/tarifas/cupón/stock se validan en la misma transacción de aceptación. Pedidos online no se convierten en pagos locales; no existe activación de cobro real. Un acuse perdido se recupera por identidad/digest tras reinicio.

La auditoría es append-only para la aplicación, con hash de detalle y ancla en meta; el legado se conserva y se cuenta por separado. No resiste por sí sola a quien reescriba registros, claves y todas las anclas. No registra PIN ni contraseña reversible.

El usuario decidió **mantener los adaptadores externos aislados, sin rutas ni comandos de activación**. CUPS, AMI, geocodificación, PAC, pagos, correo y push no se ejecutaron. Véase [decisión y revisión](INTEGRACIONES-REVISION.md). Los contratos/dobles prueban formato y reglas, no disponibilidad del proveedor.

## Verificación y pendientes de entrega

Suite reproducible: `tools/Test.ps1`; resultados y capturas en `evidence`. `SchemaTests` verifica migración explícita con snapshot cifrado y restauración sintética; no se migraron bases reales. Paquetes y hashes en `artifacts`, límites de plataforma en `MOVILES.md` y `ESTADO.md`. No se instaló sobre CulinaryOS ni se modificaron los datos originales del prototipo. No hay declaración de producción lista.

Resultado final 0.2.0: **303 comprobaciones de integración/interfaz y 19 pruebas del runtime aprobadas**; 120 comprobaciones de interfaz incluidas en esa suite. El editor conserva la tasa de impuesto del producto; la auditoría identifica producto/costos/referencias antes y después. Evidencia: `evidence/integration.json`, `runtime.json` y `ui-final.json`.

## Hoja «Diagrama maestro del ecosistema total» — etapa 1: punto de venta

Revisión: 21 de octubre de 2026. El usuario pidió implementar lo que faltara de la hoja por etapas, con prueba en cada una; integraciones externas simuladas y listas para activar; hardware con controladores simulados. Esta etapa cubre el punto de venta del comedor, mostrador, para llevar y modificadores.

| Punto de la hoja | Estado real | Archivos principales | Pruebas |
|---|---|---|---|
| Comedor: fusión de mesas | Implementado: `visit.mergeMany` une varias cuentas en una maestra; las mesas unidas quedan ocupadas (`linkedTableIds`) y se liberan al cerrar la maestra. Solo cuentas sin pagos ni descuentos | Core/Frontoffice.cs; Web/service-board.tsx, main.tsx | FrontofficeTests: unión, suma de comensales, liberación al cierre |
| Comedor: transferencia por zona | Implementado: `visit.reassignZone` (gerente) pasa las cuentas abiertas de una zona a otro mesero; conserva las impresas o divididas y devuelve movidas/omitidas | Core/Frontoffice.cs; Web/main.tsx | FrontofficeTests: zona inexistente, cuentas omitidas |
| Comedor: división visual | Implementado: tablero de arrastrar y soltar (o tocar y «Mover aquí»), hasta 6 cuentas y «Separar por persona»; usa `visit.split` existente | Web/split-dialog.tsx | Captura y prueba visual de etapa |
| Servicio rápido | Implementado: página Mostrador con catálogo primero, búsqueda, carrito, comer aquí/para llevar, nombre del cliente y `sale.quick` (cuenta + comanda + pago en un solo commit). Atajos $50/$100/$200/$500/Exacto, otro importe y cambio | Core/Frontoffice.cs; Web/counter-page.tsx | FrontofficeTests; Playwright híbrido contra backend real (`counter sells takeout…`) |
| Pulso de cajón solo en efectivo | Implementado con controlador simulado: si `drawerOnCash` está activo, cada pago en efectivo crea un trabajo de impresión tipo `drawer`; tarjeta y transferencia no. Sin pulso físico | Core/Frontoffice.cs, Restaurant.cs | FrontofficeTests: efectivo sí, tarjeta no |
| Pantalla cara al cliente | Implementado: segunda ventana (`cliente.html`), en Tauri se abre en el monitor secundario a pantalla completa; muestra renglones, total, cambio, agradecimiento y mensajes configurables. Canal local `BroadcastChannel`, sin red ni almacenamiento | Web/customer-display.ts, customer-main.tsx; src-tauri/main.rs | Playwright: la pantalla refleja orden, total y pago |
| Para llevar con hora programada | Implementado: `pickupAt` (entre ahora y 7 días) y nombre de entrega; tarjeta en amarillo al acercarse y en rojo con aviso al pasar la hora sin recoger | Core/Delivery.cs; Web/ecosystem.tsx | FrontofficeTests: hora fuera de rango, etiqueta |
| Etiqueta PARA LLEVAR / A DOMICILIO | Implementado: la comanda guarda la etiqueta y se imprime y muestra en cocina | Core/Restaurant.cs, Frontoffice.cs | FrontofficeTests, captura de cocina |
| Modificadores obligatorios/opcionales con costo | Implementado: grupos por producto (máx. 10, 1–20 opciones, «elegir hasta N», precio por opción); los obligatorios bloquean la comanda en interfaz y en dominio; punto de cocción puede ser obligatorio. Cocina no ve precios | Core/Frontoffice.cs, Restaurant.cs; Web/order-product-dialog.tsx, form-dialog.tsx | FrontofficeTests: faltante, opción desconocida, exceso, precio |
| Comentarios sanitizados | Implementado: notas de cocina, nombres y mensajes sin caracteres de control y con longitud máxima (500/80/140) | Core/Frontoffice.cs | FrontofficeTests |
| Cuenta prepagada | Implementado: una venta de mostrador cobrada se cierra sola al entregar todo | Core/Frontoffice.cs | FrontofficeTests |

Corrección incluida: crear una mesa fallaba si existía cualquier cuenta abierta sin mesa (ahora la validación solo aplica al editar una mesa existente).

Verificación de la etapa: 27/27 FrontofficeTests, 40/40 DevMapTests sin regresión, 16/16 pruebas web (Playwright con Edge) y capturas en claro y oscuro. Siguientes etapas acordadas: 2) compras, proveedores, cuentas por pagar, sugerencia mínimo/máximo, costo teórico frente a real, caja chica, límite de efectivo y PIN por usuario; 3) consumo de empleados, lealtad y monedero, listas de precios, QR en mesa, cortes X/Z y ABC; 4) integraciones simuladas listas para activar.

## Hoja «Diagrama maestro del ecosistema total» — etapa 2: administración

Revisión: 21 de octubre de 2026. Dominio en `Core/Backoffice.cs`; interfaz en `Web/src/backoffice.tsx` (páginas Compras y proveedores, Gastos y caja chica, diálogos de compra, conteo y PIN) más cambios en Inventario, Caja, Equipo y el menú de perfil.

| Punto de la hoja | Estado real | Pruebas |
|---|---|---|
| Proveedores | Implementado: `supplier.save` con RFC validado y normalizado, contacto, correo, días de crédito y baja lógica; nombres únicos | BackofficeTests |
| Órdenes de compra y recepción | Implementado: `purchase.order` (folio OC-0001, hasta 30 insumos) no toca inventario; `purchase.receive` registra lo realmente recibido, cuenta diferencias contra la orden, crea capas de costo (PEPS/promedio existentes) y guarda el último costo; compra directa sin orden; `purchase.cancel` con motivo | BackofficeTests; Playwright híbrido `back office buys…` |
| Cuentas por pagar | Implementado: cada recepción con importe crea `payable` con vencimiento por días de crédito; `payable.pay` parcial o total desde banco, efectivo de la caja (movimiento de gasto) o caja chica; antigüedad de saldos y facturas vencidas en la interfaz | BackofficeTests; Playwright |
| Sugerencia de compra mínimo/máximo | Implementado: `inventory.levels` (mínimo, máximo y proveedor preferido por insumo); `purchase.suggest` pide hasta el máximo descontando lo reservado y lo que ya viene en órdenes abiertas, y puede crear una orden por proveedor | BackofficeTests |
| Costo teórico vs real | Implementado: `stock.count` (conteo físico de hasta 25 insumos) ajusta faltantes y sobrantes a costo promedio y guarda el historial; `inventory.variance` separa costo teórico (recetas vendidas), merma, faltantes y sobrantes por insumo y como porcentaje de ventas | BackofficeTests |
| Egresos y caja chica por cuentas de gasto | Implementado: `expense.account` con presupuesto mensual; `expense.record` desde caja chica, caja o banco; fondo fijo (`petty.configure`) y reposición (`petty.fund`), opcionalmente tomada de la caja; la caja chica no puede quedar negativa | BackofficeTests; Playwright |
| Blindaje por límite de efectivo | Implementado: `cash.limit` (límite y bloqueo opcional). Al superarlo se encola aviso al dueño; con bloqueo, los cobros en efectivo se rechazan (tarjeta y transferencia siguen) hasta un `cash.withdraw` (retiro parcial con sobre). Medidor y aviso en Caja | BackofficeTests |
| Roles con PIN | Implementado con PIN; huella y lectores siguen simulados por decisión del usuario. `user.pin` (4 a 6 dígitos, sin secuencias ni dígitos repetidos) guardado con Argon2id ligado a sitio y usuario; el hash nunca sale del servidor ni entra en la huella de idempotencia. `authorize.pin` permite que un gerente autorice en el dispositivo del mesero una lista cerrada de operaciones (decidir solicitudes, reabrir, desbloquear, cajón, retiro). Cinco fallos bloquean el PIN cinco minutos y cada intento queda en `pinauth` con quién pidió y quién autorizó | BackofficeTests; Playwright |
| Permisos finos | Implementado: además de cobrar y ver resultados, por mesero: vender en Mostrador, unir mesas y transferir/cambiar de mesa. El servidor los aplica; la interfaz oculta lo no permitido | BackofficeTests |

Visibilidad: proveedores, compras, cuentas por pagar, conteos, gastos, caja chica y bitácora de PIN sólo llegan a gerencia.

Verificación de la etapa: 48/48 BackofficeTests, suite de integración completa 387/387 (sin regresiones), 17 pruebas web con Edge contra el backend real y capturas en claro y oscuro.

## Hoja «Diagrama maestro del ecosistema total» — etapa 3: clientes, empleados, cortes y QR

Revisión: 21 de octubre de 2026. Dominio en `Core/Loyalty.cs`, `Core/Closings.cs` y `Core/TableQr.cs`; nube en `Central/CommerceService.cs`, `Central/TenantStore.cs`, `Public/Program.cs` y `Public/wwwroot/app.js`; interfaz en `Web/src/loyalty.tsx` y Mostrador.

| Punto de la hoja | Estado real | Pruebas |
|---|---|---|
| Listas de precios | Implementado: `pricelist.save` con ajuste general (−90 % a +100 %), hasta 200 precios fijos, canales (mesas, mostrador, para llevar, domicilio), horario y prioridad; automáticas o manuales (`visit.pricelist`, también «precio del catálogo»). El precio se fija al enviar la comanda y la unidad guarda la lista usada. Los pedidos web conservan el precio publicado | LoyaltyTests |
| Lealtad y monedero electrónico | Implementado: cliente en la cuenta (`visit.customer`) o en Mostrador; cashback configurable al cerrar sobre lo pagado (no sobre monedero ni nómina); recargas en efectivo (entra a caja), tarjeta o transferencia; pago con monedero (`wallet.pay`, también en `sale.quick`); ajustes con motivo; nunca saldo negativo | LoyaltyTests; Playwright `loyalty wallet…` |
| Consumo de empleados | Implementado: descuento de empleado y tope mensual por nómina (`staff.configure`); `staff.meal` crea la cuenta con etiqueta EMPLEADO, aplica el descuento y liquida por nómina, efectivo o tarjeta; un mesero sólo registra su propio consumo; `staff.settle` marca lo descontado por mes | LoyaltyTests; Playwright |
| Cortes X y Z | Implementado: `report.x` lee desde el último Z sin modificar nada; `report.z` exige caja y cuentas cerradas, guarda folio consecutivo, periodo, totales por forma de pago, categoría y mesero, cancelaciones, descuentos, caja e impuesto, con digest, y encola su impresión | LoyaltyTests |
| Análisis ABC | Implementado: `report.abc` clasifica por venta (80/15/5) y por cantidad, con costo unitario y margen cuando el producto tiene receta y costo de inventario | LoyaltyTests |
| QR en mesa | Implementado sobre el canal de nube existente: `table.qr` genera un código aleatorio por mesa (regenerable; el anterior deja de funcionar) y el enlace va en el fragmento de la URL. La central sólo recibe el hash. La página pública muestra la mesa y el menú; «Llamar al mesero» y «Pedir la cuenta» llegan como aviso al mesero de esa mesa; un pedido queda pendiente hasta que el mesero lo confirma (`qr.decide`) y se revalida el precio. Productos con opciones obligatorias no se ofrecen por QR | LoyaltyTests; CloudScenario por HTTPS y entrega firmada (9 comprobaciones) |

Verificación de la etapa: 33/33 LoyaltyTests, suite de integración completa 429/429 (incluye el QR de punta a punta), 18 pruebas web con Edge contra el backend real y capturas en claro y oscuro (incluido el sitio público en teléfono).

## Hoja «Diagrama maestro del ecosistema total» — etapa 4: integraciones simuladas

Revisión: 22 de octubre de 2026. Dominio en `Core/Integrations.cs` (más ajustes en `Fiscal.cs`, `Delivery.cs`, `Frontoffice.cs` y `PosExtensions.cs`); interfaz en `Web/src/integrations.tsx` (página Integraciones, terminal, cobros de la cuenta, pedidos de plataformas y llamada entrante) y cambios en Delivery, Fiscal, Impresión, Mostrador y la cuenta.

Cada simulador recorre los mismos registros y estados que usaría un adaptador real, pero **nada sale de la sucursal**: no hay red, no se mueve dinero y los timbres no tienen validez fiscal. `integration.configure` sólo admite `simulated` u `off`; el modo `live` se rechaza (403) hasta que haya contrato, credenciales en el servidor y autorización expresa. Se mantiene la decisión de [no activar adaptadores reales](INTEGRACIONES-REVISION.md). Toda operación simulada queda en `integrationlog` con su referencia.

| Punto de la hoja | Estado real | Pruebas |
|---|---|---|
| Facturación (PAC) | Simulado: `pac.stamp` timbra una solicitud pendiente con UUID, serie/folio consecutivo y sello de prueba, marcada «SIMULADO · sin validez fiscal»; aplica la regla SAT del RFC genérico (XAXX010101000 ⇔ régimen 616 y uso S01) y rechaza con mensaje CFDI40144; timbrado automático opcional; cancelación inmediata con motivo SAT. Correo opcional al receptor | IntegrationTests |
| Terminal bancaria (PIN pad) | Simulada con tarjetas de prueba: aprobada (Visa 4242, código de autorización), rechazada (sin pago) o sin respuesta (queda incierta y bloquea otro cobro hasta `terminal.reconcile`, que aplica el cargo una sola vez o lo revierte si la cuenta ya se cubrió). En Mostrador la venta con tarjeta pasa por la terminal y es todo o nada. Nunca se reciben datos de tarjeta | IntegrationTests; Playwright `integrations run in simulation…` |
| Pagos en línea (Stripe / Mercado Pago) | Simulados: `paylink.create` genera una liga en el dominio reservado `.invalid` con vigencia; `paylink.simulate` emula el webhook (pagado → pago `online`; pagado sobre cuenta cubierta → reembolso; rechazado; vencido); una liga vigente por cuenta | IntegrationTests |
| WhatsApp y correo | Simulados: bandeja de salida (`message`) con un mensaje por evento: pedido en camino (con repartidor y tiempo estimado), pedido para llevar listo (sólo cuando todo está listo), liga de pago, factura timbrada, avisos al dueño (límite de efectivo, arqueo ciego, conciliación) y mensaje de prueba | IntegrationTests; Playwright |
| Uber Eats / Rappi / DiDi Food | Simulados: pedido entrante pendiente con folio de la plataforma y tiempo para aceptar; al aceptar se crea la cuenta, la comanda sale etiquetada (UBER EATS, RAPPI, DIDI FOOD), la plataforma paga el total (pago `platform`) y se registra comisión y neto; entrega al repartidor sólo con cocina terminada, cierra la cuenta; rechazo con motivo; aceptación automática y plataformas configurables; publicación de menú (excluye productos con opciones obligatorias) | IntegrationTests; Playwright |
| Hotel (Opera / Cloudbeds) | Simulado: `pms.charge` valida habitación, apellido del huésped y crédito autorizado; crea pago `room` y el cargo `pmspost` con referencia del PMS | IntegrationTests |
| Mapas (Google Maps) | Simulado: geocodificador determinista dentro del radio de reparto (`geo.lookup`, `address.save` con `geocode`); el pedido a domicilio guarda distancia y tiempo estimado. La captura manual sigue disponible | IntegrationTests |
| Caller ID | Simulado: `callerid.simulate` crea la llamada (mismo registro `call` que el adaptador Asterisk) y la relaciona con el cliente por los últimos 10 dígitos; aviso de llamada entrante en Delivery con «Tomar pedido» o «Registrar cliente» | IntegrationTests |
| Impresora y cajón RJ11 | Simulados: con el driver simulado activo, los trabajos pendientes (cuentas, cortes Z, cajón) se resuelven como «Simulado» con evidencia explícita; no sale papel ni se abre el cajón | IntegrationTests |
| Huella digital | Sin cambio: sigue cubierta por PIN por usuario (etapa 2), por decisión del usuario | — |

Visibilidad: los meseros sólo ven qué simuladores están activos, sus propios cobros de terminal y ligas, y los pedidos de plataformas; la bitácora, la bandeja de salida y los cargos de hotel sólo llegan a gerencia. El Corte X/Z separa los pagos en línea, a habitación y de plataformas.

Correcciones incluidas: el campo `key` lo elimina el puente web, por eso el parámetro se llama `provider`; los rechazos de negocio de las integraciones usan 400 (el cliente nativo trata los 409 como resultado incierto).

Verificación de la etapa: 61/61 IntegrationTests, suite de integración completa 490/490 sin regresiones, 19/19 pruebas del runtime y 19 pruebas web con Edge contra el backend real (incluida `integrations run in simulation…`), con capturas `evidence/etapa4-*.png` en claro, oscuro y teléfono.

## Integraciones preparadas para producción

Revisión: 23 de octubre de 2026. El usuario pidió que las integraciones simuladas quedaran listas para producción y dejar las pruebas reales para el final. Cada integración tiene tres modos (apagada, simulación, real); el modo real exige credenciales válidas en `/etc/culinaryos/integrations/<clave>.json` ([PROVEEDORES.md](PROVEEDORES.md), ejemplos en `deploy/integrations`).

| Integración | Estado | Archivos principales |
|---|---|---|
| Base común | Implementado: carga de credenciales tolerante a errores, `connection.integrations`, trabajo durable con reclamo, arrendamiento y consulta de respuestas perdidas (`SendNext`/`PollNext`), efectos de dinero en `Restaurant.IntegrationResult` | Api/LiveIntegrations.cs; Core/LiveIntegrations.cs |
| Facturama producción | Implementado: sandbox o producción, envío de PDF/XML al receptor, cancelación de CFDI reales | Api/FiscalService.cs, FiscalProcessing.cs |
| WhatsApp y correo | Implementado: plantillas de Meta por evento y SMTP con reintentos seguros | Api/LiveMessaging.cs; Integrations/MessagingAdapters.cs |
| Mapas y Caller ID | Implementado: Geocoding + Routes al guardar la dirección; Asterisk AMI con reconexión | Api/LiveMapsAndCalls.cs; Integrations/MapsAdapters.cs |
| Terminales | Implementado: Mercado Pago Point y Clip con seguimiento, cancelación y devolución; BBVA semi-integrado con voucher | Api/LiveTerminals.cs; Integrations/TerminalAdapters.cs |
| Ligas de pago | Implementado: Stripe Checkout con consulta periódica, reembolso y expiración | Api/LivePayments.cs; Integrations/PaymentAdapters.cs |
| Hotel | Implementado: Cloudbeds y OPERA (OHIP) con validación del huésped y crédito | Api/LivePms.cs; Integrations/PmsAdapters.cs |
| Deliverect | Implementado: webhook HMAC en el portal público, propuesta `aggregator` a la sucursal, estados y menú de vuelta | Public/Program.cs; Central/CommerceService.cs; Api/LiveAggregators.cs |

Verificación: 52 comprobaciones nuevas contra APIs de proveedor simuladas sin red (`LiveIntegrationTests`, más el webhook de Deliverect en `TenantTests`), suite de integración completa 598/598, runtime 27/27, instalador 44/44 y 20/20 pruebas web con Edge contra el backend real. No se probó con credenciales ni cuentas reales de ningún proveedor; esa prueba queda para el final con los archivos de `deploy/integrations`.
