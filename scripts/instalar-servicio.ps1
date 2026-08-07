<#
.SYNOPSIS
    Instala IVZ Vision como servicio de Windows a partir de una publicación nativa.

.DESCRIPTION
    Ejecutar como ADMINISTRADOR. Registra el servicio «IVZVision» apuntando al
    ejecutable publicado, con arranque automático y recuperación ante fallos.
    La aplicación nativa tiene acceso a las webcams USB del equipo (a diferencia
    del contenedor Docker) y usa el SQL Server local (.\SQLEXPRESS por defecto).

.EXAMPLE
    # 1. Publicar (desde la raíz del repositorio):
    dotnet publish src\IVZVision.Web -c Release -o C:\IVZVision

    # 2. Instalar el servicio (PowerShell como administrador):
    .\scripts\instalar-servicio.ps1 -Carpeta C:\IVZVision -Puerto 8080
#>
[CmdletBinding()]
param(
    [string]$Carpeta = "C:\IVZVision",
    [int]$Puerto = 8080,
    [string]$Nombre = "IVZVision"
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $Carpeta "IVZVision.Web.exe"
if (-not (Test-Path $exe)) {
    throw "No se encuentra $exe. Publique primero: dotnet publish src\IVZVision.Web -c Release -o $Carpeta"
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Este script debe ejecutarse como administrador."
}

if (Get-Service $Nombre -ErrorAction SilentlyContinue) {
    Write-Host "El servicio $Nombre ya existe; se detiene y se vuelve a crear." -ForegroundColor Yellow
    sc.exe stop $Nombre | Out-Null
    Start-Sleep -Seconds 3
    sc.exe delete $Nombre | Out-Null
    Start-Sleep -Seconds 2
}

# --urls fija el puerto de escucha; --contentRoot asegura que App_Data y Models se
# resuelvan en la carpeta publicada aunque el servicio arranque desde system32.
$binPath = "`"$exe`" --urls http://+:$Puerto --contentRoot `"$Carpeta`""

sc.exe create $Nombre binPath= $binPath start= auto DisplayName= "IVZ Vision" | Out-Null
sc.exe description $Nombre "Reconocimiento facial, de matriculas y de objetos en local (IVZ Vision)" | Out-Null
sc.exe failure $Nombre reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
sc.exe start $Nombre | Out-Null

Write-Host ""
Write-Host "Servicio «$Nombre» instalado y arrancado." -ForegroundColor Green
Write-Host "Web: http://localhost:$Puerto  (usuario inicial admin/admin)"
Write-Host "Nota: para abrir el puerto a otros equipos: netsh advfirewall firewall add rule name=IVZVision dir=in action=allow protocol=TCP localport=$Puerto"
