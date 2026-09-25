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

## Cambios de la versión 1.4

### Hatch / texturas
- Los **HATCH** del DWG se convierten en **Filled Regions** nativos (también los que están dentro de bloques):
  - hatch con patrón → se crea un **patrón de relleno de modelo** `DWG-<patrón> <separación>mm` con las mismas
    líneas, ángulos, separación y trazos que en el DWG (queda fijo al dibujo, a su escala real);
  - hatch sólido / degradado → relleno sólido;
  - color del hatch (o de su capa/bloque) como color del patrón; tipos `DWG-<patrón> R-G-B`, sin fondo
    (transparentes) y con contorno de líneas invisibles; se respetan las islas (huecos).
  - Si Revit no acepta el patrón exacto se intenta una versión simplificada, luego el patrón `IMPORT-<nombre>`
    que Revit creó al importar, y si nada funciona se conservan las líneas como antes.
- Se eliminan las líneas y rellenos con los que Revit dibujaba esos hatch, para que no queden duplicados.
- Sin el DWG original (importado sin archivo): los rellenos sólidos que Revit muestra se convierten en Filled
  Regions sólidos con el color de la capa, y del resto solo se dibuja el contorno (antes se dibujaban todas las
  aristas de los triángulos). Para convertir hatch con patrón hace falta el .dwg original (vinculado o
  seleccionado en el diálogo).
- Orden de dibujo: Filled Regions → líneas → textos.
- Si un elemento concreto da error al confirmar, se elimina solo ese elemento en vez de deshacer todo.

### Textos
- Tamaño mínimo de texto fijo en **0.2526 mm** (el mínimo de Revit); se usa para el aviso de cambio de escala.

## Cambios de la versión 1.3

### Tamaño de texto según la escala del DWG
- Antes los textos tenían un tamaño mínimo fijo de 0.4 mm en papel: con DWG pequeños (o insertados a
  escala reducida) todos los textos quedaban "topados" en ese mínimo, salían del mismo tamaño y más grandes
  que en el DWG. Ahora solo se limita por debajo del mínimo real de Revit (0.2526 mm desde la 1.4).
- Si aun así muchos textos quedarían por debajo del mínimo a la escala de la vista, el addin **propone cambiar
  la escala de la vista** (p.ej. de 1:100 a 1:5) para que los textos mantengan el mismo tamaño relativo al
  dibujo que en el DWG. Las líneas no cambian; solo las anotaciones. El resumen indica cuántos textos
  quedaron más grandes si se mantiene la escala.
- Se lee el **formato interno de MTEXT**: altura (`\H`), anchura (`\W`), fuente y **negrita** (`\fArial|b1`),
  que antes se ignoraban (p.ej. títulos grandes que salían del tamaño del texto normal).
- Tipos de texto en **negrita** cuando el DWG la usa (tipos `DWG x mm … Negrita`).
- Si los textos leídos del DWG vinculado no caen sobre el CAD (unidades o escala de inserción distintas),
  se usan los de la reexportación, que ya vienen a la escala real del modelo.

## Cambios de la versión 1.2

### Textos
- Los tipos de texto `DWG x mm` se crean con **fondo Transparente**, **sin borde** y con
  **Desfase de línea directriz/borde = 0** (antes heredaban ~2 mm del tipo por defecto, lo que agrandaba
  el recuadro de cada nota). Si ya existían tipos `DWG …` de una versión anterior, se corrigen al reutilizarlos.
- Tamaño de texto **10 % menor** (`TextHeightFactor = 0.9` en `ExplodeDwgCommand.cs`) para que el texto
  quepa en sus recuadros como en el DWG, y el tamaño se redondea a 0.01 mm (antes 0.05 mm).
- Se respeta el **factor de anchura** del texto del DWG y, si el estilo usa una fuente TrueType conocida
  (Arial, Arial Narrow, Calibri, Times New Roman, ISOCPEUR, …), esa **fuente**. Con fuentes SHX se usa Arial.

### Tipos de línea (continuas / segmentadas)
- Revit solo conserva el tipo de línea **por capa**; las líneas con tipo de línea asignado **por entidad**
  (p.ej. ejes con CENTER sobre una capa continua) llegaban continuas. Ahora, cuando se dispone del DWG
  original (vinculado, o seleccionado al ejecutar), se lee el tipo de línea real de cada entidad
  (también dentro de bloques, con ByLayer/ByBlock, LTSCALE y escala de tipo de línea del objeto) y se crea
  el patrón de línea `DWG-<tipo> <largo>mm` escalado a la vista y el Line Style `DWG-<capa>-<tipo> …`.
- También se detectan líneas continuas sobre capas segmentadas.
- Para DWG **importados** sin archivo original, se usa el patrón que Revit asignó a la capa: conviene
  indicar el .dwg original en el diálogo para obtener el tipo de línea exacto.

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
