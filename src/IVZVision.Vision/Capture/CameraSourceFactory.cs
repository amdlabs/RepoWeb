using System.Runtime.InteropServices;
using IVZVision.Core.Configuration;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace IVZVision.Vision.Capture;

/// <summary>Abre la captura correspondiente al origen de la cámara: RTSP o dispositivo USB.</summary>
public static class CameraSourceFactory
{
    /// <summary>
    /// Backend de captura adecuado a cada sistema operativo. En Linux V4L2 es el único
    /// que expone las cámaras USB; en Windows DirectShow es el más compatible y en
    /// macOS hay que usar AVFoundation.
    /// </summary>
    public static VideoCaptureAPIs UsbBackend
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return VideoCaptureAPIs.DSHOW;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return VideoCaptureAPIs.AVFOUNDATION;
            return VideoCaptureAPIs.V4L2;
        }
    }

    public static VideoCapture Open(CameraConfig camera, ILogger logger)
    {
        if (camera.Source == CameraSource.Usb)
            return OpenUsb(camera, logger);

        var capture = new VideoCapture(camera.BuildRtspUrl(), VideoCaptureAPIs.FFMPEG);

        // Búfer mínimo: interesa el fotograma más reciente, no el histórico.
        capture.Set(VideoCaptureProperties.BufferSize, 1);
        return capture;
    }

    private static VideoCapture OpenUsb(CameraConfig camera, ILogger logger)
    {
        var backend = UsbBackend;

        // En Linux la ruta del dispositivo es más estable que el índice, que puede
        // cambiar de orden entre arranques.
        var capture = !string.IsNullOrWhiteSpace(camera.DevicePath) && backend == VideoCaptureAPIs.V4L2
            ? new VideoCapture(camera.DevicePath.Trim(), backend)
            : new VideoCapture(camera.DeviceIndex, backend);

        if (!capture.IsOpened()) return capture;

        if (camera.CaptureWidth > 0) capture.Set(VideoCaptureProperties.FrameWidth, camera.CaptureWidth);
        if (camera.CaptureHeight > 0) capture.Set(VideoCaptureProperties.FrameHeight, camera.CaptureHeight);
        if (camera.CaptureFps > 0) capture.Set(VideoCaptureProperties.Fps, camera.CaptureFps);
        capture.Set(VideoCaptureProperties.BufferSize, 1);

        logger.LogInformation("Cámara USB {Source} abierta con {Backend} a {Width}x{Height}",
            camera.DescribeSource(), backend,
            (int)capture.Get(VideoCaptureProperties.FrameWidth),
            (int)capture.Get(VideoCaptureProperties.FrameHeight));

        return capture;
    }

    /// <summary>Mensaje de error explicando qué revisar según el origen.</summary>
    public static string DescribeOpenFailure(CameraConfig camera)
    {
        if (camera.Source == CameraSource.Usb)
        {
            var hint = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "En Linux compruebe que el dispositivo existe y que el proceso tiene permiso (grupo «video»). " +
                  "Dentro de un contenedor hay que pasarlo con --device."
                : "Compruebe que ninguna otra aplicación esté usando la cámara y que el índice sea correcto.";

            return $"No se pudo abrir la cámara USB ({camera.DescribeSource()}). {hint}";
        }

        return $"No se pudo abrir el flujo RTSP {camera.BuildRtspUrl(maskCredentials: true)}. " +
               "Compruebe IP, puerto, usuario, contraseña y que el canal exista.";
    }
}
