# ============================================================================
# IVZ Vision - imagen autocontenida (aplicación + modelos ONNX ya descargados)
# Junto con docker-compose.yml levanta el sistema completo (web + SQL Server)
# con un solo "docker compose up".
# ============================================================================

# ---- Compilación -----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Primero el grafo de proyectos, para cachear el restore.
# Directory.Build.props define el TargetFramework de todos los proyectos.
COPY IVZVision.sln Directory.Build.props ./
COPY src/IVZVision.Core/IVZVision.Core.csproj src/IVZVision.Core/
COPY src/IVZVision.Data/IVZVision.Data.csproj src/IVZVision.Data/
COPY src/IVZVision.Vision/IVZVision.Vision.csproj src/IVZVision.Vision/
COPY src/IVZVision.Web/IVZVision.Web.csproj src/IVZVision.Web/
RUN dotnet restore src/IVZVision.Web/IVZVision.Web.csproj

COPY . .

# Los modelos ONNX se descargan de sus fuentes oficiales durante la build,
# de forma que la imagen final funciona sin pasos manuales.
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && bash scripts/descargar-modelos.sh /src/src/IVZVision.Web/Models

RUN dotnet publish src/IVZVision.Web/IVZVision.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- Imagen final ----------------------------------------------------------
# Ubuntu 22.04 (jammy): libOpenCvSharpExtern.so está enlazada contra las
# versiones de librería de esta distribución (ffmpeg 4.4, libjpeg8, gtk2…).
FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy AS final
WORKDIR /app

# Dependencias nativas de OpenCvSharp (runtime oficial linux-x64): vídeo (ffmpeg),
# imagen (jpeg/png/tiff/openjp2/openexr), captura (dc1394) y GUI headless (gtk2).
RUN apt-get update && apt-get install -y --no-install-recommends \
        libavcodec58 \
        libavformat58 \
        libavutil56 \
        libswscale5 \
        libjpeg-turbo8 \
        libpng16-16 \
        libtiff5 \
        libopenjp2-7 \
        libopenexr25 \
        libdc1394-25 \
        libtesseract4 \
        libgtk2.0-0 \
        libgomp1 \
        libglib2.0-0 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# App_Data guarda la configuración (JSON cifrado) y las capturas: va en un volumen.
VOLUME ["/app/App_Data"]

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

ENTRYPOINT ["dotnet", "IVZVision.Web.dll"]
