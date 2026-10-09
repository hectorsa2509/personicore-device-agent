# Contratos GraphQL para el agente de dispositivos

Documento para el developer · 28 de septiembre de 2026.

## 1. Qué se necesita implementar

| Operación | Para qué sirve | Estado |
|---|---|---|
| `getPanelistsRouter` | Consultar el router, su hogar y las personas que se pueden seleccionar | Consulta utilizada actualmente; conservar su contrato |
| `presenceCreatePersonalDevice` | Registrar el equipo, devolver su identificador único y actualizar sus datos posteriormente | No existe en el backend; implementar el contrato de este documento |
| `presenceReportPersonalDeviceIp` | Guardar la IP inicial, sus cambios y la última vez que el equipo reportó | Nueva operación propuesta; nombre pendiente de confirmación del developer |

Estas dos mutaciones son el contrato objetivo. No describen operaciones ya disponibles.
El agente tiene una llamada antigua e incompleta a la primera y todavía necesita
adaptarse a ambas. No se debe activar el envío real hasta completar la integración.
La firma antigua con `type: WIFI` no es el contrato que debe implementarse.

## 2. Flujo y significado de los identificadores

1. El agente descubre el nodo y obtiene sus credenciales y MAC.
2. Consulta el router, hogar y panelistas.
3. El usuario confirma los datos del equipo, propietarios y propietario principal.
4. Llama a `presenceCreatePersonalDevice` sin `deviceId` la primera vez.
5. El backend genera y devuelve un `deviceId` único e inmutable.
6. El agente lo guarda en disco y comprueba que puede leerlo antes de continuar.
7. Envía la IP inicial mediante `presenceReportPersonalDeviceIp` usando ese ID.
8. Al reiniciar recupera el mismo ID. Los cambios de IP no crean otro dispositivo.
9. Si cambian los datos o propietarios, llama a la primera mutación con el ID guardado.

| Identificador | Qué identifica | Quién lo proporciona | Cuándo cambia |
|---|---|---|---|
| `deviceId` | El equipo registrado | Backend, al primer registro | No cambia en el uso normal |
| `routerId` | El router autorizado | Consulta GraphQL | Si cambia el router/contexto autorizado |
| `householdId` | El hogar al que pertenece el router | Backend | No debe cambiar automáticamente el hogar del dispositivo |
| `machineId` | La identidad de red observada por el nodo, o derivada de la MAC | Agente/nodo | Puede cambiar con la tarjeta de red o una MAC privada |
| `requestId` | Una solicitud lógica de registro o actualización | Agente | Nueva solicitud: nuevo ID; reintento: mismo ID |
| `reportId` | Un reporte lógico de IP | Agente | Nuevo reporte: nuevo ID; reintento: mismo ID |
| `panelistIds` | Personas existentes seleccionadas como propietarios | Backend; selección en interfaz | Cuando cambia la selección |

`deviceId` no es la IP, la MAC, el `machineId` ni el ID del router. Se recomienda
un UUID generado por el backend con restricción de unicidad en almacenamiento.

La persistencia local debe sobrevivir apagados, reinicios y actualizaciones que
conserven los datos del usuario. El agente dispone de copias fuera de `state.json`
y ahora verifica el guardado. Borrar todas las copias, perder el disco o restablecer
el equipo puede eliminarlo. Entre cuentas de usuario distintas no está garantizado
sin almacenamiento compartido accesible. No prometer recuperación permanente por hardware.

## 3. Consulta del hogar

Se conserva esta consulta. `macAddress` es la MAC del nodo, no la de la computadora.

```graphql
query getPanelistsRouter($macAddress: String!) {
  getPanelistsRouter(macAddress: $macAddress) {
    router { id macAddress household { id friendlyName } }
    panelists {
      id firstName lastName email
      ips { ip friendlyName machineId deviceBrand deviceModel deviceType }
    }
  }
}
```

Se requieren router, hogar y al menos un panelista elegible para registrar.
Los nombres sirven para mostrar la lista; las mutaciones asocian IDs, no crean
personas ni sobrescriben sus datos personales.

## 4. Registrar o actualizar el equipo

### Esquema que debe implementar el backend

Reutilizar `DateTime` si ya existe en el esquema.

```graphql
scalar DateTime

input PresenceCreatePersonalDeviceInput {
  requestId: ID!
  routerId: ID
  deviceId: ID
  machineId: String
  name: String!
  deviceType: String!
  brand: String!
  model: String!
  deviceOs: String!
  panelistIds: [ID!]!
  principalPanelistId: ID!
  deviceShared: Boolean!
  isPersonal: Boolean!
}

type PresencePersonalDevicePayload {
  requestId: ID!
  deviceId: ID!
  routerId: ID!
  householdId: ID!
  acceptedAt: DateTime!
}

extend type Mutation {
  presenceCreatePersonalDevice(input: PresenceCreatePersonalDeviceInput!): PresencePersonalDevicePayload!
}
```

### Qué significa cada entrada

`!` significa obligatorio y no nulo; no significa necesariamente texto no vacío.

| Campo | Obligatorio | Origen y significado |
|---|---|---|
| `requestId` | Sí | UUID generado y persistido por el agente para esta solicitud y sus reintentos |
| `routerId` | Sí | Router obtenido de la consulta; el backend valida que el JWT lo autoriza |
| `deviceId` | No en el primer registro | ID guardado; obligatorio en las actualizaciones de un equipo ya registrado |
| `machineId` | Sí | Identificador de red del equipo; no usarlo como identificador permanente |
| `name` | Sí | Nombre del equipo, detectado y editable; no vacío |
| `deviceType` | Sí | Selección de la interfaz: `laptop`, `desktop`, `tablet`, `computer` o `server` |
| `brand` | Sí | Marca detectada y editable; admitir valor desconocido |
| `model` | Sí | Modelo detectado y editable; admitir valor desconocido |
| `deviceOs` | Sí | Sistema operativo detectado, no editable; texto descriptivo |
| `panelistIds` | Sí | Lista de propietarios existentes del hogar; al menos un ID distinto |
| `principalPanelistId` | Sí | Debe estar dentro de `panelistIds`; si hay uno solo, es ese |
| `deviceShared` | Sí | `true` cuando hay más de un propietario distinto |
| `isPersonal` | Sí | Inverso de `deviceShared` |

La interfaz muestra además el build del sistema operativo, pero actualmente no se
manda `OsVersion` por separado. La IP se envía en la segunda mutación. No se envía
`householdId` como fuente de autoridad: el backend lo resuelve desde el router.

### Petición y respuesta de ejemplo

Valores ficticios:

```graphql
mutation PresenceCreatePersonalDevice($input: PresenceCreatePersonalDeviceInput!) {
  presenceCreatePersonalDevice(input: $input) {
    requestId deviceId routerId householdId acceptedAt
  }
}
```

```json
{
  "input": {
    "requestId": "a1e9f01e-663a-4b43-b9ce-7ab48d32be84",
    "routerId": "router-123",
    "deviceId": null,
    "machineId": "node-client-identifier",
    "name": "Home laptop",
    "deviceType": "laptop",
    "brand": "Apple",
    "model": "MacBook Air",
    "deviceOs": "macOS",
    "panelistIds": ["panelist-1", "panelist-2"],
    "principalPanelistId": "panelist-1",
    "deviceShared": true,
    "isPersonal": false
  }
}
```

```json
{
  "data": {
    "presenceCreatePersonalDevice": {
      "requestId": "a1e9f01e-663a-4b43-b9ce-7ab48d32be84",
      "deviceId": "1caebf7a-3731-42dc-a469-ae29e307847a",
      "routerId": "router-123",
      "householdId": "household-789",
      "acceptedAt": "2026-09-28T18:00:00Z"
    }
  }
}
```

La respuesta incluye el `requestId` recibido, el `deviceId` persistente, el router
y hogar autorizados, y `acceptedAt`: fecha UTC del servidor después del commit.

### Reglas de registro

- Primera llamada: buscar por `(routerId, machineId)` antes de crear. Proteger esa
  unicidad en base de datos para evitar duplicados por concurrencia.
- Con `deviceId`: comprobar autorización y actualizar ese mismo equipo. Aunque el
  nombre de la mutación diga Create, no crear un nuevo equipo en actualizaciones.
- Sustituir el conjunto de propietarios y principal de manera atómica; no acumular
  propietarios anteriores. Validar pertenencia al hogar y coherencia de los booleanos.
- Una nueva MAC puede asociarse al mismo equipo si llega con su ID autorizado;
  rechazar conflictos con otro equipo. Sin ID no adivinar por nombre, persona o modelo.
- Devolver el ID solo después de guardar todo correctamente.

## 5. Reportar las IP del equipo

### Esquema que debe implementar el backend

```graphql
input PresenceReportPersonalDeviceIpInput {
  reportId: ID!
  routerId: ID
  deviceId: ID
  machineId: String
  ip: String!
}

type PresencePersonalDeviceIpPayload {
  reportId: ID!
  deviceId: ID
  routerId: ID
  ip: String!
  acceptedAt: DateTime!
}

extend type Mutation {
  presenceReportPersonalDeviceIp(input: PresenceReportPersonalDeviceIpInput!): PresencePersonalDeviceIpPayload!
}
```

### Qué significa cada entrada

| Campo | Obligatorio | Significado |
|---|---|---|
| `reportId` | Sí | UUID persistido para este reporte; se reutiliza en sus reintentos |
| `routerId` | Sí | Router/red donde se observó la IP; validar autorización |
| `deviceId` | Sí | Exactamente el ID devuelto al registrar y guardado por el agente |
| `machineId` | Sí | Identidad de red actual, para validar o asociar la interfaz |
| `ip` | Sí | IPv4 local del equipo en la ruta al nodo, por ejemplo `192.168.1.25`; no la IP pública |

Enviar inmediatamente después del registro y guardado del ID, cuando cambie la
IP o identidad de red, y cada 12 horas sin cambios (intervalo configurable).

```graphql
mutation PresenceReportPersonalDeviceIp($input: PresenceReportPersonalDeviceIpInput!) {
  presenceReportPersonalDeviceIp(input: $input) {
    reportId deviceId routerId ip acceptedAt
  }
}
```

```json
{
  "input": {
    "reportId": "ebc5e609-09d7-44bf-bff7-5232a1e3b86e",
    "routerId": "router-123",
    "deviceId": "1caebf7a-3731-42dc-a469-ae29e307847a",
    "machineId": "node-client-identifier",
    "ip": "192.168.1.25"
  }
}
```

Respuesta de ejemplo:

```json
{
  "data": {
    "presenceReportPersonalDeviceIp": {
      "reportId": "ebc5e609-09d7-44bf-bff7-5232a1e3b86e",
      "deviceId": "1caebf7a-3731-42dc-a469-ae29e307847a",
      "routerId": "router-123",
      "ip": "192.168.1.25",
      "acceptedAt": "2026-09-28T18:01:00Z"
    }
  }
}
```

### Reglas de IP

- No crear equipos ni emitir nuevos IDs desde esta mutación. Rechazar IDs inexistentes
  o no autorizados. La respuesta debe repetir el ID del equipo reportado.
- Actualizar IP actual y `lastSeenAt` con hora del servidor.
- Si cambia la IP, cerrar su intervalo anterior y abrir el nuevo. Si no cambia,
  actualizar `lastSeenAt` sin duplicar historial.
- La unicidad de una IP activa es por router/red, nunca global. Si se reasigna a otro
  equipo, cerrar la atribución anterior sin borrar dispositivos ni historial.
- No sustituir la IP enviada por la IP pública origen de la petición HTTP.

## 6. Seguridad, reintentos y errores

Ambas mutaciones utilizan el JWT del nodo en `Authorization`, sin prefijo `Bearer`.
Validar firma, vencimiento, emisor/audiencia y autorización para router y dispositivo.
El JWT es del nodo: no derivar al propietario humano desde ese token.

Guardar el resultado de cada operación y una huella de su payload con una clave
única `(routerId, operación, requestId/reportId)`. La misma clave con el mismo
payload devuelve el resultado original; con contenido diferente devuelve conflicto.
El recibo y los cambios deben guardarse en la misma transacción.

El agente debe persistir la solicitud pendiente antes de enviarla, para reutilizar
el mismo ID y payload después de un reinicio. Esta parte sigue pendiente de integración.
No reenviar entradas históricas del outbox como si fueran presencia actual: el outbox
actual es diagnóstico y no es una cola de reintentos.

Las dos mutaciones son transacciones independientes. Si se registra el equipo pero
falla la IP, conservar el `deviceId` y reintentar solo la IP. Si se pierde la respuesta
de creación, repetir la misma solicitud para recuperar el mismo ID. Si falla el
guardado local, no marcar registro completo ni continuar con IP hasta guardar el ID.
Un ID diferente nunca debe sobrescribir el existente silenciosamente.

No trasladar dispositivos entre hogares automáticamente. Definir un proceso
explícito para transferencias, pérdida completa del ID y conflictos de identidad.

Errores GraphQL con `extensions.code` estable:

| Código | Significado |
|---|---|
| `UNAUTHENTICATED` | JWT inválido o vencido |
| `FORBIDDEN` | Router/dispositivo/hogar no autorizado |
| `HOUSEHOLD_NOT_READY` | Hogar sin configurar |
| `INVALID_PANELIST` | Propietario inválido o ajeno al hogar |
| `INVALID_PRINCIPAL` | Principal no incluido entre los propietarios |
| `BAD_USER_INPUT` | Campos, IP o combinaciones inválidos |
| `IDEMPOTENCY_CONFLICT` | Misma clave de reintento con datos distintos |
| `DEVICE_ID_CONFLICT` | Identidad inconsistente o en conflicto |
| `INTERNAL_SERVER_ERROR` | Fallo interno sin confirmación de operación |

Un resultado nulo, vacío o con identificadores incorrectos no cuenta como éxito.

## 7. Qué falta de cada lado

**Backend:** implementar ambas mutaciones, esquemas, validaciones, transacciones,
restricciones de unicidad, recibos para reintentos e historial de IP. Definir retención
de recibos/historial y proceso de transferencias antes de producción.

**Agente:** adaptar builders y respuestas al contrato, agregar la mutación de IP,
persistir solicitudes pendientes, reutilizar el ID guardado y separar la confirmación
de registro de la de IP. No basta cambiar la consulta en `.env`.

**Ya corregido en el agente:** comprobar al menos una copia legible del ID guardado,
fallar si ninguna pudo persistirse, permitir reintentos y rechazar un ID distinto.
Esto no implica que el contrato nuevo ya esté integrado ni desplegado.

## 8. Pruebas de aceptación

- Primer registro devuelve un ID y el agente lo guarda antes de reportar IP.
- Cerrar, apagar y reiniciar conserva el ID; no crea otro dispositivo.
- Dos registros iniciales concurrentes o una respuesta perdida no duplican equipos.
- Fallos de guardado local no se presentan como registro completado.
- Cambiar IP, nombre o propietarios conserva el ID.
- Fallar el reporte de IP no repite la creación del equipo.
- Reportes sin cambios no duplican historial; IP reasignada no queda activa para dos equipos.
- JWT inválido, propietarios ajenos e IDs no autorizados se rechazan sin cambios parciales.
- Cambiar de interfaz con ID autorizado conserva identidad y detecta conflictos.
- El agente rechaza respuestas que intenten reemplazar el ID guardado.

Referencia técnica equivalente en inglés: [GRAPHQL-DEVICE-AGENT-SPEC.md](GRAPHQL-DEVICE-AGENT-SPEC.md).