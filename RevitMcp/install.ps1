<#
  Instala EMASY MCP para Revit 2026 y lo registra en Claude.

  Uso (desde la carpeta descomprimida, en PowerShell):
    powershell -ExecutionPolicy Bypass -File .\install.ps1
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall

  - Copia el addin a %AppData%\Autodesk\Revit\Addins\2026
  - Copia el servidor MCP a %LocalAppData%\EMASY\RevitMcp\server
  - Añade "revit" a la configuración de Claude Desktop (con copia de seguridad)
  - Si encuentra la CLI de Claude Code, lo registra también allí
#>
param(
  [switch]$Uninstall,
  [switch]$SkipClaudeDesktop,
  [switch]$SkipClaudeCode
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$addinsDir = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2026'
$serverDir = Join-Path $env:LOCALAPPDATA 'EMASY\RevitMcp\server'
$desktopConfig = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
$serverEntry = Join-Path $serverDir 'index.js'

function Update-DesktopConfig([scriptblock]$change) {
  $config = [pscustomobject]@{}
  if (Test-Path $desktopConfig) {
    Copy-Item $desktopConfig "$desktopConfig.bak" -Force
    $raw = Get-Content $desktopConfig -Raw -Encoding UTF8
    if ($raw.Trim()) { $config = $raw | ConvertFrom-Json }
  } else {
    New-Item -ItemType Directory -Force -Path (Split-Path $desktopConfig) | Out-Null
  }
  if (-not $config.PSObject.Properties['mcpServers']) {
    $config | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{})
  }
  & $change $config.mcpServers
  $json = $config | ConvertTo-Json -Depth 32
  [System.IO.File]::WriteAllText($desktopConfig, $json, (New-Object System.Text.UTF8Encoding($false)))
}

if ($Uninstall) {
  Remove-Item (Join-Path $addinsDir 'RevitMcp.addin') -Force -ErrorAction SilentlyContinue
  Remove-Item (Join-Path $addinsDir 'EMASY-MCP-2026') -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item $serverDir -Recurse -Force -ErrorAction SilentlyContinue
  if ((Test-Path $desktopConfig) -and -not $SkipClaudeDesktop) {
    Update-DesktopConfig { param($servers) $servers.PSObject.Properties.Remove('revit') }
  }
  if ((Get-Command claude -ErrorAction SilentlyContinue) -and -not $SkipClaudeCode) {
    & claude mcp remove revit -s user 2>$null | Out-Null
  }
  Write-Host 'EMASY MCP desinstalado.' -ForegroundColor Green
  exit 0
}

if (Get-Process Revit -ErrorAction SilentlyContinue) {
  Write-Warning 'Revit está abierto: ciérralo antes de instalar (la DLL puede estar en uso).'
}

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
  Write-Warning 'No se encontró Node.js. Instálalo (versión 18 o superior) desde https://nodejs.org y vuelve a ejecutar este script.'
}

# 1. Addin de Revit
New-Item -ItemType Directory -Force -Path $addinsDir | Out-Null
Copy-Item (Join-Path $here 'RevitMcp.addin') $addinsDir -Force
Remove-Item (Join-Path $addinsDir 'EMASY-MCP-2026') -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $here 'EMASY-MCP-2026') $addinsDir -Recurse -Force
Get-ChildItem (Join-Path $addinsDir 'EMASY-MCP-2026') -Recurse | Unblock-File
Write-Host "Addin instalado en $addinsDir" -ForegroundColor Green

# 2. Servidor MCP
Remove-Item $serverDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $serverDir | Out-Null
Copy-Item (Join-Path $here 'server\*') $serverDir -Recurse -Force
Get-ChildItem $serverDir -Recurse | Unblock-File
Write-Host "Servidor MCP instalado en $serverDir" -ForegroundColor Green

$nodePath = if ($node) { $node.Source } else { 'node' }

# 3. Claude Desktop
if (-not $SkipClaudeDesktop) {
  Update-DesktopConfig {
    param($servers)
    $entry = [pscustomobject]@{ command = $nodePath; args = @($serverEntry) }
    if ($servers.PSObject.Properties['revit']) { $servers.revit = $entry }
    else { $servers | Add-Member -NotePropertyName revit -NotePropertyValue $entry }
  }
  Write-Host "Claude Desktop configurado ($desktopConfig). Reinicia Claude Desktop." -ForegroundColor Green
}

# 4. Claude Code (CLI), si está instalado
if (-not $SkipClaudeCode) {
  if (Get-Command claude -ErrorAction SilentlyContinue) {
    & claude mcp remove revit -s user 2>$null | Out-Null
    & claude mcp add revit -s user -- $nodePath $serverEntry
    Write-Host 'Claude Code configurado (servidor "revit", ámbito usuario).' -ForegroundColor Green
  } else {
    Write-Host "Claude Code no encontrado. Si lo instalas luego: claude mcp add revit -s user -- node `"$serverEntry`""
  }
}

Write-Host ''
Write-Host 'Listo. Abre Revit 2026: verás EMASY > Claude MCP > Estado MCP.' -ForegroundColor Cyan
Write-Host 'Luego pide a Claude, por ejemplo: "¿Qué tengo abierto en Revit?"' -ForegroundColor Cyan
