<#
.SYNOPSIS
    Publica IVZ Vision con HTTPS automático (Let's Encrypt) usando Caddy como proxy inverso.

.DESCRIPTION
    Ejecutar como ADMINISTRADOR. Descarga Caddy para Windows, genera el Caddyfile
    apuntando a la aplicación local y lo registra como servicio de Windows.

    Requisitos previos:
      - El registro DNS del dominio debe apuntar a la IP pública de esta red.
      - Los puertos 80 y 443 del router reenviados a este equipo (Let's Encrypt valida por el 80).
      - La aplicación IVZ Vision corriendo en http://localhost:8080.

    Resultado: https://SU-DOMINIO (puerto 443). El puerto 8080 puede quedar cerrado al exterior.

.EXAMPLE
    .\instalar-https-caddy.ps1 -Dominio vision.miempresa.com
    .\instalar-https-caddy.ps1 -Dominio vision.miempresa.com -Correo admin@miempresa.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Dominio,

    [string]$Correo = "",

    [string]$Carpeta = "C:\Caddy",

    [string]$AppUrl = "http://localhost:8080"
)

$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Este script debe ejecutarse como administrador."
}

New-Item -ItemType Directory -Force -Path $Carpeta | Out-Null
$exe = Join-Path $Carpeta "caddy.exe"

if (-not (Test-Path $exe)) {
    Write-Host "Descargando Caddy (build oficial para Windows x64)..." -ForegroundColor Cyan
    Invoke-WebRequest "https://caddyserver.com/api/download?os=windows&arch=amd64" -OutFile $exe -UseBasicParsing
}

# Caddyfile: HTTPS automático para el dominio, proxy a la aplicación local.
$correoLinea = if ($Correo) { "    email $Correo`r`n" } else { "" }
@"
{
$correoLinea}

$Dominio {
    reverse_proxy $AppUrl
    encode gzip
}
"@ | Out-File (Join-Path $Carpeta "Caddyfile") -Encoding utf8

# Servicio de Windows con arranque automático.
if (Get-Service "Caddy" -ErrorAction SilentlyContinue) {
    sc.exe stop Caddy | Out-Null; Start-Sleep 2; sc.exe delete Caddy | Out-Null; Start-Sleep 2
}
sc.exe create Caddy binPath= "`"$exe`" run --config `"$Carpeta\Caddyfile`"" start= auto DisplayName= "Caddy (HTTPS IVZVision)" | Out-Null
sc.exe failure Caddy reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

# Firewall: 80 (validación Let's Encrypt) y 443 (HTTPS).
netsh advfirewall firewall add rule name="Caddy HTTP 80" dir=in action=allow protocol=TCP localport=80 | Out-Null
netsh advfirewall firewall add rule name="Caddy HTTPS 443" dir=in action=allow protocol=TCP localport=443 | Out-Null

sc.exe start Caddy | Out-Null

Write-Host ""
Write-Host "Caddy instalado y arrancado." -ForegroundColor Green
Write-Host "En 1-2 minutos (emisión del certificado) el sitio estará en: https://$Dominio"
Write-Host "Recuerde: reenvíe los puertos 80 y 443 del router a este equipo."
Write-Host "El puerto 8080 ya no necesita estar abierto al exterior."
