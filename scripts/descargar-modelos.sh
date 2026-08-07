#!/usr/bin/env bash
# Descarga todos los modelos ONNX del sistema en la carpeta Models de la aplicación.
# Fuentes oficiales: OpenCV Model Zoo (rostros), release de Ultralytics YOLOv5 (objetos COCO),
# RapidOCR/PaddleOCR (OCR de matrículas en inglés y su diccionario).
set -euo pipefail

DESTINO="${1:-$(cd "$(dirname "$0")/.." && pwd)/src/IVZVision.Web/Models}"
mkdir -p "$DESTINO"
echo "Carpeta de destino: $DESTINO"

descargar() {
    local nombre="$1" url="$2" tam="$3"
    local ruta="$DESTINO/$nombre"

    if [[ -f "$ruta" && "$(stat -c%s "$ruta" 2>/dev/null || stat -f%z "$ruta")" == "$tam" ]]; then
        echo "  [ya está] $nombre"
        return
    fi

    echo "  [descargando] $nombre ..."
    curl -fsSL "$url" -o "$ruta"

    local real
    real="$(stat -c%s "$ruta" 2>/dev/null || stat -f%z "$ruta")"
    if [[ "$real" != "$tam" ]]; then
        echo "  AVISO: $nombre ocupa $real bytes y se esperaban $tam" >&2
    fi
}

descargar face_detection_yunet_2023mar.onnx \
    "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx" \
    232589

descargar face_recognition_sface_2021dec.onnx \
    "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx" \
    38696353

descargar yolov5s.onnx \
    "https://github.com/ultralytics/yolov5/releases/download/v7.0/yolov5s.onnx" \
    14698981

descargar plate_ocr_rec.onnx \
    "https://huggingface.co/SWHL/RapidOCR/resolve/main/PP-OCRv3/en_PP-OCRv3_rec_infer.onnx" \
    8967018

descargar plate_ocr_charset_en.txt \
    "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/ppocr/utils/en_dict.txt" \
    190

echo
echo "Modelos descargados."
echo "Opcional: añada un detector YOLO de matrículas (p. ej. license_plate_detector.onnx)."
