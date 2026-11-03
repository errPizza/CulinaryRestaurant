# Culinary Restaurant — revisión visual 0.1.1

Referencia elegida por el usuario: versión Grafito y Ámbar de las capturas del prototipo. El CSS fuente actual contiene otra paleta verde; para esta revisión se contrastaron las capturas `grafito-ambar-mesas.png` y `grafito-ambar-cuenta.png` con las reglas coincidentes de `dist/assets/index-raKWGdRp.css`. La sección de referencia se conserva en `branding/reference-grafito-ambar.css` para trazabilidad, no se ejecuta como UI web.

## Colores y componentes

| Elemento | Valor de referencia |
|---|---|
| Barra superior | #202733 |
| Mesa seleccionada, cuenta y superficies oscuras | #273241 |
| Texto principal | #242E3B |
| Acción ámbar | #F2B93B |
| Texto sobre ámbar | #29210C |
| Lienzo | #E7E9ED |
| Texto secundario | #596575 |
| Bordes | #CDD3DC |
| Listo: fondo / texto | #DEF5E8 / #07653F |
| Retraso: fondo / texto | #FFEBE4 / #AF2D22 |

`PrototypeTheme.axaml` y los componentes `Prototype*.cs` trasladan cabecera, navegación agrupada, leyendas, cuadrícula de mesas, prioridades, resumen seleccionado, cajón lateral, formulario de productos, tarjetas, tablas y estados a Avalonia. Se conservan los trazos vectoriales SVG del prototipo como geometrías nativas en Brand.cs. No hay WebView ni CSS como motor de presentación.

La referencia CSS declara Inter, Segoe UI, sans-serif. En el Windows de validación no estaba instalada Inter: se utiliza Segoe UI, el fallback visible del prototipo. En Linux/móviles se incluye Inter mediante el paquete Avalonia.Fonts.Inter. La rasterización y disponibilidad de fuentes dependen del sistema; las pruebas no certifican igualdad de todos los píxeles entre sistemas diferentes.

Los textos principales siguen el prototipo, salvo la marca solicitada y las diferencias de aprovisionamiento/seguridad de la app nativa. El acceso no ofrece cuentas demo. La confianza TLS y el alta permanecen en «Configurar dirección del backend». Se conserva la indicación de pago manual y confirmación local. Los informes completos y acciones administrativas siguen disponibles.

## Logos originales

`branding/symbol-original.png` y `wordmark-original.png` son copias sin modificación de los PNG proporcionados por el usuario, encontrados en Descargas. Sus hashes constan en `branding/origin.json`. `tools/Prepare-Branding.ps1` prepara tamaños y contenedores de plataforma mediante ajuste proporcional, sin redibujar ni deformar el logo.

| Destino | Símbolo sin letras | Logo con letras |
|---|---|---|
| Windows | ICO de siete tamaños: ejecutable, ventana, tarea, instalador, desinstalador y acceso del menú Inicio | Acceso y Software/Acerca de |
| Linux | PNG hicolor de 16 a 512; entrada culinary-restaurant.desktop y ventana | Acceso y Software/Acerca de |
| Android | Recursos mipmap y lanzador adaptativo con margen de seguridad; nombre Culinary Restaurant | Vista de acceso amplia y Software/Acerca de |
| iOS (fuente) | AppIcon de 1024 con fondo opaco para el catálogo de activos | Acceso amplio y Software/Acerca de |

En pantallas estrechas se conserva el símbolo en cabecera para no reducir el texto del logo hasta hacerlo ilegible. El logo completo se mantiene en Software/Acerca de, disponible desde el botón de versión. Se usa un fondo claro cuando el texto oscuro del logo necesita contraste.

## Compatibilidad e identidad

El nombre visible y el ejecutable de escritorio cambian a Culinary Restaurant/CulinaryRestaurant. Se mantienen la carpeta del proyecto MesaNexoNativo, namespaces, identificadores de contratos, paquete Android/iOS `mx.mesanexo.nativeclient` (cambiado a `mx.culinaryrestaurant.mobile` en 0.3.3), claves de almacenamiento seguro y nombre del paquete Debian `mesanexo-native`. Esto evita convertir el cambio de marca en una instalación lógica distinta o perder credenciales/datos existentes. La versión es 0.1.1 (versión móvil interna 2).

Las publicaciones nuevas están bajo `artifacts/0.1.1/`; los paquetes y ZIP previos 0.1.0 se conservaron. No se modificó el prototipo ni se instalaron paquetes en la VM. Las capturas nuevas en evidence son renderizados nativos con datos de pruebas, no evidencia de ejecución en teléfono o Debian. iOS sigue sin compilarse por falta de Mac/Xcode.

## Ampliación DEVMap — 0.2.0

Se mantienen ambos PNG originales byte a byte. Símbolo sin letras: icono Windows/launcher Linux/Android/iOS, favicon/PWA. Logo completo: acceso y cabecera pública, donde hay tamaño legible. La versión 0.2.0 conserva Grafito y Ámbar, tipografías y controles previos; los módulos nuevos reutilizan sus mismos componentes nativos. Nuevas etiquetas de estado se traducen en PrototypeLabels.

Verify-Branding.ps1 comprueba hashes, metadatos Windows y etiqueta Android. Las capturas nativas cubren tres anchos; quedan pendientes renderizado en Debian y dispositivos Android/iOS, accesibilidad con tecnologías de asistencia y comparación visual en sus escalados reales.

## Ajuste visual posterior a 0.2.0

Corregidos fuente/plantillas de botón, cabecera compacta, iconos vectoriales y entradas animadas nativas con reducción de movimiento. Validación focalizada: 129 comprobaciones de interfaz aprobadas (evidence/ui-style-review.json). No se repitió la suite de dominio para un cambio de presentación. Detalles y límites: [DISENO-NATIVO-Y-CSS.md](DISENO-NATIVO-Y-CSS.md). Disponible al compilar Debug; paquetes 0.2.0 anteriores conservados.


## Marca actual — 16 de octubre de 2026

El usuario sustituyó la marca anterior por los dos PNG naranja/café: símbolo sin letras (`CR_Icon2.png`) y logo completo (`CR_Icon1.png`). Las fuentes exactas se conservan en `branding/symbol-original.png` y `branding/wordmark-original.png`; `branding/origin.json` registra sus hashes. La marca anterior queda archivada en `branding/history`.

`tools/Prepare-Branding.ps1` sincroniza ambos PNG con el cliente Avalonia compartido, el cliente React/Tauri y el portal público. Genera el ICO real multirresolución para Windows/Tauri/favicon, PNG para Linux, lanzadores Android por densidad y el AppIcon RGB opaco de iOS. No se acepta renombrar un PNG como `.ico`. `tools/Verify-Branding.ps1` comprueba fuentes, copias y formatos; evidencia en `evidence/branding-current.json`.

Por petición del usuario se retiró el logo grande del inicio de sesión, tanto en React como en la vista nativa compartida. Se mantiene libre ese espacio para el próximo contenido que indique el usuario. El símbolo compacto de cabecera conserva la identidad de la aplicación. El logo completo permanece disponible como recurso de marca y en las superficies distintas del login donde ya se utilizaba.

Los paquetes históricos no se reescriben. Los recursos móviles/Linux están actualizados en fuente; esto no equivale a probarlos en dispositivos o a compilar iOS desde Windows.

La compilación Tauri declara los iconos como dependencias en `build.rs` para regenerar también el recurso Windows cuando cambien las imágenes. La revisión del ejecutable comprueba sus siete fotogramas ICO incorporados.
