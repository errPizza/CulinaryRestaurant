# Diseño aprobado de Culinary Restaurant

El usuario aprobó la propuesta grafito y ámbar el 16 de octubre de 2026. La implementación usa datos y operaciones reales; la maqueta con datos de ejemplo queda como referencia en `evidence/design-review/propuesta.html`.

- Barra grafito con iconos por sección y selección en ámbar.
- Acciones de altura mínima de 42 px, espacios y bordes compartidos; filtros y accesos compactos conservan una escala propia.
- Tarjetas de métricas alineadas, iconos por función y acentos discretos: azul para servicio, verde para disponibilidad/confirmación, ámbar para pendientes, violeta para reserva/limpieza y rojo para incidencias.
- Resumen, Mesas y Soporte trasladados a componentes conectados al estado de la API. Las mesas se ordenan por nombre y tienen filtros y búsqueda; se conservan cuentas múltiples, menús de acciones, permisos y resumen de mesa seleccionada.
- Solicitudes de soporte con conversación desplegable, respuestas, cobertura real y permisos temporales revocables. El permiso sigue siendo una consulta simulada de sólo lectura.
- Caja, catálogo, KDS, inventario, delivery, fiscal, impresión, reportes, equipo y administración comparten controles, iconos, encabezados y estilos de registros.
- En el acceso se oculta `::-ms-reveal` de WebView2 únicamente en los campos con un botón propio: queda un solo ojo a la derecha para cada campo. Se conserva el valor al mostrar/ocultar y esos botones no envían el formulario.

Fuentes: `src/MesaNexo.Web/src/ui.tsx`, `approved.css`, `overview-page.tsx`, `support-page.tsx` y `service-board.tsx`. Se mantienen el movimiento reducido y las animaciones de hover/clic.

Validación: la suite web comprende nueve escenarios con HTTPS, almacén cifrado y bóveda nativa de un laboratorio aislado. Incluye navegación por las secciones del gerente, POS, caja, inventario, permisos, recuperación de una respuesta perdida, acceso, soporte y comparación de tamaños y desbordamiento a 1360/390 px. La comprobación del ejecutable Windows usa WebView2 real y escribe una captura del campo de contraseña mientras se teclea (`evidence/hybrid-native-secret.png`). Los resultados se registran en `evidence/hybrid-web-tests.json` y `evidence/hybrid-tauri-startup.json`.

Los adaptadores externos permanecen aislados. No se han migrado ni reiniciado los datos del restaurante.

Los diálogos respetan el campo que el usuario ya seleccionó: el enfoque inicial programado no vuelve al primer campo si el usuario empezó a escribir en otro.

## Acabado profesional (21 de octubre de 2026)

El usuario aprobó un acabado con más profundidad sobre el mismo diseño grafito y ámbar: se conservan colores, estructura y textos. La capa está en `src/MesaNexo.Web/src/profesional.css`, importada después de `approved.css`; para volver al aspecto anterior basta con quitar esa importación de `main.tsx`.

- Sombras suaves en capas en lugar del desplazamiento duro de 3 px; las tarjetas se elevan al pasar el cursor y la mesa seleccionada lleva un halo ámbar.
- Barra grafito con degradado ligero, navegación en una cápsula hundida y la sección activa en ámbar con relieve; perfil agrupado.
- Botón principal ámbar con volumen; secundarios con borde fino; tipografía Segoe UI con títulos y textos de 12,5–13 px.
- Mesas agrupadas por zona, puntos de comensales por lugar, saldo en un recuadro interior y franja superior del color de estado.
- Resumen de mesas con el total en mesas abiertas; panel de mesa seleccionada con fondo ámbar suave; avisos de atención compactos.

Validación: las 12 pruebas web (9 híbridas y 3 de kiosco) pasan antes y después del cambio. Copia del frontend anterior y sus capturas en `.lab/ui-backup-profesional-20261021-003526`.

## Rediseño completo, animación y modo oscuro (21 de octubre de 2026)

El usuario aprobó las referencias visuales de todas las pantallas y pidió aplicarlas tal cual. Se conserva el grafito y ámbar; cambian la estructura y la jerarquía para leer primero lo importante. Capturas de la app real con datos de ejemplo en `evidence/rediseno/` (40 a 1440 px y 3 a 390 px).

- **Una sola hoja de estilos:** `src/MesaNexo.Web/src/theme.css` (tokens claro/oscuro, componentes, animaciones y adaptación a tablet y móvil). Las hojas anteriores se retiraron; copia en `.lab/ui-backup-rediseno-20261021-130655`.
- **Fuente Inter** empaquetada (`@fontsource-variable/inter`), funciona sin conexión.
- **Barra superior:** navegación en cápsula; *Administración* agrupa sus 11 secciones en Operación del turno, Menú e insumos y Control y equipo, con contadores de pendientes. El perfil abre un menú con apariencia, reducir animaciones, versión, conexión y cerrar sesión.
- **Encabezado de página:** ruta, título y una frase con lo más importante calculada de los datos reales (por ejemplo, la comanda que superó el objetivo o el efectivo esperado). Las acciones de cada pantalla aparecen junto al título.
- **Pantallas reorganizadas:** resumen por urgencia con "Cocina ahora"; mesas por zona; cocina por urgencia con semáforo y parpadeo en retraso; historial con línea de tiempo por plato; caja con efectivo esperado y reparto por forma de pago; delivery como tablero por estado; catálogo con tarjetas y filtros; inventario con menú *Configurar*; promociones sobre una línea de 24 h; reservaciones como agenda; evaluaciones con promedios y por mesero; configuración en filas; arqueo ciego por denominación con total en vivo; analítica, reportes y proveedor con cifras y gráficas.
- **Modo oscuro:** sigue al sistema hasta que se elige en el menú de perfil; la preferencia se guarda en el almacén seguro (`theme.get`/`theme.set` en el puente).
- **Animación:** entrada escalonada de secciones, elevación de tarjetas, menús desplegables, cuenta lateral deslizante, diálogos, barras que crecen. Todo se desactiva con "Reducir animaciones" o la preferencia del sistema.

Validación: 24 comprobaciones del puente (3 nuevas del tema) y 15 pruebas web (9 híbridas, 3 de kiosco, 3 del asistente) aprobadas. Sin desbordamiento horizontal a 390 px en las 18 pantallas del gerente. Se ajustaron 3 pruebas porque los controles cambiaron de lugar: "Agregar productos" sin el "+", las acciones de configuración de inventario dentro del menú *Configurar* y la versión dentro del menú de perfil.

## Auditoría de menús y formularios más fáciles (21 de octubre de 2026)

Se auditó cada menú, desplegable y diálogo de todas las pantallas en claro y oscuro a 1440, 1340, 1100 y 390 px, midiendo recortes, salidas de pantalla y campos sin contorno.

- **Listas desplegables** con el diseño de la app (`appearance: base-select` de WebView2): panel con sombra, opción elegida marcada en ámbar, animación y modo oscuro. Siguen siendo `<select>` nativos, con teclado y accesibilidad intactos.
- **Menús contextuales** que se abren hacia arriba cuando no caben abajo; el selector de color recupera su contorno; en móvil las cifras grandes ya no se parten y el botón «…» de las mesas no sobresale.
- **Diálogos** (`src/MesaNexo.Web/src/form-dialog.tsx`): los campos cortos se acomodan en pares y ocupan todo el ancho cuando van solos; casillas como interruptores con explicación; importes con prefijo `$`; los números se pueden vaciar (el 0 ya no queda fijo) y se seleccionan al enfocarlos.
- **Crear o editar producto** en secciones: información básica, preparación (punto de cocción y extras en filas con su precio), información alimentaria (ingredientes, alérgenos y etiquetas con sugerencias de un toque) e imagen y disponibilidad.
- **Otros formularios:** el pago muestra «Efectivo recibido» solo con efectivo; los permisos de mesero solo aparecen para ese rol; las estaciones de cocina se editan como etiquetas; la receta se arma con filas (insumo o subreceta, cantidad y unidad) y la zona de reparto con filas de latitud y longitud; módulos y productos de promoción se eligen como fichas.
- Los datos se envían con el mismo formato que antes; el backend no cambió. La prueba de inventario ahora arma la receta con las filas nuevas.
