<#
.SYNOPSIS
    Emite un certificado Let's Encrypt (firmado, sin avisos del navegador) para el
    dominio indicado y lo enlaza a Cerbero Garage en el puerto HTTPS que se elija.

.DESCRIPTION
    Ejecutar como ADMINISTRADOR. Pensado para equipos donde IIS ya ocupa los puertos
    80 y 443: la validación se hace por fichero en la raíz de IIS (HTTP-01), de modo
    que no hay que parar nada.

    Pasos que realiza:
      1. Descarga win-acme (cliente ACME para Windows).
      2. Valida el dominio dejando el reto en la raíz web de IIS.
      3. Instala el certificado en LocalMachine\My y programa su renovación.
      4. Configura Kestrel para servir HTTPS con ese certificado en el puerto indicado
         y deja el HTTP sólo para acceso local.
      5. Reinstala el servicio con los nuevos puertos.

    Requisitos: el dominio debe apuntar a esta red y el puerto 80 estar reenviado
    a este equipo (es donde Let's Encrypt comprueba la propiedad del dominio).

.EXAMPLE
    .\instalar-https-letsencrypt.ps1 -Dominio amdlabs.blogdns.org -PuertoHttps 8080 -Correo admin@midominio.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Dominio,

    [int]$PuertoHttps = 8080,

    # Puerto HTTP interno (sólo localhost) para administración local.
    [int]$PuertoHttpLocal = 5080,

    [string]$Correo = "",

    [string]$Carpeta = "C:\IVZVision",

    [string]$RaizWebIis = "C:\inetpub\wwwroot",

    [string]$Servicio = "IVZVision"
)

$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Este script debe ejecutarse como administrador."
}

# ---- 1. win-acme -----------------------------------------------------------
$wacsDir = "C:\win-acme"
$wacs = Join-Path $wacsDir "wacs.exe"

if (-not (Test-Path $wacs)) {
    Write-Host "Descargando win-acme..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $wacsDir | Out-Null
    $zip = Join-Path $env:TEMP "win-acme.zip"
    $url = "https://github.com/win-acme/win-acme/releases/download/v2.2.9.1701/win-acme.v2.2.9.1701.x64.pluggable.zip"
    Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
    Expand-Archive $zip -DestinationPath $wacsDir -Force
    Remove-Item $zip -Force

    if (-not (Test-Path $wacs)) {
        $encontrado = Get-ChildItem $wacsDir -Filter wacs.exe -Recurse | Select-Object -First 1
        if ($encontrado) { $wacs = $encontrado.FullName } else { throw "No se encontró wacs.exe tras descomprimir." }
    }
}

# ---- 2 y 3. Certificado ----------------------------------------------------
Write-Host "Solicitando certificado para $Dominio ..." -ForegroundColor Cyan

$argumentos = @(
    "--target", "manual",
    "--host", $Dominio,
    "--validation", "filesystem",
    "--webroot", $RaizWebIis,
    "--store", "certificatestore",
    "--certificatestore", "My",
    "--accepttos",
    "--notaskscheduler:false"
)
if ($Correo) { $argumentos += @("--emailaddress", $Correo) }

& $wacs @argumentos
if ($LASTEXITCODE -ne 0) { throw "win-acme terminó con código $LASTEXITCODE. Revise la salida anterior." }

$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -like "*$Dominio*" } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) { throw "El certificado no aparece en LocalMachine\My." }
Write-Host "Certificado emitido: $($cert.Thumbprint) (vence $($cert.NotAfter))" -ForegroundColor Green

# ---- 4. Kestrel usa ese certificado ---------------------------------------
$appsettings = Join-Path $Carpeta "appsettings.Production.json"
$config = if (Test-Path $appsettings) { Get-Content $appsettings -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }

$kestrel = [pscustomobject]@{
    Certificates = [pscustomobject]@{
        Default = [pscustomobject]@{
            Subject       = $Dominio
            Store         = "My"
            Location      = "LocalMachine"
            AllowInvalid  = $false
        }
    }
}
$config | Add-Member -NotePropertyName Kestrel -NotePropertyValue $kestrel -Force
$config | ConvertTo-Json -Depth 10 | Out-File $appsettings -Encoding utf8
Write-Host "Kestrel configurado para usar el certificado del almacén." -ForegroundColor Green

# ---- 5. Servicio con los puertos nuevos ------------------------------------
$exe = Join-Path $Carpeta "IVZVision.Web.exe"
$urls = "http://localhost:$PuertoHttpLocal;https://+:$PuertoHttps"

if (Get-Service $Servicio -ErrorAction SilentlyContinue) {
    sc.exe stop $Servicio | Out-Null
    Start-Sleep -Seconds 4
    sc.exe delete $Servicio | Out-Null
    Start-Sleep -Seconds 2
}

sc.exe create $Servicio binPath= "`"$exe`" --urls `"$urls`" --contentRoot `"$Carpeta`"" start= auto DisplayName= "Cerbero Garage" | Out-Null
sc.exe description $Servicio "Reconocimiento facial, de matriculas y de objetos en local (Cerbero Garage)" | Out-Null
sc.exe failure $Servicio reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

netsh advfirewall firewall add rule name="Cerbero Garage HTTPS $PuertoHttps" dir=in action=allow protocol=TCP localport=$PuertoHttps | Out-Null

sc.exe start $Servicio | Out-Null

Write-Host ""
Write-Host "Listo. El sitio está en https://$Dominio`:$PuertoHttps" -ForegroundColor Green
Write-Host "Administración local: http://localhost:$PuertoHttpLocal"
Write-Host "La renovación automática la gestiona la tarea programada de win-acme."
Write-Host "Nota: tras cada renovación conviene reiniciar el servicio ($Servicio) para que tome el certificado nuevo."
