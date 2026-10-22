# Ajuste visual nativo posterior a 0.2.0

El CSS/React del prototipo necesita un motor HTML para ejecutarse directamente. La aplicación operativa conserva Avalonia y controles nativos, según el alcance original. Sus reglas visuales se trasladan a estilos/plantillas XAML y transiciones nativas; no se carga studio.css, que actualmente tiene la paleta verde. La referencia elegida sigue siendo Grafito y Ámbar: capturas del prototipo, branding/reference-grafito-ambar.css y sus animaciones originales.

Cambios actuales: tema propio de botones para evitar superposición de reglas Fluent, transmisión explícita de familia/tamaño/peso/color al ContentPresenter, fuentes uniformes y pesos compatibles, foco visible, flechas y desplegables vectoriales. Cabecera compacta a 1360/1440; a 1280 conserva adaptación en dos filas. El acceso de gerente a cocina queda en Administración → Cocina en vivo / Historial de preparación. No se eliminan esas páginas ni sus permisos.

PrototypeMotion reproduce entrada de página (240 ms, 10 px verticales), diálogo (360 ms, 18 px), cuenta lateral (320 ms, ancho completo desde la derecha), fondo (180 ms) y estados de botones (140 ms), con curva .22,1,.36,1 para entradas. Son transiciones de opacidad/traslación; no alteran geometría de distribución ni asignación de foco. Las actualizaciones de datos de una misma página no repiten la animación de entrada. No se afirma que estén trasladadas todas las animaciones CSS: las salidas, escalados y microanimaciones adicionales siguen siendo refinamientos posibles.

«Reducir movimiento» se guarda en el almacén protegido de la aplicación, en Software y actualizaciones (botón de versión). En Windows se consulta además SPI_GETCLIENTAREAANIMATION, sin cambiar ninguna preferencia del sistema. En otras plataformas queda disponible la preferencia manual; no se afirma detección automática del ajuste del sistema allí.

Validación: 129 comprobaciones de interfaz en evidence/ui-style-review.json, incluyendo 360/834/1440, controles de cabecera a 1280/1360, fuentes resueltas, teclado, foco, animación hasta estado final y contenido inmediatamente visible con movimiento reducido. Las capturas se toman con movimiento suprimido para comparar el estado final, usando un frame de distribución previo. Se inspeccionaron login, mesas y cuenta lateral. Es renderizado headless, no prueba en dispositivos móviles.

Para verlo en Windows, con el backend que ya utilizas activo, cierra la ventana de la app y ejecuta desde la raíz:

```powershell
dotnet run --project .\src\MesaNexo.Desktop -c Debug
```

Si también necesitas iniciar el laboratorio, usa tools/Start-Lab.ps1. Este ajuste se encuentra en fuente/compilación Debug; los instaladores y ZIP 0.2.0 existentes conservan la entrega anterior y no se regeneraron durante esta revisión visual.

Referencias oficiales: [estilos y plantillas](https://docs.avaloniaui.net/docs/styling/control-template-walkthrough), [ContentPresenter](https://docs.avaloniaui.net/api/avalonia/controls/presenters/contentpresenter), [transiciones](https://docs.avaloniaui.net/docs/graphics-animation/control-transitions). El origen visual concreto está en los archivos locales del prototipo, no en una biblioteca de temas nueva.

Preferencia de Windows: [SystemParametersInfoW / SPI_GETCLIENTAREAANIMATION](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow), consultada mediante GET, sin usar la variante SET. Arranque del ejecutable Debug comprobado en evidence/windows-style-startup.json.

## Botones: bordes completos y rebote

A petición del usuario se eliminó la sombra inferior rígida y el borde gris sobre el ámbar. La superficie usa radio 11, trazo ámbar y sombra suave, con 3 px de margen para pintarlos completos y ClipToBounds desactivado. Los botones de navegación/enlace conservan su distribución compacta.

Hover: escala 1.025; pulsación: 0.965; clic: sube a 1.06 y regresa suavemente al tamaño de hover o reposo. Se transforma sólo la superficie, sin modificar el espacio de distribución del control. La animación observa Click y no genera clics, llamadas ni cobros adicionales. También funciona con Enter/Espacio; se cancela al salir, desactivar o retirar el control. Reducir movimiento conserva la respuesta visual de color sin escala.

Validación final: **143 comprobaciones de interfaz aprobadas**, incluidas 14 focalizadas de botones. Evidencia: evidence/ui-style-review.json, button-feedback.json y button-pop-frames.json. Se inspeccionaron las capturas buttons-login-1360.png y button-rounded-idle.png. Ratón/teclado son eventos reales del cliente headless; no una prueba en dispositivos físicos.

El ejecutable Windows más reciente está en `artifacts/desktop-actual/CulinaryRestaurant.exe`. Se genera aparte para permitir mantener abierta la app anterior mientras se compila. Con backend activo, cerrar la ventana anterior y usar desde la raíz:

```powershell
& ".\artifacts\desktop-actual\CulinaryRestaurant.exe"
```

Esta publicación de desarrollo requiere el runtime .NET 10 ya usado en este equipo. Los instaladores 0.2.0 anteriores permanecen sin regenerar.
