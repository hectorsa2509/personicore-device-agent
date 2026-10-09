# Propuesta: estado horario del proxy

Estado: propuesta de contrato; no se invoca desde los agentes hasta que el backend la implemente.
Aplica a Windows y macOS. Es independiente del reporte de IP, que sigue enviándose sólo al cambiar.

```graphql
scalar DateTime

enum DeviceProxyStatusReason {
  ACTIVE
  DISABLED
  NO_NODE
  OTHER_HOUSEHOLD
  CERTIFICATE_REQUIRED
  CONFIGURATION_MISSING
}

input PresenceDeviceProxyStatusInput {
  reportId: ID!
  deviceId: ID!
  routerId: ID!
  observedAt: DateTime!
  enabled: Boolean!
  active: Boolean!
  reason: DeviceProxyStatusReason!
}

type PresenceDeviceProxyStatusPayload {
  reportId: ID!
  deviceId: ID!
  receivedAt: DateTime!
}

extend type Mutation {
  presenceReportDeviceProxyStatus(input: PresenceDeviceProxyStatusInput!): PresenceDeviceProxyStatusPayload!
}
```

`enabled` expresa la intención del usuario (interruptor); `active` indica que la configuración
Personicore está efectivamente aplicada al sistema y apunta al nodo autorizado. Un interruptor
encendido pero pausado se reporta con `enabled: true`, `active: false` y la razón correspondiente.
No se debe deducir `active` del texto del menú ni solamente de `ProxyWanted` o de un estado
persistido: verificar la configuración real del sistema. No implica que todas las aplicaciones
respeten el proxy ni que el tráfico esté llegando al panel.

## Envío y aceptación

- Primer reporte al registrar/conectar el dispositivo; después cada 60 minutos mientras el
  agente esté ejecutándose, aunque no haya cambio de IP. Puede adelantarse al cambiar el estado.
- No crear un dispositivo para reportar: requiere el `deviceId` existente. El servidor valida
  que el token autorice ese dispositivo y su router; nunca reasigna hogares por esta mutación.
- Guardar `reportId` (UUID) y el payload antes de enviar; los reintentos de ese mismo reporte
  mantienen ambos. El backend deduplica por dispositivo y `reportId`; un payload distinto
  con la misma clave debe rechazarse. Nuevas observaciones llevan nueva clave.
- Respuesta sólo después de persistir. `receivedAt` lo fija el servidor en UTC y `observedAt`
  es la hora de observación UTC del cliente; no usar el reloj del cliente como única prueba
  de vigencia ni permitir que un reporte atrasado sobrescriba uno más reciente.
- Sin conectividad o credencial válida no hay entrega horaria garantizada. Reintentar fallos
  transitorios con espera exponencial limitada; al reconectar, reportar el estado actual.
  Nunca enviar al hogar visitado con la identidad del hogar original.
- En `sendToBackend: false` o `DryRun`, escribir al outbox sin enviar la mutación.
- Un reporte ausente por más de dos horas se muestra como **desconocido / sin reporte**, no
  como desactivado. Conservar por separado estado, última observación y última recepción.
- Si el esquema aún no ofrece la operación, deshabilitar este envío para esa sesión y dejar
  un diagnóstico; la inscripción y los reportes de IP deben seguir funcionando.

## Avisos locales implementados

Ambos menús incluyen **Test proxy notification**, que dispara el aviso inmediatamente,
incluso con el proxy encendido, sin cambiar ajustes ni reiniciar el intervalo automático.
El mensaje recomienda activar el proxy y completar el asistente si hace falta.

El primer aviso automático llega después de 30 minutos con el interruptor apagado; se repite
cada 30 minutos mientras siga apagado. Encenderlo reinicia esa espera. No pide encenderlo si
ya está encendido pero temporalmente pausado fuera del hogar. La cadencia vive en memoria:
cerrar el agente detiene los avisos y abrirlo inicia una espera nueva. La visibilidad de banners
depende de los permisos de notificaciones y del modo No molestar del sistema operativo.

## Ejemplo de operación

```graphql
mutation PresenceReportDeviceProxyStatus($input: PresenceDeviceProxyStatusInput!) {
  presenceReportDeviceProxyStatus(input: $input) {
    reportId
    deviceId
    receivedAt
  }
}
```

Variables ilustrativas para un proxy apagado:

```json
{
  "input": {
    "reportId": "9ac9b7c0-f13c-48b5-9c87-16192405906e",
    "deviceId": "existing-device-id",
    "routerId": "authorized-router-id",
    "observedAt": "2026-10-09T15:00:00Z",
    "enabled": false,
    "active": false,
    "reason": "DISABLED"
  }
}
```

Validaciones mínimas: rechazar `active: true` con `enabled: false`, exigir `ACTIVE` cuando
`active` sea verdadero y una razón distinta de `ACTIVE` cuando sea falso. Probar duplicados,
reintentos tras pérdida de respuesta, token de otro hogar, reportes fuera de orden, equipo
suspendido y estado desconocido cuando deja de reportar.

### Entrega de avisos en macOS

La app usa `UNUserNotificationCenter` dentro de su propio proceso y solicita permiso para
**Personicore Device Agent**. El delegado permite mostrar banners con la app en primer plano.
La prueba sólo muestra una ventana si los permisos/banners están deshabilitados o hay un
error. Un envío aceptado no abre ventanas ni activa la app; no garantiza un banner visible
si Focus / No molestar lo silencia. La ventana de error permite abrir los ajustes de
notificaciones. El diagnóstico se guarda
en el log; los recordatorios automáticos no abren ventanas de resultado.

El puente nativo se compila para arm64 y x86_64 antes de firmar el bundle. Es necesario usar
la app empaquetada para tener identidad y biblioteca nativa; ejecutar sólo el proyecto con
`dotnet run` no proporciona ese entorno.

### Instrucciones para habilitar y probar las notificaciones

1. En **Ajustes del Sistema → Notificaciones → Personicore Device Agent**, activar
   **Permitir notificaciones** y elegir **Tiras (Banners)** o **Alertas (Alerts)**.
2. Para verlas al duplicar o compartir pantalla, volver a la página principal de
   **Notificaciones** y activar **Allow notifications when mirroring or sharing the display**
   (permitir notificaciones al duplicar o compartir pantalla). Es un ajuste global:
   también pueden verse avisos de otras apps. Personicore no lo activa automáticamente.
3. Revisar **Concentración / No molestar** si siguen ocultas. Abrir el Centro de
   notificaciones haciendo clic en la fecha y hora y volver a usar **Test proxy notification**.

macOS puede aceptar una solicitud y después silenciarla por el estado de la pantalla
(`muted by display state`). Por eso aceptar el envío no confirma que se haya visto el aviso.
