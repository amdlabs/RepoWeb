# ---------------------------------------------------------------------------
# IVZ Vision — imagen multiplataforma (Linux, macOS y Windows con Docker Desktop)
#
# La base es Ubuntu 22.04 (jammy) porque los binarios nativos de OpenCvSharp están
# compilados contra sus bibliotecas: en 24.04 varias de ellas ya no existen
# (libavcodec58, libtiff5, libtesseract4…) y la carga del .so falla.
# ---------------------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build
WORKDIR /src

# Primero sólo los proyectos: así la capa de restore se reutiliza mientras no
# cambien las dependencias.
COPY Directory.Build.props ./
COPY IVZVision.sln ./
COPY src/IVZVision.Core/IVZVision.Core.csproj    src/IVZVision.Core/
COPY src/IVZVision.Data/IVZVision.Data.csproj    src/IVZVision.Data/
COPY src/IVZVision.Vision/IVZVision.Vision.csproj src/IVZVision.Vision/
COPY src/IVZVision.Web/IVZVision.Web.csproj      src/IVZVision.Web/
COPY tests/IVZVision.Tests/IVZVision.Tests.csproj tests/IVZVision.Tests/

RUN dotnet restore src/IVZVision.Web/IVZVision.Web.csproj

COPY . .
RUN dotnet publish src/IVZVision.Web/IVZVision.Web.csproj \
        -c Release -o /app/publish --no-restore /p:UseAppHost=false


FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy AS runtime

# Dependencias nativas de OpenCvSharp. La lista sale de los NEEDED de
# libOpenCvSharpExtern.so; sin ellas la captura de vídeo no arranca.
#   v4l-utils permite además diagnosticar las cámaras USB desde el contenedor.
RUN apt-get update && apt-get install -y --no-install-recommends \
        libavcodec58 \
        libavformat58 \
        libavutil56 \
        libswscale5 \
        libtiff5 \
        libtesseract4 \
        libdc1394-25 \
        libgtk2.0-0 \
        libopenexr25 \
        libopenjp2-7 \
        libjpeg-turbo8 \
        libpng16-16 \
        libcairo2 \
        libgdk-pixbuf-2.0-0 \
        libgomp1 \
        libv4l-0 \
        v4l-utils \
        ca-certificates \
        curl \
        tzdata \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

# Los datos que deben sobrevivir al contenedor: configuración, capturas y modelos.
RUN mkdir -p /app/App_Data /app/Models
VOLUME ["/app/App_Data", "/app/Models"]

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    TZ=America/Montevideo

EXPOSE 8080

# El contenedor está sano cuando la web responde; el reconocimiento puede estar
# degradado (sin modelos o sin SQL) y aun así hay que poder entrar a configurarlo.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD curl -fsS http://localhost:8080/ >/dev/null || exit 1

ENTRYPOINT ["dotnet", "IVZVision.Web.dll"]
