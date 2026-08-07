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
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Dependencias nativas mínimas de OpenCvSharp (runtime oficial linux-x64).
RUN apt-get update && apt-get install -y --no-install-recommends \
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
