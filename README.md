# IVZ Vision

Aplicación web en **ASP.NET Core 8** que se conecta a cámaras **Hikvision** (o cualquier
cámara ONVIF/RTSP compatible) y a **webcams USB** del propio equipo, y **reconoce en tiempo
real y en local** rostros, matrículas, objetos, códigos QR y de barras, texto y actividades
sospechosas — sin enviar ni una imagen a servicios externos.

Todo lo identificado se dibuja con su cuadrante sobre el vídeo, aparece en los paneles
laterales con su recorte y se contrasta contra una base de datos **SQL Server Express**.
Lo que no se reconoce no se pierde: va a una lista donde se le pone nombre y, a partir de
ese momento, el sistema lo identifica solo.

```
┌─────────────┐  RTSP   ┌────────────────────────────────────────────┐  MJPEG + SignalR  ┌───────────┐
│  Cámara IP  │────────►│  IVZVision.Web                             │──────────────────►│ Navegador │
│ (Hikvision) │◄ISAPI───│                                            │                   └───────────┘
└─────────────┘ (ANPR)  │  rostros    YuNet + SFace                  │  REST /api/ver    ┌───────────┐
┌─────────────┐  V4L2/  │  matrículas YOLO + CRNN/CTC                │──────────────────►│  Scripts  │
│ Webcam USB  │  DShow  │  objetos    YOLO COCO + seguimiento        │                   └───────────┘
│  del equipo │────────►│  códigos    ZXing (QR y barras)            │  MCP /mcp         ┌───────────┐
└─────────────┘         │  texto      DB + CRNN/CTC                  │──────────────────►│ Otras IA  │
                        │  actividad  reglas sobre el seguimiento    │                   └───────────┘
                        └───────────────────┬────────────────────────┘
                                            │ Entity Framework Core
                                      ┌─────▼──────┐
                                      │ SQL Express│  personas · rostros · vehículos
                                      └────────────┘  objetos · eventos · pendientes
```

---

## Qué hace

### Reconocimiento
- **Rostros**: detección con YuNet, alineación por los cinco puntos faciales y comparación
  por similitud coseno contra las plantillas de la base de datos.
- **Matrículas**: detector YOLO + OCR CRNN con decodificación CTC. Con el **formato del
  país** configurado (Uruguay: 3 letras y 4 números) se descartan las lecturas imposibles y
  se corrigen las confusiones típicas del OCR — `5AB1234` se convierte en `SAB1234`.
  Una matrícula sólo se da por buena tras varias lecturas coincidentes.
- **Objetos**: detector YOLO multiclase (COCO) para personas, animales, vehículos, mochilas…
- **Códigos QR y de barras**: QR, DataMatrix, Aztec, PDF417, EAN, UPC, Code 128/39/93, ITF
  y Codabar. No necesita ningún modelo: funciona siempre.
- **Texto y escritura**: detector DB/DBNet + reconocedor CRNN/CTC, con enderezado de las
  líneas inclinadas.
- **ANPR de la propia cámara** (opcional): escucha el `alertStream` ISAPI de Hikvision,
  combinable con el OCR local.

### Actividad sospechosa
Sobre las personas y los animales detectados se hace un seguimiento entre fotogramas y se
aplican reglas **explícitas y auditables** — cada alerta dice qué condición se cumplió y con
qué valores, en vez de salir de un modelo opaco:

| Regla | Cuándo salta |
|---|---|
| Merodeo | una persona permanece más de N segundos |
| Intrusión | una persona o un animal entra en la zona restringida de la cámara |
| Aglomeración | más de N personas a la vez |
| Carrera | el objeto se desplaza más de una fracción del fotograma por segundo |
| Fuera de horario | presencia fuera de la franja permitida |
| Animal | se detecta un animal |
| Rostro no visible | se ve a alguien varios segundos sin detectarle la cara |

### Aprendizaje
Todo rostro, matrícula u objeto que el sistema **no reconoce** va a la pantalla
**«Sin identificar»**, agrupado por sujeto: los rostros por parecido, las matrículas por
texto y los objetos por clase o apariencia. Al ponerle nombre, el vector de características
que ya se calculó en el momento de la detección **se convierte en plantilla de
reconocimiento**. No hay que reprocesar la imagen ni reentrenar nada: en la siguiente
recarga del índice, ese sujeto ya se identifica.

### Consulta e integración
- **Buscador por lenguaje natural**: «personas desconocidas de anoche en la entrada»,
  «matrículas de las últimas 2 horas», «animales de esta semana». El analizador es
  determinista y funciona sin conexión; muestra siempre cómo interpretó la consulta.
- **API REST `/api/ver`**: devuelve en JSON lo que las cámaras están viendo en ese instante.
- **Servidor MCP en `/mcp`**: expone siete herramientas para que otros asistentes de IA
  consulten y actúen sobre el sistema de visión.
- **Padrón** de personas, vehículos y objetos, e **histórico** filtrable con el recorte de
  cada detección.
- **Configuración completa desde la web**, con pruebas de conexión reales para cada sistema.

---

## Requisitos

| | |
|---|---|
| **.NET** | SDK 8.0 (ejecución: ASP.NET Core Runtime 8.0) |
| **Base de datos** | SQL Server Express 2017 o posterior (vale cualquier edición) |
| **Sistema** | Windows Server / Windows 10-11 (recomendado). Linux, ver [nota](#nota-sobre-linux) |
| **Hardware** | 4 núcleos y 8 GB para 1-2 cámaras en CPU. GPU opcional (CUDA / DirectML) |
| **Cámaras** | Hikvision o compatible con RTSP, y/o webcams USB del propio equipo |

---

## Puesta en marcha

### 1. Compilar

```bash
git clone <este-repositorio>
cd RepoWeb
dotnet build IVZVision.sln -c Release
```

### 2. Descargar los modelos

Los modelos ONNX no se versionan (son binarios grandes). Los de rostros se bajan solos:

```powershell
# Windows
.\scripts\descargar-modelos.ps1
```
```bash
# Linux / macOS
./scripts/descargar-modelos.sh
```

Esto deja en `src/IVZVision.Web/Models/`:

| Fichero | Qué es | Origen |
|---|---|---|
| `face_detection_yunet_2023mar.onnx` | Detector de rostros (233 KB) | [OpenCV Model Zoo](https://github.com/opencv/opencv_zoo), Apache 2.0 |
| `face_recognition_sface_2021dec.onnx` | Embeddings faciales, 128-D (37 MB) | [OpenCV Model Zoo](https://github.com/opencv/opencv_zoo), Apache 2.0 |
| `plate_ocr_charset.txt` | Diccionario del OCR de matrículas (0-9, A-Z) | incluido en el repositorio |
| `coco.names` | Las 80 clases del detector de objetos | incluido en el repositorio |

**El resto de modelos los aporta usted**, porque el acierto depende del país, del caso de uso
y de la licencia que le convenga:

| Función | Fichero por defecto | Qué poner |
|---|---|---|
| Matrículas · detector | `license_plate_detector.onnx` | Un **YOLOv5/v7/v8/v11** entrenado en matrículas, exportado a ONNX |
| Matrículas · OCR | `plate_ocr_rec.onnx` | Un reconocedor **CRNN con salida CTC** (sirve el `rec` de PP-OCR) |
| Objetos | `yolov8n.onnx` | Un **YOLO entrenado en COCO**; sin él no hay detección de objetos ni análisis de actividad |
| Objetos · apariencia | *(vacío, opcional)* | Un codificador de imagen (CLIP, MobileNet…) para reconocer objetos concretos por su aspecto |
| Texto · detector | `text_det.onnx` | Un detector **DB/DBNet** (el `det` de PP-OCR) |
| Texto · reconocedor | `text_rec.onnx` + `text_charset.txt` | Un **CRNN/CTC** con su diccionario |

Exportar un YOLO desde Ultralytics: `yolo export model=tu_modelo.pt format=onnx opset=12`.
La aplicación detecta sola el formato de salida (`[1, N, 5+clases]` con objectness o
`[1, 4+clases, N]` sin ella) y el tamaño de entrada que declare el modelo.

**Nada de esto bloquea el arranque.** Cada bloque es independiente: si falta el de
matrículas, los rostros y los objetos siguen funcionando; los códigos QR y de barras no
necesitan ningún modelo y están siempre disponibles. La pantalla de configuración indica
exactamente qué falta y **Verificar modelos** intenta abrirlos de verdad antes de guardar.

### 3. Arrancar

```bash
dotnet run --project src/IVZVision.Web
```

Abra `http://localhost:5000` (o el puerto que indique la consola).

### 4. Configurar

Todo se hace desde **Configuración**, sin tocar ficheros:

1. **Sistemas → Base de datos.** Servidor (`.\SQLEXPRESS`), nombre de la base y
   autenticación. Pulse **Probar conexión**. Con «Crear la base y las tablas si no existen»
   marcado, el esquema se crea solo al guardar.
2. **Sistemas → Modelos.** Rutas de los `.onnx` y proveedor de ejecución (CPU / CUDA / DirectML).
   Pulse **Verificar modelos**: intenta abrirlos de verdad y dice qué falla.
3. **Sistemas → Umbrales.** Elija el **formato de matrícula** de su país (Uruguay viene
   por defecto: 3 letras y 4 números).
4. **Sistemas → Actividad sospechosa.** Active las reglas que le interesen y ajuste sus
   umbrales.
5. **Sistemas → API y MCP.** Genere un token si va a consumir `/api/ver` o `/mcp`.
   El valor completo sólo se muestra una vez, al crearlo.
6. **Cámaras → Añadir cámara.** Elija el **origen**:
   - **Ip**: dirección, puertos, usuario, contraseña, canal y perfil.
   - **Usb**: pulse **Buscar cámaras USB** y elija de la lista, o indique el índice.

   **Probar vídeo** abre el origen y devuelve un fotograma real; **Probar ISAPI** comprueba
   las credenciales HTTP de las cámaras IP. Marque qué debe reconocer cada cámara y, si
   quiere alertas de intrusión, defina su **zona restringida**.
7. **Personas** y **Vehículos.** Dé de alta a las personas y suba una o varias fotos de
   cada una; registre las matrículas conocidas.
8. **Directo.** Elija la cámara en el desplegable y verá el vídeo con los cuadrantes y los
   paneles de alertas, rostros, matrículas y demás objetos en tiempo real.
9. **Sin identificar.** A medida que el sistema vea sujetos que no conoce, aparecerán aquí
   para ponerles nombre.

Al guardar, los modelos se recargan y las cámaras se reinician sin parar la aplicación.

---

## Docker

La imagen se basa en Ubuntu 22.04 porque los binarios nativos de OpenCvSharp están
compilados contra sus bibliotecas; todas las dependencias necesarias se instalan en el
`Dockerfile`.

```bash
# Windows, macOS y Linux — cámaras IP
docker compose up -d --build
# → http://localhost:8080
```

```bash
# Linux — además, webcams USB del equipo
docker compose -f docker-compose.yml -f docker-compose.linux.yml up -d --build
```

El compose levanta también un **SQL Server Express** en contenedor. Si ya tiene una
instancia propia, borre ese servicio e indique su servidor en la pantalla de configuración.

**Redes y dispositivos, con franqueza:**

| | Linux | macOS | Windows |
|---|---|---|---|
| **Cámaras IP de la red local** | sí | sí | sí |
| **Webcams USB del equipo** | sí, con `--device` | **no** | **no** |

Las cámaras IP funcionan en los tres sistemas sin nada especial: el contenedor alcanza la
red local a través del NAT de Docker.

Las webcams USB son otra historia. En macOS y Windows, Docker Desktop ejecuta los
contenedores dentro de una máquina virtual Linux que **no tiene acceso al USB del equipo**;
no es una limitación de esta aplicación, sino de Docker en esas plataformas. Sus opciones ahí:

- Ejecutar la aplicación **de forma nativa** con `dotnet run` (la webcam funciona sin más).
- O publicar la webcam en la red desde el equipo anfitrión (por ejemplo con OBS o
  `ffmpeg` emitiendo RTSP) y darla de alta como cámara **Generic** con su URL.

Para ver qué dispositivos tiene en Linux: `ls -l /dev/video*`, y ajuste la lista de
`devices` en `docker-compose.linux.yml`.

---

## API REST

Todas las rutas exigen un token creado en **Configuración → API y MCP**. Se admite la
cabecera `X-API-Token`, `Authorization: Bearer …` o el parámetro `?token=`.

```bash
TOKEN=ivz_...

# Qué están viendo TODAS las cámaras ahora mismo
curl -H "X-API-Token: $TOKEN" http://localhost:8080/api/ver

# Sólo una cámara, sólo matrículas, con el fotograma anotado incluido
curl -H "X-API-Token: $TOKEN" \
  "http://localhost:8080/api/ver?camara=<id>&tipo=matricula&imagen=true"

# Búsqueda en lenguaje natural sobre el histórico y los pendientes
curl -H "X-API-Token: $TOKEN" \
  "http://localhost:8080/api/buscar?prompt=personas%20desconocidas%20de%20anoche"
```

| Ruta | Qué devuelve |
|---|---|
| `GET /api/ver` | Lo que ve cada cámara en este instante: objetos y alertas |
| `GET /api/camaras` | Cámaras y su estado de conexión |
| `GET /api/buscar?prompt=…` | Búsqueda en lenguaje natural, con la interpretación aplicada |
| `GET /api/pendientes` | Sujetos sin identificar a la espera de un nombre |
| `GET /api/instantanea/{id}` | Último fotograma anotado en JPEG |

Respuesta de `/api/ver` (abreviada):

```json
{
  "instante": "2026-08-07T15:30:00-03:00",
  "camaras": 1,
  "detalle": [{
    "nombre": "Entrada Principal",
    "origen": "rtsp://***:***@192.168.1.64:554/Streaming/Channels/102",
    "conectada": true,
    "viendo": [
      { "tipo": "rostro", "etiqueta": "Ana Pérez", "conocido": true, "autorizado": true,
        "similitud": 0.71, "cuadro": { "x": 412, "y": 180, "ancho": 96, "alto": 96 } },
      { "tipo": "matricula", "etiqueta": "SAB1234", "matricula": "SAB1234",
        "conocido": false, "similitud": 0.88, "cuadro": { "x": 640, "y": 520, "ancho": 180, "alto": 60 } }
    ],
    "alertas": [
      { "tipo": "alerta", "alerta": "Loitering", "gravedad": "Warning",
        "motivo": "Persona en la escena desde hace 52 s (umbral 45 s)." }
    ]
  }],
  "motor": { "rostros": true, "matriculas": true, "objetos": true, "codigos": true, "texto": false }
}
```

---

## Servidor MCP

En `/mcp` (transporte HTTP con streaming) para que otros asistentes de IA usen el sistema
de visión. El reparto de trabajo es deliberado: aquí se ofrecen operaciones **estructuradas
y deterministas**, y es el asistente que llama quien pone la comprensión del lenguaje. Así
el reconocimiento no depende de ningún modelo de lenguaje para funcionar.

| Herramienta | Qué hace |
|---|---|
| `listar_camaras` | Cámaras, estado y cuántos objetos y alertas tienen ahora |
| `ver_objetos` | Lo que se ve en este instante, filtrable por cámara y tipo |
| `capturar_imagen` | Último fotograma anotado en base64 |
| `buscar` | Búsqueda en lenguaje natural sobre histórico y pendientes |
| `listar_desconocidos` | Sujetos que esperan un nombre |
| `nombrar_desconocido` | Le pone nombre: a partir de ahí el sistema lo reconoce |
| `consultar_registro` | Padrón de personas, vehículos y objetos |

Configuración en un cliente MCP:

```json
{
  "mcpServers": {
    "ivzvision": {
      "type": "http",
      "url": "http://localhost:8080/mcp",
      "headers": { "Authorization": "Bearer ivz_..." }
    }
  }
}
```

---

## Cómo funciona por dentro

### Proyectos

| Proyecto | Responsabilidad |
|---|---|
| `IVZVision.Core` | Configuración, tipos de dominio y utilidades (sin dependencias pesadas) |
| `IVZVision.Data` | Entity Framework Core, índice en memoria, cola de aprendizaje y buscador |
| `IVZVision.Vision` | Captura RTSP y USB, inferencia ONNX, seguimiento, reglas de actividad e ISAPI |
| `IVZVision.Web` | Razor Pages, SignalR, MJPEG, API REST y servidor MCP |
| `IVZVision.Tests` | Pruebas de la geometría, los formatos de matrícula, el seguimiento y el buscador |

### Recorrido de un fotograma

1. `CameraWorker` lee del origen en un hilo propio (`VideoCapture` de OpenCV es bloqueante).
   RTSP va por FFmpeg; USB por V4L2 en Linux, DirectShow en Windows y AVFoundation en macOS.
2. Se recorta la región de interés y se reduce al ancho de análisis configurado.
3. **Rostros** — YuNet localiza cara y cinco puntos; se alinea a 112×112 con una
   transformación de semejanza; SFace produce 128 dimensiones; se compara por coseno
   contra el índice en memoria.
4. **Matrículas** — YOLO localiza la placa; el CRNN + CTC lee el texto; se normaliza, se
   corrige según el formato del país y se busca en el padrón.
5. **Objetos** — YOLO multiclase; si hay extractor de características, además se compara
   con los objetos ya nombrados.
6. **Códigos** — ZXing sobre el fotograma en escala de grises.
7. **Texto** — DB localiza las líneas, se enderezan y el CRNN + CTC las lee.
8. **Actividad** — los objetos se asocian entre fotogramas por solapamiento y el analizador
   aplica las reglas sobre esos seguimientos.
9. Se dibujan los cuadrantes y la zona restringida, se codifica en JPEG y se publica a los
   clientes MJPEG.
10. Los reconocimientos válidos se guardan en SQL, los desconocidos van a la cola de
    aprendizaje y todo se envía por SignalR al navegador.

### Decisiones que conviene conocer

- **La identidad no se resuelve contra SQL en cada fotograma.** `KnownSubjectsIndex`
  mantiene los embeddings y las matrículas en memoria y se recarga cada 60 segundos
  o inmediatamente tras cualquier alta o edición.
- **Tiempo de guarda por sujeto.** Una persona parada delante del objetivo genera un
  evento, no cientos. Configurable en segundos por cámara y sujeto.
- **Confirmación de matrículas.** Un reflejo puede producir una lectura plausible pero
  falsa; por eso se exigen N lecturas idénticas dentro de una ventana temporal.
- **Fps de análisis y fps de vídeo son independientes.** Puede emitir 12 fps al navegador
  analizando sólo 6, que es donde está el coste de CPU.
- **Degradación controlada.** Si faltan los modelos de matrículas, los rostros siguen
  funcionando; si SQL no responde, la web arranca igual para poder corregir los datos.
- **Las credenciales nunca se muestran.** Las URLs RTSP aparecen enmascaradas en la
  interfaz, en el registro y en la API.
- **El aprendizaje no reentrena nada.** Guardar el vector de características en el momento
  de la detección permite que ponerle nombre a un desconocido sea instantáneo y exacto.
- **Las reglas de actividad son reglas, no un modelo.** Quien revisa una alerta necesita
  saber por qué saltó; por eso cada una lleva su condición y sus valores.
- **El buscador no depende de un modelo de lenguaje.** Es un analizador por palabras clave,
  así que funciona sin conexión y siempre muestra cómo interpretó la consulta. Para
  comprensión más fina, un asistente externo puede llamar a las herramientas MCP.

### Dónde se guarda la configuración

En `src/IVZVision.Web/App_Data/ivzvision.settings.json`, escrito de forma atómica.
**Contiene las contraseñas de las cámaras y de SQL en claro**, así que está excluido del
control de versiones; protéjalo con permisos NTFS y no lo copie a repositorios.

---

## Esquema de la base de datos

| Tabla | Contenido |
|---|---|
| `Persons` | Personas conocidas, con marca de autorizada y de activa |
| `FaceTemplates` | Embeddings faciales (`varbinary`), varios por persona |
| `Vehicles` | Matrículas normalizadas (índice único), titular y autorización |
| `KnownObjects` / `ObjectTemplates` | Objetos con nombre y sus vectores de apariencia |
| `RecognitionEvents` | Histórico: cámara, tipo, momento, identidad, confianzas, alerta y recorte |
| `PendingSubjects` | Sujetos sin identificar, agrupados, con su vector listo para aprender |

Las tablas se crean solas al arrancar. La purga del histórico se ejecuta a diario según
los días de retención configurados y borra también los recortes en disco.

---

## Despliegue en IIS

```powershell
dotnet publish src/IVZVision.Web -c Release -o C:\inetpub\IVZVision
```

1. Instale el **ASP.NET Core Hosting Bundle 8.0**.
2. Cree el sitio apuntando a esa carpeta.
3. En el grupo de aplicaciones, active **«Cargar perfil de usuario»** y ponga
   **«Tiempo de espera de inactividad» a 0**: el pipeline de cámaras tiene que seguir
   vivo aunque nadie esté mirando la web.
4. Dé permiso de escritura a la identidad del grupo de aplicaciones sobre `App_Data`.
5. Si usa autenticación de Windows contra SQL, configure la identidad del grupo de
   aplicaciones con una cuenta con acceso a la instancia.

### Nota sobre Linux

El paquete nativo de OpenCvSharp está compilado contra las bibliotecas de Ubuntu 20.04
(`libavcodec58`, `libtiff5`, `libtesseract4`, GTK 2…), que ya no existen en Ubuntu 24.04.
En Windows los binarios nativos vienen completos en el paquete NuGet y no hay que
instalar nada. Para desplegar en Linux, use una imagen base Ubuntu 20.04/22.04 e instale
esas dependencias, o compile `OpenCvSharpExtern` contra el OpenCV de su distribución.

---

## Pruebas

```bash
dotnet test tests/IVZVision.Tests/IVZVision.Tests.csproj
```

107 pruebas sobre lo que es fácil romper sin darse cuenta:

- el mapeo de coordenadas del letterbox de vuelta al fotograma y la supresión de no-máximos;
- la similitud coseno y la serialización de los vectores de características;
- los formatos de matrícula por país y la corrección de confusiones del OCR;
- el formato de las URLs RTSP de cada fabricante (Hikvision codifica canal y perfil como
  `canal*100 + perfil`: 101, 102, 201…);
- la asociación del seguimiento entre fotogramas y cada regla de actividad;
- la interpretación de las consultas en lenguaje natural.

---

## Licencias de terceros

| Componente | Licencia |
|---|---|
| OpenCvSharp4 | Apache 2.0 |
| ONNX Runtime | MIT |
| ZXing.Net | Apache 2.0 |
| ModelContextProtocol (SDK de MCP) | MIT |
| Entity Framework Core / ASP.NET Core | MIT |
| YuNet y SFace (OpenCV Model Zoo) | Apache 2.0 |

Los modelos que añada (matrículas, objetos, texto) tienen la licencia de su autor:
revísela antes de usarlos en producción. En particular, los pesos de YOLOv5 y YOLOv8 de
Ultralytics son **AGPL-3.0**, lo que tiene consecuencias si va a distribuir el sistema.

## Aviso legal

El reconocimiento facial, la lectura de matrículas y el análisis de comportamiento tratan
datos personales, y los rostros son además datos biométricos. En la Unión Europea esto
entra de lleno en el RGPD (artículo 9 para la biometría) y el Reglamento de IA restringe
la categorización biométrica y la vigilancia masiva. En Uruguay aplica la Ley 18.331 de
protección de datos personales, con registro de la base ante la URCDP.

Antes de poner el sistema en producción asegúrese de tener base legal, señalización visible,
información a los afectados, un plazo de conservación proporcionado y, cuando proceda, una
evaluación de impacto. Las alertas de actividad son ayudas a la decisión, no pruebas:
revíselas siempre antes de actuar sobre una persona.
