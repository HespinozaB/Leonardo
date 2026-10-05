# Power BI Modeling MCP – Instalador para Claude

`PowerBI-Modeling-MCP-Setup.exe` instala el **Power BI Modeling MCP Server** oficial de Microsoft
y lo registra en **Claude Desktop** y **Claude Code**, para crear y editar modelos semánticos
(tablas, relaciones, medidas DAX, documentación) en lenguaje natural.

> El ejecutable **no incluye** el binario de Microsoft: lo descarga del Visual Studio Marketplace
> oficial (`analysis-services.powerbi-modeling-mcp`, win32-x64) en el momento de instalar.
> El servidor está en PREVIEW y se rige por la licencia de Microsoft
> (`extension\LICENSE.txt` en la carpeta de instalación).

## Requisitos
- Windows 10/11 x64
- Power BI Desktop
- Claude Desktop y/o Claude Code
- Acceso a internet hacia `marketplace.visualstudio.com`

## Uso
Doble clic en `PowerBI-Modeling-MCP-Setup.exe` y elegir:

1. **Instalar / actualizar**: descarga la última versión, la extrae en
   `%LOCALAPPDATA%\PowerBIModelingMCP` (sin permisos de administrador) y configura:
   - Claude Desktop: `%APPDATA%\Claude\claude_desktop_config.json`
     (y la variante de Microsoft Store si existe)
   - Claude Code: `%USERPROFILE%\.claude.json`

   Antes de modificar cada archivo se crea un respaldo `*.bak-AAAAMMDD-HHMMSS`.
2. **Desinstalar**: quita la entrada `powerbi-modeling` de Claude y borra la carpeta.
3. **Ver estado**.

Luego: abrir el `.pbix` en Power BI Desktop, reiniciar Claude y pedir
*"Conéctate a <archivo> en Power BI Desktop"*.

### Línea de comandos
```
PowerBI-Modeling-MCP-Setup.exe install   [-yes] [-readonly] [-version 0.4.0] [-dir C:\ruta]
PowerBI-Modeling-MCP-Setup.exe uninstall [-yes]
PowerBI-Modeling-MCP-Setup.exe status
```
- `-yes`: modo desatendido (configura todos los clientes, sin pausas).
- `-readonly`: registra el servidor con `--readonly` (solo lectura del modelo).

Windows SmartScreen puede avisar porque el `.exe` no está firmado: *Más información → Ejecutar de todas formas*.

## Compilar
Requiere Go 1.24+ (se compila desde cualquier sistema operativo):
```
./build.sh   # genera dist/PowerBI-Modeling-MCP-Setup.exe
```
