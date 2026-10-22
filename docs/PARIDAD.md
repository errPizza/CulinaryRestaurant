# Matriz de paridad funcional

La auditoría se basa en los casos de `backend/domain.ts`, opciones compartidas y vistas del prototipo. Los nombres de archivos remiten a `src/` y `tests/MesaNexo.Tests/`. «Probado» significa ejecución local Windows con los límites descritos en ESTADO.md; no certificación en todos los sistemas.

| Función del prototipo | Implementación nativa | Evidencia / diferencia |
|---|---|---|
| Mesas, apertura, personas, limpieza | Core/Restaurant; Client/MainView | API y negocio: apertura concurrente, ocupación y cierre; UI en tres anchos |
| Borrador y envío de comandas | NativeSession; Forms; Restaurant | Persistencia previa, UUID conservado, respuesta perdida tras commit y reconciliación |
| Extras, notas, precio histórico, cocción | Restaurant; Forms | Extras y cambios probados; cocción Medio/Tres cuartos/Bien cocido, compatibilidad explícita del producto legado |
| Cocina por unidad y estación, retención por tiempos | Restaurant; MainView | Transiciones, liberación retenida, no disponible, reinicio tras cambios |
| Avisos persistentes y entrega | Restaurant; MainView | Unidades listas/completas, notificaciones durables y destinatarios por rol |
| Cambio, cancelación, descuento y aprobaciones | Restaurant | Aprobación/rechazo, redondeo monetario, imposibilidad de saldo negativo |
| Transferencia de mesero, cambio de mesa | Restaurant | Preserva comandas, unidades y responsabilidades; permisos probados |
| Dividir y reunir cuentas | Restaurant | Conserva unidades; bloquea divisiones con pagos/descuentos incompatibles |
| Cobros, propinas y cierre | Restaurant; MainView | Enteros en centavos, sobrepago/negativos rechazados, duplicado y reintento durable |
| Caja y arqueo | Restaurant; Administration | Apertura, movimientos, esperado/contado/diferencia y propinas probados |
| Informes y exportación | Api/Reports; Administration | Histórico paginado, filtros, roles y CSV; no se midió volumen masivo |
| Catálogo, categorías, extras, disponibilidad | Restaurant; Administration | Precios históricos separados del catálogo, opciones y stock manual |
| Fotografías | Client/ProductPicture | Bitmap HTTPS/cache y data URL pequeña; migración rechaza registros >40 KiB. No hay equivalencia para blobs inline grandes sin externalizarlos |
| Promociones/combos y horarios | Restaurant; Administration | Ventana horaria America/Mexico_City; reglas locales, sin banco externo |
| Reservaciones | Restaurant; Administration | Bloque de dos horas, propuesta y aceptación probadas |
| Evaluaciones privadas | Restaurant; Administration | Registro interno único por visita; no es reseña pública |
| Usuarios, roles, permisos | BranchService; Restaurant; Administration | Argon2id, filtrado cocina/mesero/gerente, límites y revocación |
| Sesiones/dispositivos | BranchService; NativeSession | Alta de un uso, secreto protegido, expiración y revocación probadas |
| Configuración | Restaurant; Administration | Tablas, capacidad y módulos; gerente no administra el SO |
| Solicitudes/seguimiento del cliente | Restaurant; Forms | Simulación interna y clave de seguimiento; sin endpoint público |
| Impresión | Reports; Forms | Vista previa nativa; envío a impresora física pendiente |
| Panel proveedor multiempresa | Central/ProviderService; Client/Provider | Cartera, sucursales, contratos, módulos y cuotas; no abre operación de sucursal |
| Servicios comerciales | ProviderService | Permanencia/renta/fuente y módulos; no provisiona alojamiento ni factura fiscal real |
| Soporte y permiso temporal | Restaurant; BranchService; Provider | Consentimiento y vencimiento local, consulta de solo lectura, revocación inmediata; no control remoto del SO |
| Conversación de soporte | CentralStore; Entitlements | Respuesta firmada pendiente hasta commit y eco local; integración probada |
| Paquetes y publicación selectiva | ProviderService; Updates | Carga real, firma RSA-PSS, hash y destinatario; validación con fixture. Instalación real de actualización pendiente |
| Respaldos/restauración | Runtime CulinaryOS; CentralStore; herramientas | Snapshots y verificación probados localmente. Restauración administrativa en mantenimiento, no mismo botón de restauración en vivo del prototipo |
| Diseño grafito/ámbar | PrototypeTheme.axaml y Prototype*.cs | Revisión 0.1.1 con colores, trazos, composiciones y marca Culinary Restaurant; capturas de todas las vistas, cuenta y formulario. Ver MARCA-Y-DISENO.md para fuentes y límites de validación |
| Operación LAN sin central | Api + runtime local | Flujos locales no consultan central; WAN física/varias terminales en VM pendiente |
| Reintentos tras reinicio | Runtime extensión + recibos; NativeSession | Persistencia y deduplicación tras reinicio de procesos; apagado eléctrico real pendiente |

Los pagos con tarjeta/transferencia son registros manuales, igual que el origen. Las integraciones simuladas se identifican como tales. No se declara paridad total: quedan los límites anteriores, verificación real por plataforma y certificación de distribución.

## Ampliación DEVMap — 0.2.0

La referencia visual sigue siendo Grafito y Ámbar con los dos PNG originales; no se vuelve a la paleta verde. Se conservan las páginas y flujos previos y se añaden Inventario, Delivery y clientes, Arqueo ciego, Impresión, Fiscal, revisión de propuestas y analítica del dueño.

Las nuevas capacidades se trazan en [DEVMap-REQUISITOS.md](DEVMap-REQUISITOS.md). El servicio rápido conserva folio trazable sin mesa; las divisiones financieras no duplican unidades de cocina; los cobros manuales siguen etiquetados como tales. La UI adapta controles nativos: selección accesible equivale al arrastre manual funcional, sin prometer identidad de cada píxel entre sistemas.

Límites: impresión es trabajo durable pendiente, no papel confirmado; QR lleva al portal de solicitud, no al timbre; agregadores usan contrato propio, no integración certificada de terceros; online/push/AMI/mapas/PAC aislados. La PWA es un canal público separado y no sustituye los clientes nativos. iOS no se compiló. Las diferencias y pendientes históricos anteriores siguen vigentes salvo ampliación explícita en la matriz.

## Ajuste visual posterior a 0.2.0

Corregidos fuente/plantillas de botón, cabecera compacta, iconos vectoriales y entradas animadas nativas con reducción de movimiento. Validación focalizada: 129 comprobaciones de interfaz aprobadas (evidence/ui-style-review.json). No se repitió la suite de dominio para un cambio de presentación. Detalles y límites: [DISENO-NATIVO-Y-CSS.md](DISENO-NATIVO-Y-CSS.md). Disponible al compilar Debug; paquetes 0.2.0 anteriores conservados.

## Botones actualizados

Bordes redondeados completos, sombra suave, hover y rebote de clic implementados con reducción de movimiento. Pasaron 143 comprobaciones de interfaz, incluidas 14 de botones y activación única con ratón/Enter/Espacio. Nueva publicación Windows en artifacts/desktop-actual; se conserva el instalador anterior. Detalles: [DISENO-NATIVO-Y-CSS.md](DISENO-NATIVO-Y-CSS.md).
