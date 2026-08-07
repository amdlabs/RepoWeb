<#
.SYNOPSIS
    Descarga todos los modelos ONNX del sistema en la carpeta Models de la aplicación.

.DESCRIPTION
    Los modelos son binarios grandes y no se versionan en el repositorio. Este script
    trae, desde fuentes oficiales:

      - Rostros: YuNet (detector) y SFace (embeddings), del OpenCV Model Zoo (Apache 2.0).
      - Objetos: YOLOv5s entrenado en COCO (80 clases), release oficial de Ultralytics (AGPL-3.0).
      - OCR de matrículas: PP-OCRv3 inglés (conversión ONNX del proyecto RapidOCR) y su
        diccionario oficial de PaddleOCR.

    El detector de matrículas (YOLO específico de matrículas) depende del país y del
    proveedor: déjelo en la misma carpeta y selecciónelo en Configuración → Modelos.

.EXAMPLE
    .\descargar-modelos.ps1
    .\descargar-modelos.ps1 -Destino "C:\IVZVision\Models"
#>
[CmdletBinding()]
param(
    [string]$Destino = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Destino)) {
    $raiz = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Destino = Join-Path $raiz "..\src\IVZVision.Web\Models"
}

# Los .onnx del OpenCV Zoo están en Git LFS: hay que bajarlos por media.githubusercontent.com.
$modelos = @(
    @{
        Nombre = "face_detection_yunet_2023mar.onnx"
        Url    = "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx"
        Tam    = 232589
    },
    @{
        Nombre = "face_recognition_sface_2021dec.onnx"
        Url    = "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx"
        Tam    = 38696353
    },
    @{
        # Detector genérico de objetos (COCO, 80 clases) - release oficial de Ultralytics.
        Nombre = "yolov5s.onnx"
        Url    = "https://github.com/ultralytics/yolov5/releases/download/v7.0/yolov5s.onnx"
        Tam    = 14698981
    },
    @{
        # Reconocimiento de texto PP-OCRv3 (inglés), conversión ONNX del proyecto RapidOCR.
        Nombre = "plate_ocr_rec.onnx"
        Url    = "https://huggingface.co/SWHL/RapidOCR/resolve/main/PP-OCRv3/en_PP-OCRv3_rec_infer.onnx"
        Tam    = 8967018
    },
    @{
        # Diccionario oficial del modelo de OCR en inglés (PaddleOCR).
        Nombre = "plate_ocr_charset_en.txt"
        Url    = "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/ppocr/utils/en_dict.txt"
        Tam    = 190
    }
)

New-Item -ItemType Directory -Force -Path $Destino | Out-Null
$Destino = (Resolve-Path $Destino).Path
Write-Host "Carpeta de destino: $Destino" -ForegroundColor Cyan

foreach ($m in $modelos) {
    $ruta = Join-Path $Destino $m.Nombre

    if ((Test-Path $ruta) -and ((Get-Item $ruta).Length -eq $m.Tam)) {
        Write-Host "  [ya está] $($m.Nombre)" -ForegroundColor DarkGray
        continue
    }

    Write-Host "  [descargando] $($m.Nombre) ..." -NoNewline
    Invoke-WebRequest -Uri $m.Url -OutFile $ruta -UseBasicParsing
    $tamReal = (Get-Item $ruta).Length

    if ($tamReal -ne $m.Tam) {
        Write-Host ""
        Write-Warning "$($m.Nombre) ocupa $tamReal bytes y se esperaban $($m.Tam). Compruebe la descarga."
    } else {
        Write-Host " OK ($([math]::Round($tamReal / 1MB, 1)) MB)" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Modelos descargados." -ForegroundColor Green
Write-Host "Opcional: añada un detector YOLO de matrículas (p. ej. license_plate_detector.onnx)." -ForegroundColor Yellow
Write-Host "Después, en la web: Configuración -> Modelos -> Verificar modelos."
