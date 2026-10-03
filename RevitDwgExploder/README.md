# EMASY · DWG Tools (Revit 2024)

Addin con tres grupos en la pestaña **EMASY**:

- **DWG Tools** — *Explotar en Vista Actual* y *Explotar Varios DWG's*: convierte los DWG importados/vinculados en
  Detail Lines, Filled Regions y TextNotes nativos de Revit, en la misma posición y escala, sin tocar el DWG original.
- **PDF Tools** — *Explotar PDF Actual* y *Explotar Varios PDF's*: lo mismo con los PDF (insertados o desde archivo).
- **Imágenes Tools** — *Explotar Imagen Actual* y *Explotar Varias Imágenes*: vectoriza imágenes de planos (PNG, JPG…).

## Instalación

### Con el instalador (recomendado)
1. Descargar `EMASY-DWGTools-2024-Setup.exe`.
2. Cerrar Revit y ejecutar el `.exe`. Como no está firmado digitalmente, Windows puede mostrar
   "Windows protegió su PC": pulsar **Más información → Ejecutar de todas formas** (solo la primera vez).
3. Pulsar **Instalar** (o **Actualizar**). No necesita permisos de administrador.

El instalador copia el addin a `%AppData%\Autodesk\Revit\Addins\2024\`, elimina versiones anteriores
(incluida `RevitDwgExploder`) y **desbloquea todos los archivos** automáticamente. También permite
**Desinstalar**. Uso sin ventana: `EMASY-DWGTools-2024-Setup.exe /silent` (o `/silent /uninstall`).

### Manual (ZIP)
1. **Antes de extraer**, clic derecho sobre el ZIP → Propiedades → *Desbloquear*: así ningún archivo
   extraído queda bloqueado (no hace falta desbloquearlos uno a uno).
2. Borrar la versión anterior si existe (`RevitDwgExploder.addin` / `RevitDwgExploder-2024/`).
3. Copiar `EMASY.addin` y la carpeta `EMASY-2024/` a `%AppData%\Autodesk\Revit\Addins\2024\`.

Después: Revit 2024 → pestaña **EMASY** → grupo **DWG Tools** → **Explotar DWGs** / **Buscador DWG's**.

## Compilar

```bash
./build-zip.sh      # genera dist/EMASY-DWGTools-2024.zip y dist/EMASY-DWGTools-2024-Setup.exe
python3 tools/make_icons.py   # regenera los iconos (requiere Pillow)
```

Requiere .NET SDK 6+ (las referencias de la API de Revit 2024 vienen de NuGet, no hace falta tener Revit instalado).

## Cambios de la versión 1.18.2

### Depurar Modelo: eliminar más rápido
* **Eliminar** ya no vuelve a analizar todo el modelo: solo quita de la lista lo que se borró (y sus dependientes). Para recalcular todo, botón **Actualizar**.
* Los **parámetros** también se eliminan en lote (antes uno por uno).
* **Planos sin uso**: se quitan las columnas *Otras anotaciones* e *Id* (y su cálculo).

## Cambios de la versión 1.18.1

### Depurar Modelo mucho más rápido
* **Planos sin uso** ya no obliga a Revit a "generar gráficos" de cada plano: las anotaciones de todos los planos se cuentan en una sola pasada. En modelos con cientos de planos pasa de muchos minutos a segundos, también al refrescar la lista después de eliminar.
* **Eliminar** borra todo lo marcado de una sola vez (Revit regenera una vez, no una por elemento). Si algo no se puede borrar, sigue uno por uno e informa cuál.

## Cambios de la versión 1.18

### Nuevo grupo Depurar Modelo

Cuatro botones que se usan en orden; cada ventana tiene **Siguiente ▸** para pasar al paso siguiente:

| Paso | Botón | Qué muestra |
|---|---|---|
| 1 | **Planos sin uso** | Número, nombre, vistas colocadas y tablas de cada plano. Vacío → ✓, con vistas o tablas → ✗. |
| 2 | **Vistas sin plano** | Nombre de vista, tipo y nombre de plano (`NA` si no está en ninguno). Vistas 3D, con dependientes y tablas sin plano → ⚠. |
| 3 | **Filtros sin uso** | Activo en vista, activo en plano y en plantillas de vista. |
| 4 | **Parámetros sin uso** | Activo en plano, activo en tabla, proceder a eliminar y valores (vacío / con información). |

* Marcas: **✓ verde** sí / se puede eliminar, **✗ rojo** no / en uso, **⚠ ámbar** sin uso pero con algo que revisar (el motivo está en *Alertas*).
* Casillas para elegir qué eliminar; **Eliminar** (con confirmación, se deshace con Ctrl+Z), **Ubicar** (o doble clic), **Seleccionar**, **Exportar CSV** y **Columnas ▾** para mostrar una a una las columnas ocultas.
* Parámetros: los valores se buscan con el filtro nativo de Revit "tiene valor", así que la ventana carga rápido. Un parámetro sin uso pero con información sale con ⚠.
* Las tablas no usan filtros de vista: borrar un filtro nunca afecta a una tabla.
* El instalador quita el Depurador si antes se instaló aparte (*EMASY Parámetros* / *EMASY Depurador*), para que no aparezca dos veces.

## Cambios de la versión 1.17

### Explotar imágenes: máxima interpretación y calidad de imagen

- **Calidad de imagen**: las imágenes de baja resolución (lado mayor ≤ 1600 px) se amplían 2× antes de procesarlas;
  líneas y rellenos salen más continuos y el texto pequeño se lee mejor.
- **Textos**:
  - OCR en español + inglés (modelo de español incluido): tildes y Ñ (TÉCNICAS, LÁMINAS, BAÑOS, BRUÑADO).
  - Cada zona de texto dudosa se vuelve a leer sola, con los dos idiomas y en dos versiones de la imagen; gana la
    lectura más segura. Las etiquetas pegadas a títulos o líneas ya no se mezclan.
  - Corrector con vocabulario de construcción (español/inglés): tildes que faltan y letras confundidas típicas del OCR
    (WACK → WICK, MEADER → HEADER, STUOS → STUDS, RESSSTANT → RESISTANT), solo cuando hay motivo y una única
    palabra posible. Viñetas « “ * - → "·" y "NIVEL 20.00" → "NIVEL ±0.00".
  - Se descartan lecturas basura de rayados (códigos con baja confianza, palabras cortas en minúsculas, cajas
    demasiado estrechas).
- **Líneas**: detección directa de líneas rectas a 0°, 90°, 45° y 135° (muros, rayados): los rayados cruzados salen
  como líneas rectas continuas en vez de zigzag; las líneas que se cortan en los cruces se vuelven a unir; el color de
  las líneas finas se toma de su tinta real (ya no salen grises claras).
- **Rellenos válidos para Revit**: los contornos se limpian (sin partes de 1 px ni "pellizcos" en diagonal), se
  comprueba que no se crucen y se respeta el tramo mínimo de Revit; si Revit rechaza algún hueco, se crea el relleno
  con los huecos que acepte (antes se perdían rellenos grandes, como la membrana gris).

## Cambios de la versión 1.16

### Explotar imágenes: textos del tamaño correcto y más rápido

- **Tamaño de los textos**: se mide la altura real de las mayúsculas en la imagen (sin contar subrayados, paréntesis
  ni acentos), así una etiqueta subrayada ya no sale enorme. Los textos de tamaño parecido comparten tamaño y negrita.
- **Ancho**: cada texto lleva un factor de ancho (tipos "x0.85"…) para ocupar lo mismo que en la imagen: los textos de
  fuentes estrechas ya no se salen de las tablas.
- **Negrita** recalibrada (la leyenda en negrita sale en negrita y las etiquetas normales, normales).
- **OCR**: imagen ampliada 2× (más rápido y lee mejor: ahora también C08, F05…); las letras negras gruesas ya no
  se borran antes de leer (volvía "OTAS" en vez de "NOTAS"); se quita la "I" que el OCR lee en un marco pegado al
  texto ("IDETALL"); "FOS" → "F05".
- **Líneas**: se conserva el subrayado de las etiquetas (es parte de la línea de referencia); ya no quedan restos de
  letras ni manchas grises en los textos ni en la vegetación.

## Cambios de la versión 1.15

### Explotar imágenes: resultado mucho más limpio

- **Textos**: el OCR lee la imagen ampliada y en tres pasadas (imagen original, imagen sin líneas largas ni
  rellenos —las etiquetas subrayadas por líneas de referencia ahora se leen— y, una a una, las zonas con forma de
  texto que faltaban). Se descartan lecturas basura de rayados y vegetación ("zzzz"), se corrigen códigos (co2 → C02,
  $05 → S05), el tamaño se ajusta al ancho real del texto (no se sale de su sitio), los tamaños parecidos se unifican,
  se detecta la negrita y se usa la línea base del texto.
- **Líneas**: grosor relativo al trazo fino de la imagen (plumas de 0.13 a 0.35 mm): ya no aparecen líneas gruesas ni
  "puntos negros". Se eliminan las motas sueltas y los tramos minúsculos.
- **Rellenos**: se detectan también las muestras de color pequeñas (leyendas), los rayados dentro de una mancha no la
  cortan, los sombreados grises claros pasan a rellenos y los rellenos respetan sus huecos. Se corrigió el trazado
  de contornos que cortaba algunas manchas a la mitad, y colores vecinos parecidos (gris/verde) ya no se mezclan.

## Cambios de la versión 1.14

### Nuevo grupo Imágenes Tools

- **Explotar Imagen Actual**: explota las imágenes (PNG, JPG, BMP, TIF, GIF) insertadas en la vista actual (o las
  seleccionadas) en su lugar, con la misma posición y tamaño. Si no hay ninguna, permite elegir un archivo de imagen
  e importarlo en una vista de dibujo nueva o en la vista actual (resolución en ppp y escala 1:N).
- **Explotar Varias Imágenes**: la misma ventana de lista que DWG/PDF con todas las imágenes del modelo: buscar,
  filtrar, seleccionar, ubicar, eliminar, explotar varias a la vez y "Agregar imágenes…" desde archivos.
- La imagen se vectoriza: las manchas grandes de color pasan a Filled Regions (con su color), los trazos a Detail
  Lines rectas (Line Styles "IMG R-G-B grosor", con grosores ajustados a plumas estándar) y los textos se reconocen por
  OCR (Tesseract) como TextNotes. Las imágenes importadas en Revit se pueden explotar aunque falte el archivo original.
- Funciona mejor con planos limpios (fondo claro, buena resolución); fotos, degradados o escaneos borrosos dan un
  resultado aproximado.

## Cambios de la versión 1.13

### Nuevo grupo PDF Tools
- **Explotar PDF Actual**: explota los PDF insertados en la vista actual (o los seleccionados) en su lugar, en la
  misma posición y tamaño que la imagen. Si no hay ninguno, permite elegir un archivo PDF e importarlo:
  páginas (todas o un rango), escala del dibujo (se detecta sola si el PDF dice "ESC 1:50", "ESCALA 1/100"…) y
  destino (una **vista de dibujo nueva por página**, a tamaño real y con la escala del dibujo, o la vista actual).
- **Explotar Varios PDF's**: la misma ventana de lista que para los DWG, con los PDF del modelo (archivo, página,
  vista, estado del archivo) y los botones **Agregar PDF…** (importar varios archivos a la vez), **Explotar…**,
  Seleccionar, Ubicar, Eliminar y Actualizar.
- Qué se convierte (lectura con [PdfPig](https://github.com/UglyToad/PdfPig), Apache 2.0):
  - trazos → Detail Lines con Line Styles `PDF R-G-B 0.25mm` (color, grosor → pluma, patrón de trazos del PDF);
    los círculos y arcos se detectan y se crean como arcos reales; el resto de curvas, como splines;
  - rellenos → Filled Regions sólidas con su color (se ignora el fondo blanco de la página);
  - textos → TextNotes (líneas de texto agrupadas, tamaño, fuente, negrita y rotación).
  - Las imágenes raster dentro del PDF (y los PDF escaneados) no son dibujo vectorial y no se convierten.
- Si el PDF insertado ya no está en su ruta, se pide el archivo.

### Técnico
- `App` resuelve las DLL de apoyo (System.Memory…) desde la carpeta del addin, ya que ACadSharp y PdfPig
  se compilaron contra versiones distintas.

## Cambios de la versión 1.12

### Explotar más rápido y sin el aviso "contornos demasiado grandes"
- La reexportación a DWG (para leer los textos de CAD importados) ya no exporta la vista completa: se ocultan
  temporalmente los demás elementos y, si la vista lo permite, se recorta al área de los CAD. Es mucho más
  pequeña y rápida, y evita el aviso de Revit de contornos demasiado grandes. Todo se deshace después.
- Una sola exportación por vista (antes, si el CAD no tenía textos, se exportaba dos veces).
- El OCR (lento) solo se usa si la exportación falla; si funcionó y no hay textos, el CAD no tiene textos.
- La imagen para leer colores se hace **una vez por vista** (no una por CAD) y **solo si algún CAD tiene
  rellenos**; resolución algo menor.
- En **Explotar Varios DWG's**, una vista con problemas ya no detiene el lote: se sigue con las demás y el
  resumen indica cuáles fallaron (se pueden explotar luego con "Explotar en Vista Actual"). Los avisos
  modales de Revit durante el lote se cancelan automáticamente para no quedar esperando.

## Cambios de la versión 1.11

### Nombres de los comandos
- **Explotar DWGs** → **Explotar en Vista Actual**.
- **Buscador DWG's** → **Explotar Varios DWG's**.

### Explotar Varios DWG's
- Nuevo botón **Explotar…**: explota en lote los DWG seleccionados en la lista. Cada DWG se explota en su
  vista (la propia si es "solo en su vista"; si es de modelo, la vista activa si lo muestra o una planta de su
  nivel), con una transacción por vista y un único resumen al final. Opciones: ajustar la escala de la vista
  si los textos son demasiado pequeños (por defecto sí) y eliminar los DWG originales después de explotarlos.
  Las imágenes de colores y OCR se exportan de la vista indicada, sin tener que abrirla.
- Columnas **Nivel, Estado, Fijado e Id ocultas** por defecto; se muestran con la casilla **Más datos**.
- **Eliminar** quita también el archivo DWG del proyecto cuando no le quedan instancias, para que no quede
  en la lista como "(sin instancias)".
- **Actualizar** vuelve a leer la lista y redibuja la vista activa.

## Cambios de la versión 1.10

### Buscador DWG's más rápido
- La columna **Vista** se calculaba revisando la visibilidad de **cada vista del modelo** (y se repetía en
  cada Actualizar / Eliminar), lo que en modelos grandes tardaba mucho. Ahora se obtiene sin calcular
  visibilidades: la vista propia del CAD si es "solo en su vista", o las **plantas de su nivel** si es de
  modelo. La lista sale casi al instante aunque el modelo tenga cientos de vistas.
- **Ubicar** solo comprueba las plantas del nivel del CAD en lugar de recorrer todas las vistas.

## Cambios de la versión 1.9

### Colores de los rellenos (equipos que salían en blanco)
- En la 1.8 la lectura de colores desde la imagen de la vista pasó a mandar y dejó todo en blanco: la imagen
  quedaba desalineada porque incluía cotas, textos y otros elementos fuera del CAD. Ahora:
  - durante la captura se **ocultan temporalmente** todos los demás elementos de la vista (y se activa el
    recorte de anotaciones), así la imagen es solo el CAD; todo se deshace después;
  - si la imagen no tiene las proporciones del área del CAD, se descarta;
  - la imagen **ya no manda**: primero se comprueba que lo leído coincide con los colores conocidos (material
    del relleno o color del DWG) en al menos el 60 % de los casos; si no, se ignora.
- Orden de prioridad del color de un relleno de Revit: **material** (color real, incluido el blanco de las
  máscaras) → imagen (si es fiable) → color habitual de la capa → color de la capa.

### Instalador
- Al terminar de instalar/actualizar/desinstalar aparece una ventana de confirmación con el botón **Cerrar**,
  que cierra también el instalador.
- Se quitó el mensaje "0 archivo(s) desbloqueado(s)" (los archivos que copia el instalador nunca quedan
  bloqueados); ahora indica que todos los archivos quedaron listos.

## Cambios de la versión 1.8

### Rellenos (hatch) negros donde el DWG tenía máscaras blancas
- Los hatch **blancos** del DWG (usados como máscara para tapar parte de otros rellenos) se convertían en
  negros: el addin pasaba todo blanco a negro y, al leer el color de la imagen, ignoraba el blanco como si
  fuera el fondo. Ahora el blanco real se conserva (solo el color 7 de AutoCAD, que en papel es negro, se
  imprime negro) y el blanco leído de la imagen cuenta como color del relleno.
- Los hatch se crean en el **orden de dibujo del DWG** (traer al frente / enviar al fondo), así cada máscara
  o relleno queda encima o debajo de los demás igual que en AutoCAD.

### Buscador DWG's
- Nueva columna **Vista**: la vista propia del CAD o, si es de modelo, todas las vistas donde se ve.
  La columna *Ubicación* indica si es "Solo en su vista" o "Modelo". El buscador de texto también filtra por vista.
- **Ubicar** abre una vista donde se vea el CAD si no se ve en la vista activa.

## Cambios de la versión 1.7

### Nuevo: Buscador DWG's
- Ventana (no bloquea Revit) con **todos los CAD del modelo**: instancias vinculadas e importadas, y archivos
  que quedaron en el proyecto **sin instancias**. Columnas: archivo, tipo, ubicación (vista propia o modelo),
  nivel, estado del vínculo (cargado / no encontrado / descargado), fijado, Id y ruta.
- Búsqueda por texto, filtro (todos / vinculados / importados / sin instancias) y orden por columna.
- **Seleccionar**: selecciona en Revit los CAD marcados.
- **Ubicar** (o doble clic): abre la vista del CAD si solo está en una vista, lo selecciona y hace zoom.
- **Eliminar…**: elimina las instancias marcadas (desfija las fijadas); opcionalmente también el archivo del
  proyecto (vínculo/importación) si no le quedan instancias. Se puede deshacer con Ctrl+Z.

### Color de los hatch
- El color de los rellenos sólidos ahora se **lee de lo que Revit muestra**: antes de explotar se exporta una
  imagen de la zona del CAD y se toma el color en un punto interior de cada relleno, elegido fuera de otros
  rellenos que lo tapen. Así el color es correcto aunque los rellenos se solapen o Revit los agrupe sin
  material. Si no se puede leer, se usa como antes el material, el color habitual de la capa, etc.

## Cambios de la versión 1.6

- **Rellenos que salían negros al solaparse**: Revit junta en una sola malla varios rellenos que se solapan y
  esa malla no trae color; ahora la malla se separa en sus piezas conectadas (cada relleno con su propio
  contorno) y, si una pieza no trae color, toma el color de los demás rellenos de su misma capa en vez del
  color de la capa (que suele ser negro/blanco).
- Addin renombrado a **EMASY**: pestaña **EMASY**, grupo **DWG Tools**, manifiesto `EMASY.addin` y carpeta
  `EMASY-2024/`.
- **Icono** del botón (32 px y 16 px): círculo gris con una hoja de plano "explotada" en trazo blanco, en el
  estilo del logo. Se generan con `tools/make_icons.py` y van incrustados en la DLL.

## Cambios de la versión 1.5

### Hatch que no se convertían
- **Rellenos sólidos de CAD importados** (p.ej. los amarillos): Revit los entrega como láminas planas, no
  solo como mallas; ahora ambas se convierten en Filled Region sólido, con el color del material del relleno
  (el color real del hatch) o, si no tiene, el de su capa. Si la lámina tiene líneas de patrón dentro, no se
  rellena en sólido (es un hatch con patrón).
- **Hatch con patrón sin el DWG original**: se leen del DWG que el addin ya exporta de la vista (con su
  patrón, separación y color), filtrados por las capas y el área del CAD.
- **Plano de la vista**: la región se prueba en el plano de trabajo de la vista, en el del nivel, en el del
  origen de la vista y en la cota original del DWG, hasta que Revit la acepta (antes, si el contorno no
  estaba exactamente en el plano esperado, la región se descartaba).
- **Unidades del DWG**: si el DWG declara unas unidades pero se insertó en Revit con otras (p.ej. "sin
  unidades" importado en metros), se detectan las unidades reales comparando la geometría con la instancia;
  así hatch, tipos de línea y textos caen en su sitio.
- **Hatch de doble línea**: hatch definidos por el usuario con la opción *Doble* que traen una sola familia de
  líneas ahora generan también la familia cruzada a 90°.
- Un relleno de Revit solo se descarta si ya lo cubre un hatch convertido (antes se descartaban todos los de
  la misma capa, aunque su hatch no se hubiera podido convertir).

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
