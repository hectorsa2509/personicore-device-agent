# Portar el core a Android e iOS

Para los desarrolladores de Android e iOS. Cubre las tres piezas que se pidieron —
**encontrar el nodo meter**, **instalar el certificado HTTPS** y **pasar el tráfico por el
proxy** — tal como las implementa hoy el agente de escritorio, cuál es el equivalente en cada
sistema móvil, y qué reglas tienen que sobrevivir al port porque romperlas ya nos costó caro
una vez.

Este documento está en español a propósito; el resto de `docs/` está en inglés.

Para el comportamiento de punta a punta está [`FLOW.md`](FLOW.md), y para lo que acepta el
backend está [`GRAPHQL-CONTRACT.md`](GRAPHQL-CONTRACT.md). Aquí no se repiten.

---

## 0. El párrafo que cambia la estimación

**Ni Android ni iOS permiten que una app instale una CA raíz de confianza, y ninguno permite
que una app ponga el proxy del sistema.** Las dos son decisiones deliberadas de plataforma, no
huecos que se puedan rodear. Por eso todo lo de las secciones 2 y 3 es un *diseño distinto* en
móvil, no un port — y la consecuencia es que **el HTTPS de otras apps no se puede descifrar en
un teléfono normal**:

- En Android 7 (API 24) y posteriores, las apps no confían en CAs instaladas por el usuario a
  menos que su propio `network_security_config.xml` lo diga explícitamente. Tu app puede
  habilitarse a sí misma; Chrome, Instagram y las demás no. Una CA de usuario no te compra nada
  fuera de tu propio proceso.
- En iOS la CA sí puede quedar en confianza total, pero sólo con un perfil de configuración que
  el panelista instala *y además* habilita a mano en una segunda pantalla de Ajustes, y
  interceptar el tráfico de otras apps exige un entitlement de Network Extension que Apple
  otorga caso por caso.

Así que planeen el panel móvil alrededor de **medición a nivel de metadatos** — hostnames por
DNS/SNI, número de conexiones, volúmenes, tiempos — recolectados por un túnel VPN y sin
interceptar TLS. El SSL-bump completo sólo es realista en equipos aprovisionados (device-owner
en Android, o supervisión MDM en iOS) que el propio panel entregue. **Decidan cuál de los dos
es el panel antes de construir nada**: cambia la app, los entitlements y el texto de
consentimiento.

Lo que sí porta, casi sin cambios, es la sección 1 y todo lo de la sección 4.

| Pieza | ¿Porta? | Forma en móvil |
|---|---|---|
| Descubrimiento del nodo | **sí**, lógica sin cambios | Los mismos tres pasos, menos hilos, sólo en primer plano |
| Leer el hogar, registrar, reportar la IP | **sí**, sin cambios | Las mismas dos mutaciones, las mismas llaves de idempotencia |
| `machineId`, device id, mitades del estado | **sí**, con una salvedad (§4) | El nodo entrega el `machineId`; un teléfono no puede calcularlo |
| Certificado HTTPS | **no** | Instalación conducida por el usuario, propia de cada OS, verificar antes de ofrecer |
| Proxy del sistema | **no** | `VpnService` / `NEPacketTunnelProvider` |
| Menú de bandeja, diálogos | n/a | UI móvil, pero las mismas palabras — ver §4 |

---

## 1. Encontrar el nodo meter

El nodo es una caja OpenWrt que hace de puente en el hogar. Es un **puente transparente**, así
que no tiene TLS y todo lo de abajo es HTTP plano dentro de la LAN. No es el router del
panelista, y el agente nunca le dice "router" en nada que el panelista lea: se llama **nodo
meter**.

### Lo que sirve

| Ruta | Devuelve | Para qué |
|---|---|---|
| `/cgi-bin/router_info.sh` | `{ wan_mac, lan_mac, ip }` | **Identidad.** Esto es lo que convierte a un host en nodo |
| `/cgi-bin/get_config.sh` | `{ jwt, graphql }` | La credencial y el endpoint que usa el propio nodo |
| `/cgi-bin/list_clients_json.sh` | `[{ ip, mac, machineId, name, state, brand, model, os, deviceType }]` | Lo que el nodo ve en el puente — **de aquí saca un teléfono su propio `machineId`** |

La llave del hogar es `wan_mac` cuando existe, y si no `lan_mac`, normalizada a 17 caracteres
(`aa:bb:cc:dd:ee:ff`, en minúsculas). **Un host es un nodo si y sólo si `router_info.sh`
devuelve una MAC válida.** Cualquier otra cosa —una impresora, un NAS, la página web del router
del ISP— no lo es, conteste lo que conteste.

### Los tres pasos, del más barato al más caro

1. **Ping al nombre** `router.example.invalid`. El nodo resuelve ese nombre para sí mismo
   con dnsmasq, así que una respuesta significa que lo tienes enfrente. En un teléfono se usa un
   **connect TCP** en lugar de ICMP (ver las notas por plataforma).
2. **Si el nombre no contesta, barre buscando un puerto abierto.** Un connect TCP pelón es
   muchísimo más barato que una petición HTTP, así que primero se filtra por puertos: un barrido
   de cientos de peticiones se vuelve un puñado. Sondea `22`, `3129` y `80` **en paralelo por
   dirección**, no en secuencia — en secuencia cada puerto extra multiplica lo que cuesta una
   dirección muerta; en paralelo una dirección muerta sigue costando un solo timeout.
3. **Identifica a los sobrevivientes** por HTTP con `router_info.sh`. Gana el primero.

El orden del barrido importa y no es arbitrario:

1. las direcciones a las que resuelve el nombre — se prueban aunque el ping haya fallado, porque
   el nombre es un registro público que apunta a la IP privada del nodo;
2. la puerta de enlace por defecto — en despliegues donde el nodo *es* la puerta de enlace, esto
   es un solo sondeo en lugar de un barrido completo;
3. cada subred local activa, con la máscara real del adaptador;
4. el /24 alrededor de cada dirección resuelta — el nodo no siempre está en el segmento del
   panelista; puede estar detrás del router del ISP en otro, alcanzable por ruteo.

Los resultados se devuelven en **orden de sondeo, no de llegada**, para que los logs sigan
siendo comparables entre ciclos.

Una vez encontrado un nodo, **revalídalo en el siguiente ciclo en lugar de volver a barrer.** El
barrido es la única parte cara del ciclo. Vuelve a descubrir sólo cuando el nodo conocido deje
de contestar.

### Parámetros que usa el escritorio

| Ajuste | Default en escritorio | Sugerido en móvil |
|---|---|---|
| `routerHost` | `router.example.invalid` | igual |
| `routerPort` | `80` | igual |
| `routerTimeoutSeconds` | `4` | igual |
| `discoveryPorts` | `22, 3129, 80` | igual |
| `portProbeTimeoutMs` | `400` | `600` — el RTT de Wi-Fi en un teléfono es peor |
| `subnetSweepConcurrency` | `128` | `24–32`; con 128 sockets te van a limitar y te vas a comer la batería |
| `pollIntervalSeconds` | `3600` | igual, pero sólo mientras la app pueda correr |
| `ipWatchIntervalSeconds` | `30` | usa el callback de cambio de red del OS, no un temporizador |
| `proxyWatchIntervalSeconds` | `30` | sólo mientras el túnel esté arriba |

### Notas de Android

- `INTERNET` y `ACCESS_NETWORK_STATE`. Para leer el estado del Wi-Fi (SSID/BSSID) además hace
  falta permiso de ubicación, que es una conversación de consentimiento aparte — evítalo si
  puedes cachear contra el objeto `Network` en lugar del SSID.
- El HTTP plano hacia la LAN necesita `android:usesCleartextTraffic="true"` o, mejor, un
  `network_security_config.xml` con la excepción de texto claro **sólo** para rangos privados.
  La bandera global es un hallazgo en cualquier revisión de seguridad.
- `ConnectivityManager.NetworkCallback` te da los cambios de enlace sin hacer polling. Amarra
  tus sockets al objeto `Network` del Wi-Fi: en un teléfono con datos móviles arriba, un socket
  sin amarrar hacia una dirección privada puede salir por la interfaz celular y no llegar a
  ningún lado.
- Barre **sólo en Wi-Fi**, nunca en datos, y nunca desde un job en segundo plano.

### Notas de iOS

- **`NSLocalNetworkUsageDescription` es obligatorio** desde iOS 14, y el sistema muestra un
  diálogo la primera vez que tocas una dirección local. Sin eso, toda conexión a la LAN falla.
  Agrega `NSBonjourServices` si además exploras Bonjour. Escribe ese texto con cuidado: es lo
  primero que el panelista lee sobre el producto.
- No hay API de ICMP sin raw sockets, así que el paso 1 se vuelve un connect TCP a
  `router.example.invalid:80` con el mismo timeout. Significa lo mismo: contesta o no
  contesta.
- `NWPathMonitor` para los cambios de enlace. Para los sondeos prefiere `NWConnection` sobre
  `URLSession`: lo que quieres es un timeout de conexión, no una petición.
- Barrer un /24 es una operación de primer plano. En segundo plano tienes segundos, no las
  decenas de segundos que puede costar un barrido — cachea el último nodo por red y revalida.

### Reglas que tienen que sobrevivir

- **Sin nodo, no hay acciones.** Hasta que un nodo conteste, la app no ofrece más que "ver el
  estado" y "volver a intentar". Registrarse, el certificado y el proxy quedan bloqueados, y la
  pantalla dice por qué. La única excepción es la siguiente.
- **Apagar el proxy siempre está permitido**, haya nodo o no. Nunca dejes al panelista con un
  switch que no puede deshacer.
- **Sólo IPv4 privada sale del dispositivo como dirección.** Cuatro octetos decimales, sin ceros
  a la izquierda, sin dos puntos, y únicamente `10/8`, `172.16/12`, `192.168/16`, `169.254/16`.
  El parser de direcciones de tu plataforma no es lo bastante estricto: `inet_aton` y parientes
  aceptan `1.2.3` y `192.168.01.1`, y el resolver del backend rechaza los dos.

---

## 2. El certificado HTTPS

### Qué es

Una sola CA, embebida en la app, únicamente el certificado público. Antes de usarse para
cualquier cosa se verifica contra:

- un **SHA-256 fijado (pinning)**:
  `741FFDF0F0CBD5E75255B797F4208BCED3D1C27181DFC5FB2120F33AD231D94C`
- `basicConstraints` diciendo que es una CA
- su ventana de validez (2025-05-13 → 2035-05-11 UTC)
- y que el bundle **no traiga llave privada** — el agente de escritorio se niega a cargarlo si
  la trae, y el de ustedes también debería. Tener esa llave privada es poder suplantar a
  cualquier sitio frente a todo dispositivo que confíe en ella.

La historia completa está en [`CERTIFICATES.md`](CERTIFICATES.md).

### Qué hace el escritorio

Instala en el almacén de confianza de la máquina detrás de un solo diálogo de administrador, y
después **verifica la confianza por separado** — Windows construye una cadena X509 para
autenticación de servidor contra el almacén raíz de la máquina; macOS corre
`security verify-cert -p ssl -l -L`. Instalar no enciende el proxy: el proxy vuelve a preguntar
por la confianza siempre, incluso al reanudarse solo.

### Android

Una app **no puede** instalar una CA. Lo que sí puede hacer:

```kotlin
val intent = KeyChain.createInstallIntent().apply {
    putExtra(KeyChain.EXTRA_CERTIFICATE, caDerBytes)
    putExtra(KeyChain.EXTRA_NAME, "Personicore")
}
startActivity(intent)
```

Eso abre el instalador del sistema; el panelista confirma, y el certificado aterriza en el
almacén **de usuario**. Dos cosas que hay que saber antes de diseñar la pantalla:

- Google ha movido la ruta manual más adentro en casi cada versión (hoy es más o menos
  *Ajustes → Seguridad → Cifrado y credenciales → Instalar un certificado → Certificado de CA*,
  y los fabricantes la renombran). El instructivo tiene que depender de la versión, y hay que
  probarlo en los teléfonos reales a los que el panel reparte, no en un emulador.
- El sistema muestra una advertencia fuerte y, en muchas compilaciones, una notificación
  permanente de "la red puede estar siendo monitoreada". Eso es el OS haciendo su trabajo.
  Explica para qué es el certificado **antes** de que el panelista llegue a esa pantalla, o se
  va a salir de ahí.

En un equipo aprovisionado, un **DPC con device-owner** puede instalar en el almacén del
sistema con `DevicePolicyManager.installCaCert()` y sin interacción del usuario. Esa es la única
ruta que logra que otras apps confíen.

Verifica la confianza validando la cadena con un `CertPathValidator` sobre las anclas del
sistema + las del usuario, o más honestamente completando un handshake TLS a través del proxy
contra un host conocido. Verifica, no supongas: el panelista puede quitar el certificado desde
Ajustes en cualquier momento.

### iOS

Una app **tampoco puede** instalar una CA, ni puede pedirlo. Hay dos rutas:

1. **Perfil de configuración (.mobileconfig).** El panelista lo descarga, lo instala en
   *Ajustes → General → VPN y gestión de dispositivos*, y entonces —este es el paso que todo el
   mundo olvida— **lo habilita a mano** en *Ajustes → General → Información → Ajustes de
   confianza del certificado*. Apple deja ese segundo paso manual a propósito y ninguna API
   puede hacerlo por ellos. Presupuesta un instructivo de verdad, con capturas de las dos
   pantallas, y **detecta el resultado** en lugar de confiar en que ocurrió.
2. **Supervisión por MDM.** Un equipo supervisado recibe la CA instalada y en confianza sin el
   segundo paso. Sólo realista si el panel entrega los teléfonos.

No hay API pública para leer el almacén de confianza del sistema, así que "¿está en confianza?"
se contesta igual que en Android: intenta un handshake TLS a través del proxy y observa. Hazlo
una vez por ciclo y carga la respuesta; nunca mientras dibujas una pantalla.

### Reglas que tienen que sobrevivir

- **Reportar antes de ofrecer.** Si el certificado ya está en confianza, el elemento dice
  *instalado* y al tocarlo muestra el estado. No vuelve a ofrecer la instalación. En escritorio
  esa regla existe porque reinstalar gasta una contraseña de administrador en nada; en móvil
  gasta la paciencia del panelista en un recorrido por Ajustes que ya completó.
- **La verificación de confianza no es gratis** — en escritorio lanza un proceso. Evalúala una
  vez por ciclo y llévala en el snapshot. Nunca la pidas mientras renderizas.
- **Vuelve a verificar justo después de instalar**, o la interfaz seguirá ofreciendo lo que
  acaban de hacer.
- **No hay proxy sin confianza vigente.** El escritorio envuelve el switch del proxy en un guard
  que revalida la confianza en todas las rutas de activación, incluida la reanudación automática
  tras un cambio de red, y falla cerrado si la verificación misma truena. Conserven ese guard. En
  móvil, revaliden además al volver a primer plano: el panelista puede borrar el perfil mientras
  la app está suspendida.

---

## 3. El proxy

### Qué es en escritorio

El proxy HTTP y HTTPS del sistema, apuntado al nodo en el **puerto 3129** —fijo, nunca tomado de
una petición— con una lista de excepciones que mantiene fuera del proxy a Bonjour, al propio
nodo, a las impresoras y a la página del router del ISP:

```
*.local  169.254/16  10.0.0.0/8  172.16.0.0/12  192.168.0.0/16  127.0.0.1  localhost
```

macOS necesita root para esto, así que el agente instala un helper mínimo una sola vez (una
contraseña) y le habla por un socket local con cinco verbos — `APPLY`, `REPOINT`, `RESTORE`,
`OWNED`, `STATUS` — y nada más. Windows no necesita elevación: escribe los ajustes del usuario
actual.

### Android

`VpnService`. Levantas un túnel, declaras rutas y reenvías. Dos formas:

- **Sólo metadatos** (lo recomendado para un panel de consumo): no termines nada; lee los
  metadatos que necesitas —dirección destino, el SNI del ClientHello de TLS, consultas DNS,
  conteo de bytes— y reenvía el resto intacto. No hace falta certificado, lo que borra la
  sección 2 de la ruta crítica.
- **Reenviar al proxy del nodo**: saca el TCP del túnel por `nodo:3129`. Sólo tiene sentido con
  una CA en el almacén del sistema, es decir, una compilación con device-owner.

Un DPC con device-owner también puede poner el proxy del Wi-Fi directamente con `ProxyInfo`, que
es lo más parecido al comportamiento de escritorio — otra vez, sólo en equipos aprovisionados.

### iOS

`NEPacketTunnelProvider` dentro de una Network Extension, configurado con
`NETunnelProviderManager`. Pon `NEProxySettings` en el `NEPacketTunnelNetworkSettings` del túnel
y el sistema aplica los ajustes de proxy a todo el dispositivo mientras el túnel esté arriba: ese
es el análogo real del switch de escritorio, y es muchísimo menos trabajo que escribir un
reenviador de paquetes.

**Pidan el entitlement desde el día uno.**
`com.apple.developer.networking.networkextension` con `packet-tunnel-provider` se otorga caso por
caso, y App Review va a preguntar qué hace el túnel con el tráfico. Un panel de medición con
consentimiento informado es una respuesta defendible; "desciframos el HTTPS de otras apps" no es
una que sobreviva la revisión en el App Store público. Si el panel necesita interceptar, planeen
distribución enterprise o por MDM y pónganlo en el plan **ahora**, no después de construirlo.

### Reglas que tienen que sobrevivir

Cada una de estas está en el agente de escritorio porque la alternativa se descubrió
corriéndolo:

- **Nunca pises un proxy que puso el panelista.** Guarda lo que había antes del primer APPLY y
  restaura exactamente eso. La propiedad se lleva **por servicio de red y por valor**, no con una
  bandera global: una máquina tiene varios servicios, el panelista puede tomar uno y dejar los
  demás, y una sola respuesta sí/no o le pisa el que cambió o abandona los que todavía son tu
  responsabilidad.
- **Sigue al nodo.** Un watchdog revisa cada 30 s y vuelve a apuntar cuando el nodo reaparece en
  otra dirección, para que la navegación no se corte y nadie tenga que reactivar nada. Al
  reapuntar sólo se toca lo que el agente todavía posee — eso es lo que separa `REPOINT` de
  `APPLY`.
- **Pausarse fuera de casa y volver solo al llegar.** Fuera de la red meter el proxy apunta a
  nada, así que se quita y se escribe una marca de *pausado*. Sin esa marca, el agente lee su
  propia red de seguridad como si el panelista lo hubiera apagado, y se apaga para siempre.
- **Una salida limpia restaura.** Un kill no — por eso en escritorio el watchdog es un proceso
  aparte. En móvil el OS mata apps cuando quiere, así que **la extensión** tiene que ser la dueña
  del estado y poder restaurar sin la app.
- **Apagarlo nunca se bloquea.**
- **Las llamadas del propio agente no pasan por ahí.** Todos los clientes HTTP del core ponen
  "sin proxy" explícitamente. Con el proxy encendido, el nodo interceptaría el TLS y el agente
  perdería su propio backend. En móvil el equivalente es excluir tu propio tráfico del túnel.
- **Valida antes de aplicar.** El helper rechaza cualquier dirección que no sea IPv4 privada y
  cualquier dirección donde no conteste un nodo meter (`router_info.sh` con una MAC válida).
  Conserven las dos verificaciones: son lo que impide que un proceso local use su componente
  privilegiado para secuestrar tráfico.

---

## 4. Alrededor de las tres

Breve, porque esto está en el documento del contrato, pero es justo donde una segunda
implementación se separa de la primera.

- **`machineId` es la llave de unión, y un teléfono no puede calcularla.** El escritorio la
  deriva de la MAC del adaptador —`base64(mac sin dos puntos)` sin el relleno `=`, más los dos
  primeros caracteres hex de la MAC, más `PMPM`— pero **los dos sistemas móviles llevan años
  devolviendo una MAC falsa fija**, y los dos aleatorizan la MAC de Wi-Fi por red. Entonces:
  averigua tu propia IP, llama a `list_clients_json.sh`, busca la entrada cuyo `ip` sea el tuyo y
  toma **su** `machineId`. El nodo ve la MAC real del socket. No hay plan B: si el nodo todavía
  no te lista, espera al siguiente ciclo en lugar de inventar uno.
- **La aleatorización de MAC es un problema de medición, no un bug.** Si el teléfono del
  panelista rota su dirección, el hogar pierde continuidad. Los dos agentes de escritorio
  detectan una MAC aleatorizada y le dicen al panelista cómo apagarla para la red de casa; en
  móvil hay que hacer lo mismo, con las palabras del propio sistema.
- **El device id se escribe una vez y nunca se reemplaza.** Se obtiene del backend en el primer
  create y se manda para siempre. Lo único que puede borrarlo es que el backend conteste
  `The device doesn't exist!` a un update — esa respuesta es autoritativa, así que el registro se
  vuelve a crear en el mismo ciclo. No amplíen ese disparador: una credencial rechazada o una
  mutación ausente son respuestas que cambian sin que cambie el id.
- **Dos llaves de idempotencia, y no son intercambiables.** `requestId` se acuña por *escritura
  pretendida* (cambió el inventario o cambiaron los dueños); `reportId` se acuña por
  *observación* (una por dirección distinta). Reusar un `reportId` para una dirección nueva hace
  que el backend lo trate como reintento y devuelva el periodo original — la dirección entonces
  **deja de actualizarse en silencio**. Eso está medido, no supuesto.
- **Se reporta por cambio, no por reloj.** El ciclo habla sólo cuando cambió la huella de lo que
  se mandaría. Móvil tiene todavía menos razón para mandar por temporizador: el sistema te avisa
  cuando cambió la red.
- **Todo el texto que lee el panelista, en un solo lugar.** Los dos agentes de escritorio
  comparten un `AgentText`, y el puñado de frases que de verdad difieren por plataforma están
  *declaradas* en `PlatformWords` en vez de escritas dos veces. Dos copias de una frase siempre
  terminan siendo dos frases distintas, y entonces soporte está cuidando dos productos. Hagan lo
  mismo entre las dos apps móviles, y mantengan la redacción idéntica a la de escritorio siempre
  que el significado sea idéntico.

---

## 5. Orden sugerido

1. **Día uno, en paralelo con todo lo demás:** solicitar el entitlement de Network Extension de
   Apple y decidir si el panel es de instalación por el usuario o de equipos aprovisionados. Los
   dos tienen tiempos de espera largos y los dos bloquean la sección 3.
2. **Descubrimiento.** Es autocontenido, no pide entitlements, y terminarlo demuestra el enlace
   con el hogar de punta a punta. Sáquenlo detrás de una pantalla de depuración que muestre la
   MAC del nodo, el `get_config.sh` y la entrada propia de `list_clients_json.sh`.
3. **Registro y reporte** contra el stub, y luego contra stage. `tools/fake-graphql.py` en este
   repositorio responde las dos mutaciones de verdad —recuerda request ids y report ids y mantiene
   un periodo abierto por sujeto—, así que create / retry / update y primero / no-op / contiguo /
   reintento son todos ejercitables sin red. Úsenlo: sale más barato que descubrir las reglas de
   idempotencia contra producción.
4. **El túnel**, sólo metadatos.
5. **El certificado y la interceptación**, únicamente si el panel resultó ser de equipos
   aprovisionados.

---

## 6. Vectores de prueba

Respuestas conocidas, para que una segunda implementación pueda validarse sola sin un hogar.

**`machineId` a partir de una MAC**

| MAC | `machineId` |
|---|---|
| `3c:22:fb:9a:10:4e` | `M2MyMmZiOWExMDRl3cPMPM` |

Algoritmo: quita los dos puntos, saca el base64 del ASCII del resultado, tira el relleno `=`,
agrega los dos primeros caracteres de la MAC sin dos puntos, agrega `PMPM`. Es reversible.

**Direcciones que se deben aceptar**

`10.0.0.5` · `172.16.0.1` · `172.31.255.254` · `192.168.0.116` · `169.254.1.1`

**Direcciones que se deben rechazar**

`1.2.3` (tres octetos) · `192.168.01.1` (cero a la izquierda) · `::1` (dos puntos) · `8.8.8.8`
(pública) · `127.0.0.1` (loopback) · `172.32.0.1` (fuera de `172.16/12`) · `256.1.1.1`

**La CA**

SHA-256 `741FFDF0F0CBD5E75255B797F4208BCED3D1C27181DFC5FB2120F33AD231D94C`, subject
`C=US, ST=Proxy, L=MITM, O=Squid, OU=SSL-Bump, CN=MITM Proxy CA`, válida de 2025-05-13 a
2035-05-11. Rechacen el bundle si cualquiera de esas tres cosas no coincide, o si trae llave
privada.

**El nodo**

Un host es un nodo meter sólo si `GET /cgi-bin/router_info.sh` devuelve un JSON cuyo `wan_mac` o
`lan_mac` normaliza a 17 caracteres. Todo lo demás con el 22, el 80 o el 3129 abierto es la
impresora de alguien.
