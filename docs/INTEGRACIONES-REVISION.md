# Activación optativa de integraciones — revisión

Estado: código de adaptadores preparado; no se ejecutaron impresiones, llamadas AMI, geocodificación, PAC ni notificaciones. Sin claves ni destinatarios configurados.

Archivos concretos: `src/MesaNexo.Api/PrintDriver.cs`, `Telephony.cs`, `FiscalProcessing.cs`, `src/MesaNexo.Integrations/ExternalAdapters.cs`.

La revisión automática del entorno rechazó conectar estos adaptadores a rutas y comandos ejecutables porque habilitan efectos físicos, fiscales o envío de datos a terceros. Esta limitación proviene de la revisión automática, no del PDF ni de un SKILL.md.

Cambio propuesto para aprobar: añadir activación manual, opcional y desactivada por defecto en el servidor. Ninguna terminal recibe secretos. No implica ejecutar ninguna integración ahora.

- Impresora: comando de servidor para procesar un trabajo que un gerente ya solicitó. Usa `/usr/bin/lp` y el driver CUPS configurado. Estado reclamado incierto antes del efecto; spooler aceptado no equivale a papel impreso. Cajonera sólo con opción del driver verificada por administración.
- Caller ID: comando de servidor con configuración privada para escuchar Asterisk AMI sobre TLS y relacionar llamadas auténticas con CRM. No genera llamadas de prueba para mostrarlas como reales.
- Mapas: ruta privada autenticada y acción explícita de gerente para enviar una dirección a Google Geocoding y revisar la coordenada antes de guardarla. Alternativa manual siempre disponible.
- Fiscal: comando explícito de sandbox para solicitudes que un gerente autorizó individualmente. UUID de pruebas etiquetado sin validez fiscal. La emisión/cancelación real permanece desactivada; necesitaría autorización específica, PAC y datos fiscales revisados.
- Push: sólo adaptadores de servidor preparados. No activar envíos a destinatarios reales ni registrar dispositivos ficticios como integración terminada.

Pruebas: se utilizan transportes dobles HTTP y de impresora con respuestas explícitamente sintéticas. No equivalen a sandbox de proveedor ni a dispositivos físicos. La integración real requiere credenciales, permisos, configuración y pruebas autorizadas independientes.

## Decisión del usuario

Mantener los adaptadores aislados, sin rutas ni comandos de activación. No se incorpora el cambio propuesto. PAC real, CUPS, AMI, mapas y push no se ejecutan; contratos y pruebas permanecen disponibles para revisión futura. Los concretos HTTP viven en `MesaNexo.Integrations`, sin referencia desde los clientes nativos.

## Etapa 4 (22 de octubre de 2026): simuladores dentro del dominio

Se añadieron simuladores para PAC, terminal bancaria, pagos en línea, WhatsApp/correo, plataformas de reparto, PMS hotelero, mapas, Caller ID e impresora/cajón (`src/MesaNexo.Core/Integrations.cs`). Corren dentro de la transacción local, no abren conexiones y etiquetan todo como simulado. No modifican la decisión anterior: los adaptadores reales siguen aislados, sin rutas ni comandos de activación, y el modo `live` se rechaza. Detalle en [DEVMap-REQUISITOS.md](DEVMap-REQUISITOS.md#hoja-diagrama-maestro-del-ecosistema-total--etapa-4-integraciones-simuladas).
