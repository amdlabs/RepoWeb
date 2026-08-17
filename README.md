# IVZ Vision

Aplicación web en **ASP.NET Core 8** que se conecta a cámaras **Hikvision** (o cualquier
cámara ONVIF/RTSP compatible), obtiene el vídeo en directo y **reconoce rostros y matrículas
en tiempo real, íntegramente en local** — sin enviar ni una imagen a servicios externos.

Cada objeto identificado se dibuja con su cuadrante sobre el vídeo, aparece en un panel
lateral con su recorte y se contrasta contra una base de datos **SQL Server Express** para
saber si ya se conoce a esa persona o a ese vehículo.

```
┌─────────────┐   RTSP    ┌──────────────────────────────────────┐   MJPEG + SignalR   ┌───────────┐
│  Cámara IP  │──────────►│  IVZVision.Web                       │────────────────────►│ Navegador │
│ (Hikvision) │           │   captura → detección → identidad    │                     └───────────┘
│             │◄─ ISAPI ──│   YuNet + SFace  ·  YOLO + CRNN/CTC  │
└─────────────┘  (ANPR)   └──────────────┬───────────────────────┘
                                         │ Entity Framework Core
                                   ┌─────▼──────┐
                                   │ SQL Express│  personas · rostros · vehículos · eventos
                                   └────────────┘
```

---

## Qué hace

- **Vídeo en directo** con los cuadrantes dibujados sobre cada rostro y cada matrícula.
  Verde = identificado y autorizado · ámbar = identificado sin autorización · rojo = desconocido.
- **Reconocimiento facial local**: detección con YuNet, alineación por los cinco puntos
  faciales y comparación por similitud coseno contra las plantillas de la base de datos.
- **Lectura de matrículas local**: detector YOLO + OCR CRNN con decodificación CTC.
  Una matrícula sólo se da por buena tras varias lecturas coincidentes.
- **ANPR de la propia cámara** (opcional): escucha el `alertStream` ISAPI de Hikvision
  y registra las matrículas que reconoce el hardware, combinable con el OCR local.
- **Padrón de personas y vehículos**: alta de personas con una o varias fotos y de
  matrículas con su titular, con marca de autorizado / no autorizado.
- **Histórico de eventos** filtrable por tipo, cámara, estado, texto y fechas, con el
  recorte de cada detección.
- **Detección genérica de objetos** (personas, vehículos, animales y las 80 clases COCO)
  con un YOLO local. Las clases sin etiquetar se listan como *desconocidas*; al ponerles
  nombre en la pantalla **Objetos** pasan a *conocidas* y las siguientes detecciones salen
  identificadas.
- **Cámaras USB / locales** además de las IP: webcams y capturadoras, eligiendo el índice
  del dispositivo.
- **Inicio de sesión obligatorio** (cookies) con usuarios del sistema en su propia tabla
  (hash PBKDF2, roles administrador / operador / consulta). Usuario inicial `admin`/`admin`;
  la API JSON queda anónima para integraciones.
- **API JSON** para integraciones: `GET /api/camaras` (lista de cámaras y su estado) y
  `GET /api/camaras/{id}/detecciones?take=50&incluirImagen=true` (últimos objetos, rostros
  y matrículas con su recorte en base64).
- **Configuración completa desde la web**: cámaras, base de datos, modelos y umbrales,
  con botones de prueba de conexión para cada sistema. Los modelos disponibles en la
  carpeta `Models` se eligen desde listas desplegables. Las contraseñas se guardan
  **cifradas** (DPAPI) y cada guardado deja una copia histórica en la base de datos.

---

## Requisitos

| | |
|---|---|
| **.NET** | SDK 8.0 (ejecución: ASP.NET Core Runtime 8.0) |
| **Base de datos** | SQL Server Express 2017 o posterior (vale cualquier edición) |
| **Sistema** | Windows Server / Windows 10-11 (recomendado). Linux, ver [nota](#nota-sobre-linux) |
| **Hardware** | 4 núcleos y 8 GB para 1-2 cámaras en CPU. GPU opcional (CUDA / DirectML) |
| **Cámara** | Hikvision o compatible, con RTSP habilitado |

---

## Puesta en marcha con Docker (recomendada)

Con Docker instalado, un solo comando levanta el sistema completo (SQL Server + aplicación,
con los modelos ya descargados dentro de la imagen y los datos en volúmenes persistentes):

```bash
docker compose up -d
```

Abra `http://localhost:8080` (usuario inicial `admin` / `admin`). La aplicación genera su
configuración inicial apuntando al SQL Server del propio compose; después todo se edita
desde la web. La contraseña de SQL se puede cambiar exportando `IVZVISION_DB_PASSWORD`
antes de levantar.

Para usar una cámara USB del anfitrión (solo Linux), descomente el bloque `devices:`
del servicio `web` en `docker-compose.yml`.

---

## Puesta en marcha manual

### 1. Compilar

```bash
git clone <este-repositorio>
cd RepoWeb
dotnet build IVZVision.sln -c Release
```

### 2. Descargar los modelos

Los modelos ONNX no se versionan (son binarios grandes). El script los baja todos de sus
fuentes oficiales:

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
| `yolov5s.onnx` | Detector de objetos COCO, 80 clases (14 MB) | [Ultralytics YOLOv5 v7.0](https://github.com/ultralytics/yolov5/releases/tag/v7.0), AGPL-3.0 |
| `plate_ocr_rec.onnx` | OCR PP-OCRv3 inglés (9 MB) | [RapidOCR](https://huggingface.co/SWHL/RapidOCR) (conversión de PaddleOCR), Apache 2.0 |
| `plate_ocr_charset_en.txt` | Diccionario del OCR PP-OCR | [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR), Apache 2.0 |
| `object_labels.txt` | Las 80 clases COCO en español | incluido en el repositorio |
| `plate_ocr_charset.txt` | Diccionario alfanumérico (0-9, A-Z) para modelos ANPR propios | incluido en el repositorio |

Todos los ficheros de esa carpeta aparecen en **listas desplegables** en
Configuración → Modelos, de modo que se puede elegir qué motor usar para cada tarea.

**Opcional — detector de matrículas**: para leer matrículas con el OCR local conviene añadir
un modelo **YOLOv5/v7/v8/v11** entrenado en matrículas y exportado a ONNX
(`license_plate_detector.onnx`), porque el acierto depende del formato de cada país.
La aplicación detecta sola el formato de salida. Exportar desde Ultralytics:
`yolo export model=tu_modelo.pt format=onnx opset=12`.

Sin ese fichero la aplicación arranca igual: rostros y objetos funcionan, y la pantalla
de configuración indica exactamente qué falta.

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
3. **Cámaras → Añadir cámara.** IP, puertos, usuario, contraseña, canal y perfil.
   **Probar vídeo RTSP** abre el flujo y devuelve un fotograma real; **Probar ISAPI**
   comprueba las credenciales HTTP.
4. **Personas** y **Vehículos.** Dé de alta a las personas y suba una o varias fotos de
   cada una; registre las matrículas conocidas.
5. **Directo.** Vídeo con cuadrantes y paneles de rostros y matrículas en tiempo real.

Al guardar, los modelos se recargan y las cámaras se reinician sin parar la aplicación.

---

## Cómo funciona por dentro

### Proyectos

| Proyecto | Responsabilidad |
|---|---|
| `IVZVision.Core` | Configuración, tipos de dominio y utilidades (sin dependencias pesadas) |
| `IVZVision.Data` | Entity Framework Core, entidades, índice en memoria y registro de eventos |
| `IVZVision.Vision` | Captura RTSP, inferencia ONNX, pipeline por cámara y cliente ISAPI |
| `IVZVision.Web` | Razor Pages, SignalR, streaming MJPEG y pantallas de configuración |
| `IVZVision.Tests` | Pruebas de la geometría, los umbrales y la construcción de URLs |

### Recorrido de un fotograma

1. `CameraWorker` lee del RTSP en un hilo propio (`VideoCapture` de OpenCV es bloqueante).
2. Se recorta la región de interés y se reduce al ancho de análisis configurado.
3. **Rostros** — YuNet localiza cara y cinco puntos; se alinea a 112×112 con una
   transformación de semejanza; SFace produce 128 dimensiones; se compara por coseno
   contra el índice en memoria.
4. **Matrículas** — YOLO localiza la placa; se recorta con margen; el CRNN + CTC lee el
   texto; se normaliza (mayúsculas, sin separadores) y se busca en el padrón.
5. Se dibujan los cuadrantes, se codifica en JPEG y se publica a los clientes MJPEG.
6. Los reconocimientos válidos se guardan en SQL y se envían por SignalR al navegador.

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
  interfaz y en el registro.

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
| `RecognitionEvents` | Histórico: cámara, tipo, momento, identidad, confianzas y recorte |

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

Cubren lo que es fácil romper sin darse cuenta: el mapeo de coordenadas del letterbox
de vuelta al fotograma, la supresión de no-máximos, la similitud coseno, la
normalización de matrículas y el formato de las URLs RTSP de cada fabricante
(Hikvision codifica canal y perfil como `canal*100 + perfil`: 101, 102, 201…).

---

## Licencias de terceros

| Componente | Licencia |
|---|---|
| OpenCvSharp4 | Apache 2.0 |
| ONNX Runtime | MIT |
| Entity Framework Core / ASP.NET Core | MIT |
| YuNet y SFace (OpenCV Model Zoo) | Apache 2.0 |

Los modelos de matrículas que añada tienen la licencia de su autor: revísela antes de
usarlos en producción.

## Aviso legal

El reconocimiento facial y la lectura de matrículas tratan datos personales y datos
biométricos. En la Unión Europea esto entra de lleno en el RGPD (y, para la biometría,
en su artículo 9). Antes de poner el sistema en producción asegúrese de tener base
legal, señalización, información a los afectados, un plazo de conservación
proporcionado y, cuando proceda, una evaluación de impacto.
