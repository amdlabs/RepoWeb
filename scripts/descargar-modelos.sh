#!/usr/bin/env bash
# Descarga los modelos ONNX de reconocimiento facial en la carpeta Models de la aplicación.
#
# Los modelos son binarios grandes y no se versionan en el repositorio. Este script trae
# los dos modelos de rostros del OpenCV Model Zoo (Apache 2.0). Los de matrículas
# (detector YOLO + OCR) dependen del país y del proveedor: vea el README.
#
#   ./descargar-modelos.sh [carpeta-destino]

set -euo pipefail

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DESTINO="${1:-$RAIZ/src/IVZVision.Web/Models}"

mkdir -p "$DESTINO"
echo "Carpeta de destino: $DESTINO"

# nombre|url|tamaño esperado en bytes
# Los .onnx del OpenCV Zoo están en Git LFS: hay que bajarlos por media.githubusercontent.com.
MODELOS=(
  "face_detection_yunet_2023mar.onnx|https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx|232589"
  "face_recognition_sface_2021dec.onnx|https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx|38696353"
)

for entrada in "${MODELOS[@]}"; do
  IFS='|' read -r nombre url tam <<< "$entrada"
  ruta="$DESTINO/$nombre"

  if [[ -f "$ruta" && "$(stat -c%s "$ruta")" == "$tam" ]]; then
    echo "  [ya está] $nombre"
    continue
  fi

  echo -n "  [descargando] $nombre ..."
  curl -sSL -o "$ruta" "$url"
  real="$(stat -c%s "$ruta")"

  if [[ "$real" != "$tam" ]]; then
    echo ""
    echo "  AVISO: $nombre ocupa $real bytes y se esperaban $tam. Compruebe la descarga." >&2
  else
    echo " OK ($((real / 1024 / 1024)) MB)"
  fi
done

echo
echo "Modelos de rostros listos."
echo "Faltan los de matrículas (detector YOLO y OCR CTC): consulte el README."
echo "Después, en la web: Configuración -> Modelos -> Verificar modelos."
