# EMASY · Partidas (Revit 2024)

Addin hermano de *EMASY DWG Tools* y *EMASY Parámetros*. Está en la pestaña **EMASY**, panel **Partidas**, botón **Partidas DYCUSA**.

Lee la estructura del presupuesto y la carga como tabla de **Código de montaje y Descripción de montaje**. Después asocia cada tipo a su partida: usa la **etapa** (u otro parámetro) como filtro y revisa toda la metadata (todos los parámetros y los nombres) para sugerirla. Opcionalmente, en el paso 4, guarda la partida en parámetros de nivel por elemento (**DYCUSA_PARTIDA 1 … 6**):

| Parámetro | `03.01.04.02.01.01` | `03.01.01.01.01` |
|---|---|---|
| DYCUSA_PARTIDA 1 | `03` (ARQUITECTURA, fijo) | `03` |
| DYCUSA_PARTIDA 2 | `03.01` (ETAPA 1, del filtro) | `03.01` |
| DYCUSA_PARTIDA 3 | `03.01.04` | `03.01.01` |
| DYCUSA_PARTIDA 4 | `03.01.04.02` | `03.01.01.01` |
| DYCUSA_PARTIDA 5 | `03.01.04.02.01` | `03.01.01.01.01` |
| DYCUSA_PARTIDA 6 | `03.01.04.02.01.01` | *(vacío)* |

## Paso 1 · Código de montaje
* **Cargar presupuesto…**: el addin lee su estructura en cualquier hoja con *Item / Descripción / Und.*. Una fila con unidad es una partida final; una fila sin unidad es un título. Los **niveles** (6) se detectan del presupuesto.
* **Generar y cargar Código de montaje y Descripción de montaje**:
  - escribe la tabla con todos los códigos y descripciones del presupuesto (`%AppData%\EMASY\Codigos de montaje - <presupuesto>.txt`);
  - la carga en el modelo, reemplazando la tabla actual;
  - después **comprueba que cada código quedó bajo su padre**. Revit acepta menos niveles en el código de montaje que los 6 del presupuesto: si deja códigos fuera de su rama, los más profundos se vuelven a escribir junto a su partida de nivel 5, dentro de la misma rama, y se recarga. El mensaje indica cuántos niveles admite y cuántos códigos se ajustaron.
  - Los códigos ya escritos en los tipos no se borran.

## Paso 2 · Filtros
Para cada nivel se elige su origen:
* **Valor fijo**: por ejemplo, nivel 1 = `03`, porque todo es ARQUITECTURA.
* **Parámetro de Revit** (parámetro de regla): por ejemplo, nivel 2 desde `PR_Elemento_Etapa`. **Leer valores** lista los valores del modelo y su número de elementos. En la **regla** eliges el código de cada valor: automático por descripción ("ETAPA 1" → 03.01), un código concreto, o ignorarlo.
* **Múltiple**: el nivel sale de revisar **toda la metadata** del elemento. Primero el Código de montaje y, si falta, todos los parámetros de ejemplar y de tipo, más los nombres.

El filtro decide la rama. Si el código de montaje de un tipo es de la otra etapa, se usa la partida equivalente (misma descripción y unidad) de la etapa del elemento.

## Paso 3 · Revisión
**Qué partida se le asigna a cada elemento:**
1. El **Código de montaje** de su tipo, ajustado a la etapa. Si es un título, se sugiere por nombre la partida dentro de él.
2. Si falta, se reúne **toda la metadata** del elemento:
   - cada parámetro de ejemplar o de tipo que tiene un código del presupuesto es un **voto** para esa partida;
   - un código de título suma a su partida;
   - un código de la otra etapa suma a su equivalente;
   - cada texto (familia, tipo, valores de parámetros como `DOC_NOM_PARTIDA`) suma su **parecido** de nombre.

   Gana la partida con más respaldo. Si otra queda cerca, el elemento queda como *Conflicto*. El grupo muestra todo lo que respalda la sugerencia (p. ej. "código en DYCUSA_Partida_05, DO_Partida_5 · nombre 91 % (DOC_NOM_PARTIDA)").

**Al seleccionar** un grupo o elemento, su partida (la sugerida, o la de su código de montaje) **se marca en el árbol del presupuesto**. Así ves dónde cae y puedes cambiarla por otra con *Asignar esta partida a los marcados*.

**Botones del paso 3** (todos asocian la partida al **Código de montaje** del tipo; la Descripción de montaje sale de la tabla del paso 1):
* **Aceptar sugerencia seleccionada**: solo el grupo o elemento seleccionado (o los marcados).
* **Aceptar todas**: todas las sugerencias.
* **Enlazar…** (o doble clic): busca la partida por código o por nombre, viendo el Código y la Descripción de montaje, el filtro y las sugerencias.
* **Resolver en 3D…**: pinta los elementos con varias opciones; eliges con un botón o un clic en el modelo.

En el tipo va el código de la etapa más frecuente; los elementos de la otra etapa leen su equivalente. Antes de escribir se avisa si otros elementos del mismo tipo cambiarán.

También están **Usar selección de Revit**, **Seleccionar en Revit** y **Exportar CSV…**.

## Paso 4 · Parámetros de nivel (opcional)
* Elige el prefijo (`DYCUSA_PARTIDA ` da DYCUSA_PARTIDA 1…6; con dos cifras da DYCUSA_01…06) y pulsa **Crear / actualizar parámetros**. Son de ejemplar, así cada elemento guarda la partida de su etapa.
* **Escribir niveles en los pendientes** los llena desde la partida de cada elemento.
* **Vaciar niveles** los borra de lo marcado en la revisión.
* Si existen en el modelo, las asignaciones del paso 3 también los escriben.
* Sin estos parámetros, un elemento con código de montaje válido ya queda **OK**.

### Estados

| Estado | Significado |
|---|---|
| **Conflicto** | Varias partidas posibles (códigos distintos en parámetros, o nombres con el mismo parecido) |
| **Sugerencia (confirmar)** | Sin código de montaje; un parámetro o el nombre apunta a una partida |
| **No coincide con el filtro** | El código es de otra rama y no tiene equivalente con la misma descripción |
| **No está en el presupuesto** | El código de montaje no existe en el presupuesto |
| **Es título** | El código de montaje es un título y no hay sugerencia dentro de él |
| **Difiere de lo escrito** | Lo escrito en los niveles y el código de montaje no coinciden |
| **Pendiente** | Partida clara; faltan los parámetros de nivel (solo si existen, paso 4) |
| **Solo niveles superiores** | Sin partida, pero el filtro da los niveles 1–2 (con parámetros de nivel) |
| **Sin partida** | Nada apunta a una partida |
| **OK** | Niveles escritos y coherentes con el presupuesto |

Todas las escrituras se hacen en una transacción y se deshacen con Ctrl+Z.

## Limitaciones

* El Código de montaje es de tipo: si un tipo se usa para partidas distintas (no solo por etapa), conviene duplicar el tipo; los parámetros de nivel (paso 4) sí guardan la partida por elemento.
* No se leen los elementos de modelos vinculados.
* La cantidad BIM del CSV es orientativa: usa el área, la longitud o el volumen del elemento completo.

## Compilar

```bash
./build-zip.sh      # dist/EMASY-Partidas-2024.zip y dist/EMASY-Partidas-2024-Setup.exe
```

Requiere el SDK de .NET (net48 / Revit 2024). El instalador copia `EMASY-Partidas.addin` y `EMASY-Partidas-2024/` en `%AppData%\Autodesk\Revit\Addins\2024`.
