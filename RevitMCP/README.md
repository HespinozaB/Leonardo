# RevitMCP — Claude ↔ Revit 2024

Conecta Claude (Desktop o Claude Code) con **Revit 2024** abierto en tu ordenador, también con Revit **en español**. Claude puede consultar el modelo, crear y modificar elementos, sacar capturas de vistas y ejecutar código de la API de Revit.

```
Claude Desktop / Claude Code
        │  MCP (stdio)
        ▼
mcp_server/revit_mcp_server.py      (Python, en tu PC)
        │  TCP 127.0.0.1:8765 (JSON por líneas)
        ▼
Add-in RevitMCP.dll dentro de Revit 2024  →  API de Revit (ExternalEvent)
```

El puente solo escucha en `127.0.0.1`: nada queda expuesto a la red.

## Instalación

Requisitos: Windows, Revit 2024, [Python 3.10+](https://www.python.org/downloads/) (marca *Add python.exe to PATH*) y Claude Desktop o Claude Code.

1. Descarga el paquete **RevitMCP-2024** (zip) desde la pestaña *Actions* del repositorio, en la última ejecución del workflow *RevitMCP* (sección *Artifacts*). También puedes compilarlo tú (ver más abajo).
2. Descomprímelo, abre PowerShell en esa carpeta y ejecuta:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
   El script copia el add-in a `%APPDATA%\Autodesk\Revit\Addins\2024`, instala el servidor en `%LOCALAPPDATA%\RevitMCP` con su propio entorno de Python y lo registra en Claude Desktop (y en Claude Code, si tienes el comando `claude`).
3. Abre Revit 2024 y elige **Cargar siempre** cuando pregunte por *RevitMCP*. Aparece la pestaña **MCP**, con el botón **Servidor MCP** para iniciarlo o detenerlo. Se inicia solo al abrir Revit.
4. Cierra Claude Desktop por completo (también desde la bandeja del sistema) y vuelve a abrirlo.
5. Prueba: *"¿Qué niveles tiene el proyecto abierto en Revit?"*

### Registro manual (si no usas el script)

`%APPDATA%\Claude\claude_desktop_config.json`:
```json
{
  "mcpServers": {
    "revit": {
      "command": "C:\\ruta\\a\\python.exe",
      "args": ["C:\\ruta\\a\\mcp_server\\revit_mcp_server.py"]
    }
  }
}
```
Claude Code: `claude mcp add revit --scope user -- C:\ruta\a\python.exe C:\ruta\a\mcp_server\revit_mcp_server.py`

## Herramientas

| Herramienta | Qué hace |
|---|---|
| `estado`, `info_proyecto` | Versión e idioma de Revit, documento y vista activos |
| `listar_niveles`, `listar_vistas`, `listar_categorias`, `listar_tipos` | Inventario del proyecto |
| `consultar_elementos` | Buscar por categoría, nivel, nombre o valor de parámetro |
| `obtener_parametros`, `obtener_seleccion` | Leer parámetros y la selección actual |
| `seleccionar_elementos`, `abrir_vista`, `captura_vista` | Interactuar con la interfaz; la captura devuelve un PNG que Claude puede ver |
| `establecer_parametro` | Cambiar parámetros de instancia o de tipo |
| `crear_nivel`, `crear_muro`, `crear_suelo`, `crear_habitacion`, `crear_vista_planta` | Modelado básico |
| `colocar_familia` | Colocar mobiliario, puertas o ventanas (con muro anfitrión) |
| `mover_elementos`, `copiar_elementos`, `eliminar_elementos` | Editar |
| `ejecutar_codigo` | Ejecutar C# arbitrario contra la API (para todo lo demás) |

- **Unidades**: todas las coordenadas y longitudes van en **milímetros**.
- **Revit en español**: las categorías aceptan `OST_Walls`, `Walls` o `Muros`, y los parámetros aceptan el nombre visible (`Comentarios`) o el interno (`ALL_MODEL_INSTANCE_COMMENTS`).
- Cada cambio es una transacción con nombre `MCP: …`, así que se deshace con **Ctrl+Z** en Revit. Los avisos de Revit se devuelven a Claude en lugar de abrir diálogos.

## Configuración

Variables de entorno de Windows (opcionales):

| Variable | Valor por defecto | Uso |
|---|---|---|
| `REVIT_MCP_PORT` | `8765` | Puerto local (debe coincidir en Revit y en el servidor) |
| `REVIT_MCP_AUTOSTART` | `1` | `0` = no arrancar el puente al abrir Revit |
| `REVIT_MCP_ALLOW_CODE` | `1` | `0` = desactivar `ejecutar_codigo` |

## Compilar

```powershell
dotnet build addin\RevitMCP.csproj -c Release
```
No necesita Revit instalado: las referencias de la API vienen de los paquetes NuGet `Nice3point.Revit.Api.*` 2024. La salida queda en `addin\bin\Release\RevitMCP-2024\`.

## Solución de problemas

- **"No hay conexión con Revit"**: Revit debe estar abierto con un proyecto y el botón *Servidor MCP* activo. Si el puerto 8765 está ocupado, cambia `REVIT_MCP_PORT`.
- **"Tiempo de espera agotado"**: Revit solo atiende peticiones cuando está libre. Cierra diálogos o comandos en curso (pulsa Esc).
- **El add-in no carga**: ejecuta `install.ps1` de nuevo, porque desbloquea las DLL descargadas.
