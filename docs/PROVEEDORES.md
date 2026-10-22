# Proveedores y configuración revisable

El usuario decidió mantener los adaptadores aislados, sin rutas ni comandos de activación. Los archivos `deploy/providers.review.example.json` y `fiscal-issuer.review.example.json` son inventarios de requisitos para revisión: **ningún host los carga**. Rellenarlos no activa nada. No contienen credenciales ni se ofrece un procedimiento de activación. Los secretos futuros deberán permanecer en servicios de servidor autorizados, fuera del repositorio y terminales.

| Proveedor / componente | Contrato implementado y comprobación local | Recurso o trabajo que falta |
|---|---|---|
| CUPS / cajonera | Driver fijo sin shell; claim incierto durable; dobles de impresión | Cola/driver fabricante, equipo físico, autorización futura y prueba de papel/pulso |
| Asterisk | AMI TLS, eventos NewCallerid, deduplicación de perfil/llamada | PBX/certificado/permisos/telefonía reales; no se origina ninguna llamada |
| Google Geocoding | Conversión address→lat/lng sobre HTTPS | Clave restringida, cuenta y revisión humana de coordenada. Captura manual funciona sin él |
| Facturama | CFDI 4.0 REST, Folio/Date congelados, consulta y cancelación; contratos HTTP sintéticos | Emisor, cuenta sandbox autorizada, catálogos SAT, CSD y revisión fiscal. No hay timbrado masivo ni real |
| Stripe | Test-key-only PaymentIntent aislado; webhook HMAC/raw body, importe/moneda/intención/tenant y recibo transaccional | Cuenta y sandbox real, activación futura. PWA no ofrece pago online; la sucursal no acepta online sin conciliación independiente |
| Hub | Culinary Hub v1 firmado HMAC, evento estable, traducción al dominio | Acuerdo/API real del agregador. No es un conector certificado Uber/Rappi/DiDi |
| FCM | OAuth RS256 y HTTP v1 con validate_only por defecto | Cuenta de servicio, permisos y registro real de dispositivo; sin envío |
| APNs | JWT ES256, topic y HTTP/2, sandbox por defecto | Equipo/llave Apple, token y permiso real; sin envío |
| Correo del dueño | Aviso durable en outbox/espejo, visible con rol autorizado | Adaptador de correo no implementado; no se envían mensajes |

Los adaptadores HTTP están en MesaNexo.Integrations; los nativos no los referencian. PrintDriver, Telephony y FiscalProcessing son auxiliares aislados de servidor. Sus pruebas usan dobles explícitos; no hay evidencia de servicio externo contactado ni de sandbox del proveedor.

Fuentes primarias revisadas para los contratos: [SAT CFDI](https://www.sat.gob.mx/minisitio/Factura/emite_materialdeayudaparafactura.htm), [Facturama creación](https://facturama.mx/docs/es/api-web/crear-cfdi), [consulta](https://facturama.mx/docs/es/api-web/consultar-cfdi), [cancelación](https://facturama.mx/docs/es/api-web/cancelar-cfdi), [Stripe webhooks](https://docs.stripe.com/webhooks), [PaymentIntents](https://docs.stripe.com/api/payment_intents), [FCM HTTP v1](https://firebase.google.com/docs/cloud-messaging/send/v1-api), [APNs](https://developer.apple.com/documentation/usernotifications/sending-notification-requests-to-apns), [AMI v2](https://docs.asterisk.org/Configuration/Interfaces/Asterisk-Manager-Interface-AMI/AMI-v2-Specification/), [NewCallerid](https://docs.asterisk.org/Certified-Asterisk_20.7_Documentation/API_Documentation/AMI_Events/NewCallerid/), [Geocoding v3](https://developers.google.com/maps/documentation/geocoding/guides-v3/start). Es necesario revalidarlos antes de cualquier integración autorizada futura.
