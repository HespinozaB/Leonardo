# RevitDwgExploder (Revit 2024)

Addin que convierte los DWG importados/vinculados de la vista activa (o los seleccionados) en
**Detail Lines** y **TextNotes** nativos de Revit, en la misma posición y escala, sin tocar el DWG original.

## Instalación

1. Copiar `RevitDwgExploder.addin` y la carpeta `RevitDwgExploder-2024/` a
   `%AppData%\Autodesk\Revit\Addins\2024\`.
2. Si Windows bloqueó los DLL descargados: clic derecho → Propiedades → *Desbloquear*.
3. Abrir Revit 2024 → pestaña **DWG Tools** → **Explotar DWGs**.

## Compilar

```bash
./build-zip.sh      # genera dist/RevitDwgExploder-2024.zip
```

Requiere .NET SDK 6+ (las referencias de la API de Revit 2024 vienen de NuGet, no hace falta tener Revit instalado).

## Cambios de la versión 1.1 (optimización)

### Rendimiento
- **Sin diálogos de advertencia**: un `IFailuresPreprocessor` descarta los warnings
  ("línea ligeramente fuera de eje", etc.), que antes podían aparecer por miles y ralentizar el commit.
- **Creación de líneas en lote** (`NewDetailCurveArray`, lotes de 500 agrupados por Line Style), con
  reintento curva a curva solo si un lote falla.
- **Segmentos duplicados eliminados** (aristas compartidas de mallas/sólidos, líneas superpuestas del DWG).
- **Reexportación a DWG una sola vez por vista** (antes se exportaba la vista completa, hasta 2 veces,
  por *cada* DWG sin archivo original).
- **Cada DWG vinculado se lee una sola vez** aunque haya varias instancias del mismo archivo, y se lee sin
  verificación CRC ni resumen.
- **Tipos de texto**: los `TextNoteType` existentes se consultan una sola vez (antes, un colector por cada tamaño nuevo).
- **Textos**: se crean con `TextNoteOptions` (rotación y alineación en la creación) en lugar de crear + rotar cada nota.
- Búsqueda de textos existentes con índice espacial en vez de comparar contra todos.
- OCR: se eliminó la carga de la imagen con System.Drawing solo para leer su tamaño.

### Correcciones
- **Line Styles por capa**: las capas del DWG son subcategorías del import y Revit no permite asignarlas a
  una Detail Line (antes fallaba en silencio para cada línea y todas quedaban con el estilo por defecto).
  Ahora se crea un Line Style `DWG-<capa>` con el color, grosor y patrón de la capa, una sola vez.
- **Círculos y elipses completos** ya no se pierden: se dividen en dos arcos.
- **Textos dentro de bloques** (y bloques anidados, con escala/rotación/punto base) y **atributos** visibles
  ahora se importan también desde el DWG vinculado.
- **Alineación del texto** respetada (izquierda/centro/derecha, arriba/medio/abajo) según TEXT/MTEXT.
- Textos duplicados entre varias instancias o métodos ya no se crean dos veces.
- Si una curva no está en el plano de la vista, se reintenta proyectándola.
- OCR: el recorte temporal de la vista se hace dentro de un `TransactionGroup` que se deshace, así que no
  deja entradas en el historial de deshacer; además funciona con vistas giradas.
- El botón se desactiva en vistas que no admiten Detail Lines (3D, plantillas, tablas…).
- Si hay varias instancias del mismo CAD importado, el archivo original se pide una sola vez por tipo.
- Todo el resultado queda en una única transacción (un solo *Deshacer*).
