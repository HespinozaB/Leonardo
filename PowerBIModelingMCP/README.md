# Power BI Modeling MCP – Instalador para Claude

`PowerBI-Modeling-MCP-Setup.exe` registra dos servidores MCP en **Claude Desktop** y **Claude Code**:

| Servidor | Qué hace |
|---|---|
| `powerbi-modeling` | **Power BI Modeling MCP Server** oficial de Microsoft: modelo semántico (tablas, relaciones, medidas DAX, consultas DAX, documentación). |
| `powerbi-report` | Servidor propio incluido en el `.exe`: **lee y genera gráficos** en los reportes. |

> El ejecutable **no incluye** el binario de Microsoft: lo descarga del Visual Studio Marketplace
> oficial (`analysis-services.powerbi-modeling-mcp`, win32-x64) en el momento de instalar.
> El servidor está en PREVIEW y se rige por la licencia de Microsoft
> (`extension\LICENSE.txt` en la carpeta de instalación).

## Requisitos
- Windows 10/11 x64
- Power BI Desktop
- Claude Desktop y/o Claude Code
- Acceso a internet hacia `marketplace.visualstudio.com`

## Gráficos (`powerbi-report`)

| Herramienta | Descripción |
|---|---|
| `list_pages` | Páginas del reporte. |
| `list_visuals` | Gráficos: tipo, título, posición y campos por rol (eje, valores, leyenda...). |
| `get_visual` | JSON completo de un gráfico. |
| `visual_dax_query` | Consulta DAX con los datos que muestra un gráfico (se ejecuta con `powerbi-modeling`). |
| `list_model_fields` | Tablas, columnas y medidas del modelo del proyecto. |
| `create_visual` | Crea un gráfico: column, stackedcolumn, bar, stackedbar, line, area, combo, pie, donut, treemap, funnel, waterfall, scatter, gauge, card, multirowcard, table, matrix, slicer. |
| `update_visual` | Mueve, redimensiona, cambia título o tipo. |
| `delete_visual`, `create_page`, `delete_page` | Gestión de gráficos y páginas. |

**Formatos:**
- **Lectura:** `.pbip` y `.pbix`.
- **Escritura:** solo `.pbip` con formato **PBIR**. En Power BI Desktop active
  *Opciones > Características en versión preliminar > "Almacenar informes con formato de metadatos mejorado (PBIR)"*
  y guarde con *Archivo > Guardar como > Proyecto de Power BI (.pbip)*.
- Power BI Desktop no recarga cambios externos: después de crear o editar gráficos,
  **cierre y vuelva a abrir el `.pbip`**. Si lo tiene abierto, no guarde desde Desktop o sobrescribirá los cambios.

**Ejemplos de pedidos a Claude:**
- "Lee los gráficos de C:\Reportes\Ventas.pbip y dime qué muestra cada uno."
- "¿Qué valores muestra el gráfico 'Ventas por región'?" (usa `visual_dax_query` y el servidor `powerbi-modeling`)
- "En la página Resumen crea un gráfico de columnas con Ventas[Total] por Clientes[Región] y una tarjeta con el margen."

## Uso
Doble clic en `PowerBI-Modeling-MCP-Setup.exe` y elegir:

1. **Instalar / actualizar**: descarga la última versión, la extrae en
   `%LOCALAPPDATA%\PowerBIModelingMCP` (sin permisos de administrador) y configura:
   - Claude Desktop: `%APPDATA%\Claude\claude_desktop_config.json`
     (y la variante de Microsoft Store si existe)
   - Claude Code: `%USERPROFILE%\.claude.json`

   Además, se copia a sí mismo como `powerbi-report-mcp.exe` en la misma carpeta (servidor de gráficos).

   Antes de modificar cada archivo se crea un respaldo `*.bak-AAAAMMDD-HHMMSS`.
2. **Desinstalar**: quita las entradas `powerbi-modeling` y `powerbi-report` de Claude y borra la carpeta.
3. **Ver estado**.

Luego: abrir el `.pbix` en Power BI Desktop, reiniciar Claude y pedir
*"Conéctate a <archivo> en Power BI Desktop"*.

### Línea de comandos
```
PowerBI-Modeling-MCP-Setup.exe install   [-yes] [-readonly] [-version 0.4.0] [-dir C:\ruta]
PowerBI-Modeling-MCP-Setup.exe uninstall [-yes]
PowerBI-Modeling-MCP-Setup.exe status
PowerBI-Modeling-MCP-Setup.exe serve      # servidor MCP de gráficos (lo usa Claude)
```
- `-yes`: modo desatendido (configura todos los clientes, sin pausas).
- `-readonly`: registra el servidor con `--readonly` (solo lectura del modelo).

Windows SmartScreen puede avisar porque el `.exe` no está firmado: *Más información → Ejecutar de todas formas*.

## Compilar
Requiere Go 1.24+ (se compila desde cualquier sistema operativo):
```
go test ./...  # pruebas
./build.sh     # genera dist/PowerBI-Modeling-MCP-Setup.exe
```
