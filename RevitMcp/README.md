# EMASY · MCP para Revit 2026

Permite que **Claude** (Claude Desktop o Claude Code en tu PC) opere Revit 2026 directamente: consultar el modelo, cambiar parámetros, crear muros, suelos, niveles, planos, colocar familias, capturar vistas y ejecutar código C# dentro de Revit.

```
Claude Desktop / Claude Code ──stdio──► server/ (Node, MCP) ──TCP 127.0.0.1:8765 + token──► addin EMASY MCP (dentro de Revit)
```

* El addin abre un puerto **solo en este equipo** (127.0.0.1) y exige un token que genera al primer arranque en `%AppData%\EMASY\RevitMcp\bridge.json`. El servidor MCP lo lee de ahí.
* Cada orden se ejecuta en el hilo de Revit (ExternalEvent) y cada modificación es **una transacción con nombre "MCP: …"**, así que se deshace con **Ctrl+Z**.
* Las advertencias de Revit no abren diálogos: se descartan y se devuelven a Claude. Si hay un error, se deshace la operación y Claude recibe el mensaje.
* Unidades de la interfaz: **milímetros** y **grados** (Revit usa pies internamente; la conversión es automática).

## Instalación (Windows)

Requisitos: Revit 2026 y [Node.js](https://nodejs.org) 18 o superior.

1. Descomprime `EMASY-MCP-2026.zip`.
2. Con Revit cerrado, en esa carpeta: `powershell -ExecutionPolicy Bypass -File .\install.ps1`
   * Copia el addin a `%AppData%\Autodesk\Revit\Addins\2026`.
   * Copia el servidor a `%LocalAppData%\EMASY\RevitMcp\server`.
   * Añade el servidor `revit` a Claude Desktop (`%AppData%\Claude\claude_desktop_config.json`, con copia `.bak`) y a Claude Code si está instalado.
3. Abre Revit 2026 (acepta cargar el addin) y un proyecto. En **EMASY › Claude MCP › Estado MCP** debe decir *Puente MCP activo*.
4. Reinicia Claude Desktop y pregúntale: *"¿Qué tengo abierto en Revit?"*

Desinstalar: `install.ps1 -Uninstall`.

### Configuración manual

Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "revit": { "command": "node", "args": ["C:\\Users\\TU_USUARIO\\AppData\\Local\\EMASY\\RevitMcp\\server\\index.js"] }
  }
}
```

Claude Code: `claude mcp add revit -s user -- node "%LOCALAPPDATA%\EMASY\RevitMcp\server\index.js"`

## Herramientas

| Herramienta | Qué hace |
|---|---|
| `get_revit_info` | Versión, documento y vista activos, documentos abiertos |
| `get_selection` / `set_selection` | Leer o fijar la selección (con zoom) |
| `list_elements` | Buscar por categoría (`Muros` u `OST_Walls`), clase, vista, nombre o parámetro; paginado |
| `get_element` | Ubicación y todos los parámetros (de ejemplar y de tipo) |
| `list_views`, `list_levels`, `list_family_types` | Vistas/planos, niveles, tipos disponibles |
| `activate_view` | Abrir una vista |
| `export_view_image` | Captura PNG de una vista o plano que Claude puede ver |
| `set_parameters` | Cambiar parámetros en lote (números en mm/m²… o texto como en Revit) |
| `delete_elements` | Eliminar elementos |
| `transform_elements` | Mover, copiar, girar, simetría |
| `create_level`, `create_grid` | Niveles (con planta) y rejillas |
| `create_wall` | Muros sueltos, en lote o como polilínea cerrada |
| `create_floor` | Suelo a partir de un contorno |
| `place_family_instance` | Familias (con anfitrión para puertas/ventanas) |
| `create_text_note` | Notas de texto |
| `create_sheet`, `place_view_on_sheet` | Planos y viewports |
| `run_csharp` | Código C# libre dentro de Revit (`doc`, `uidoc`, `uiapp`, `Print`, `Mm`) |

`run_csharp` da acceso completo a la API de Revit: Claude puede hacer cualquier cosa que haría un addin. Se ejecuta dentro de una transacción (deshacible) salvo que se indique `transaction: false`. Claude Desktop y Claude Code piden confirmación antes de cada herramienta salvo que la apruebes de forma permanente; para esta conviene revisar el código antes de aceptarlo.

## Problemas frecuentes

* **"No hay conexión con Revit"**: Revit no está abierto, el addin no se cargó (revisa que Windows no bloqueó la DLL; el instalador la desbloquea) o el puerto está ocupado.
* **Dos Revit abiertos**: solo el primero abre el puerto 8765. Para usar otro, cambia `Port` en `bridge.json` (afecta a ambos lados).
* **"Revit no respondió a tiempo"**: Revit tiene un diálogo abierto o está ejecutando un comando. Ciérralo y repite.
* **`run_csharp` falla al cargar Roslyn**: el resto de herramientas siguen funcionando; avisa con el mensaje de error.

## Desarrollo

```bash
./build-zip.sh                # dist/EMASY-MCP-2026.zip
cd server && npm install && npm test   # prueba el servidor MCP contra un puente falso (sin Revit)
```

El addin compila contra los ensamblados de referencia de Revit 2026 (`Nice3point.Revit.Api.*`, .NET 8). Para añadir una herramienta: implementarla en `addin/src/Tools`, registrarla en `ToolRegistry.cs` y describirla en `server/tools.js` (la prueba verifica que ambas listas coinciden).
