# Instala RevitMCP en este ordenador (Windows):
#   1. Add-in de Revit 2024  -> %APPDATA%\Autodesk\Revit\Addins\2024
#   2. Servidor MCP (Python) -> %LOCALAPPDATA%\RevitMCP (con su propio entorno virtual)
#   3. Registro en Claude Desktop y, si está instalado, en Claude Code.
#
# Uso (PowerShell, desde la carpeta descomprimida):
#   powershell -ExecutionPolicy Bypass -File .\install.ps1

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# Los archivos descargados de Internet quedan "bloqueados" y Revit no carga la DLL.
Get-ChildItem -Path $here -Recurse -File | Unblock-File

# ---------------------------------------------------------------- 1. Add-in
$addinsDir = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2024'
New-Item -ItemType Directory -Force -Path $addinsDir | Out-Null
Copy-Item (Join-Path $here 'RevitMCP.addin') $addinsDir -Force
Copy-Item (Join-Path $here 'RevitMCP-2024') $addinsDir -Recurse -Force
Write-Host "[OK] Add-in copiado a $addinsDir" -ForegroundColor Green

# ---------------------------------------------------------------- 2. Servidor MCP
$installDir = Join-Path $env:LOCALAPPDATA 'RevitMCP'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item (Join-Path $here 'mcp_server') $installDir -Recurse -Force
$serverScript = Join-Path $installDir 'mcp_server\revit_mcp_server.py'

$python = $null
foreach ($candidate in @('py', 'python')) {
    if (Get-Command $candidate -ErrorAction SilentlyContinue) { $python = $candidate; break }
}
if (-not $python) {
    throw 'No se encontró Python. Instala Python 3.10 o superior desde https://www.python.org/downloads/ (marca "Add python.exe to PATH") y vuelve a ejecutar este script.'
}

$venv = Join-Path $installDir 'venv'
if ($python -eq 'py') { & py -3 -m venv $venv } else { & python -m venv $venv }
$venvPython = Join-Path $venv 'Scripts\python.exe'
& $venvPython -m pip install --upgrade pip --quiet
& $venvPython -m pip install 'mcp>=1.2,<2' --quiet
Write-Host "[OK] Servidor MCP instalado en $installDir" -ForegroundColor Green

# ---------------------------------------------------------------- 3a. Claude Desktop
$claudeDir = Join-Path $env:APPDATA 'Claude'
$configPath = Join-Path $claudeDir 'claude_desktop_config.json'
New-Item -ItemType Directory -Force -Path $claudeDir | Out-Null
if (Test-Path $configPath) {
    Copy-Item $configPath "$configPath.bak" -Force
    $raw = Get-Content $configPath -Raw
    $config = if ([string]::IsNullOrWhiteSpace($raw)) { [pscustomobject]@{} } else { $raw | ConvertFrom-Json }
} else {
    $config = [pscustomobject]@{}
}
if (-not ($config.PSObject.Properties.Name -contains 'mcpServers')) {
    $config | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue ([pscustomobject]@{})
}
$entry = [pscustomobject]@{ command = $venvPython; args = @($serverScript) }
if ($config.mcpServers.PSObject.Properties.Name -contains 'revit') {
    $config.mcpServers.revit = $entry
} else {
    $config.mcpServers | Add-Member -NotePropertyName 'revit' -NotePropertyValue $entry
}
$json = $config | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($configPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "[OK] Registrado en Claude Desktop ($configPath)" -ForegroundColor Green

# ---------------------------------------------------------------- 3b. Claude Code (opcional)
if (Get-Command claude -ErrorAction SilentlyContinue) {
    & claude mcp remove revit --scope user 2>$null | Out-Null
    & claude mcp add revit --scope user -- $venvPython $serverScript
    Write-Host '[OK] Registrado en Claude Code (claude mcp list)' -ForegroundColor Green
}

Write-Host ''
Write-Host 'Listo. Pasos siguientes:' -ForegroundColor Cyan
Write-Host '  1. Abre (o reinicia) Revit 2024 y acepta cargar "RevitMCP" (Cargar siempre).'
Write-Host '  2. Cierra Claude Desktop por completo (también desde la bandeja) y ábrelo de nuevo.'
Write-Host '  3. Pide a Claude: "usa la herramienta estado de revit".'
