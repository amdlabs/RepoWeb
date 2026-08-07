<#
.SYNOPSIS
    Descarga los modelos ONNX de reconocimiento facial en la carpeta Models de la aplicación.

.DESCRIPTION
    Los modelos son binarios grandes y no se versionan en el repositorio.
    Este script trae los dos modelos de rostros del OpenCV Model Zoo (licencia Apache 2.0),
    que son los únicos con descarga directa y estable.

    Los modelos de matrículas (detector YOLO + OCR) dependen del país y del proveedor
    que elija: consulte el README para las opciones recomendadas y déjelos en la
    misma carpeta con los nombres que indique en Configuración → Modelos.

.EXAMPLE
    .\descargar-modelos.ps1
    .\descargar-modelos.ps1 -Destino "C:\IVZVision\Models"
#>
[CmdletBinding()]
param(
    [string]$Destino = (Join-Path $PSScriptRoot "..\src\IVZVision.Web\Models")
)

$ErrorActionPreference = "Stop"

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
Write-Host "Modelos de rostros listos." -ForegroundColor Green
Write-Host "Faltan los de matrículas (detector YOLO y OCR CTC): consulte el README." -ForegroundColor Yellow
Write-Host "Después, en la web: Configuración -> Modelos -> Verificar modelos."
