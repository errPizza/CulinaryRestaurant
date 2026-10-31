# Compatibilidad de versiones

Qué versiones funcionan juntas y qué hacer para actualizar o revertir. Los números están en `src/MesaNexo.Core/Contract.cs`.

## Contratos

| Contrato | Valor | Dónde se comprueba | Si no coincide |
|---|---|---|---|
| API de cliente | 1 | `api/identity`, al iniciar sesión | El cliente pide actualizarse y no envía credenciales |
| Esquema de registros | 2 | `Ready`, al arrancar el backend | El backend no queda listo y registra `COS-SCHEMA-NEWER` |
| Eventos a la central | `culinaryos-events-v1` | `CentralService` | La central rechaza el lote; los eventos siguen pendientes |
| Extensión del almacén | `capabilities_v1` | `Ready` | El backend no queda listo hasta actualizarla |

## Matriz

| Backend | Cliente 0.2.x | Cliente 0.3.x | Esquema | Central |
|---|---|---|---|---|
| 0.2.x | Sí | Sí | 1 o 2 | `events-v1` |
| 0.3.x | Sí | Sí | 2 | `events-v1` |

Las columnas de 0.2.x salen de que el contrato no cambió entre versiones: API 1 y eventos v1. No hay una prueba con el binario 0.2.x real.

## Cuándo sube cada número

- **API:** al quitar o cambiar el significado de un campo o una ruta. Agregar campos o acciones no la sube. Un cliente anterior ignora lo que no conoce, como `outcome`, `refund` o `dualControlAmount`.
- **Esquema:** cuando un backend anterior leería mal los registros. Requiere una migración en `SchemaUpgrade.cs`.
- **Eventos:** al cambiar el formato de un evento. La central debe aceptar las dos versiones antes de que una sucursal cambie.

## Actualizar

1. Cerrar cuentas y sesiones, y dejar los trabajos de inventario terminados. La migración se niega si no.
2. Actualizar la extensión del almacén, si cambió, y luego el backend.
3. `MesaNexo.Api --upgrade-schema`. Crea una copia con `pg_dump`, verifica que sea de esta sucursal y sólo entonces cambia el esquema. Repetirlo no hace nada.
4. Actualizar los clientes. Los eventos pendientes se conservan.

## Revertir

Una migración de esquema no se deshace en su lugar: se vuelve a la copia. El binario anterior rechaza una base más nueva, así que revertir sólo el programa no alcanza.

1. Detener el backend.
2. Instalar la versión anterior.
3. Cargar la copia de `schema-backups`, con el procedimiento de `CulinaryOS/docs/SIMULACRO-RESTAURACION.md`.
4. Volver a arrancar. Lo que se haya vendido después de la copia se pierde, así que hay que hacerlo antes de abrir o conciliar a mano.

Sin cambio de esquema, revertir es reinstalar el programa anterior.
