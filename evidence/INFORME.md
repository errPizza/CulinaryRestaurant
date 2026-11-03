# Informe de pruebas

- Commit: `d8fc1cace45c` (postgresql)
- Cambios sin confirmar en el código: sí
- Sistema: Windows-10-10.0.19045-SP0
- .NET: 10.0.402
- Node: v24.22.0
- Python: 3.13.15

| Riesgo | Suite | Comprobaciones | Pasaron |
|---|---|---:|---:|
| Dinero | money | 145 | 145 |
| Dinero | approvals | 32 | 32 |
| Dinero | boundaries | 10 | 10 |
| Dinero | live-integrations | 62 | 62 |
| Dinero | fiscal-sandbox | 31 | 31 |
| Inventario | inventory | 17 | 17 |
| Inventario | backoffice | 48 | 48 |
| Seguridad | licenses | 92 | 92 |
| Seguridad | checkpoints | 38 | 38 |
| Seguridad | approvals | 32 | 32 |
| Seguridad | privacy | 11 | 11 |
| Seguridad | tenants | 27 | 27 |
| Restauración | store-outbox | 42 | 42 |
| Restauración | checkpoints | 38 | 38 |
| Restauración | boundaries | 10 | 10 |
| Sincronización | store-outbox | 42 | 42 |
| Sincronización | boundaries | 10 | 10 |

Web: 1 pasaron, 0 fallaron, 0 omitidas, 58 s.

Un riesgo cubierto aquí significa probado con simuladores. Lo validado en real está en `docs/ESTADO-COMERCIAL.md`.
