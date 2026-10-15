# EMASY · Partidas (Revit 2024)

Addin hermano de *EMASY DWG Tools* y *EMASY Parámetros*. Está en la pestaña **EMASY**, panel **Partidas**, botón **Partidas DYCUSA**.

El **Código de montaje** (parámetro de tipo de Revit) dice la partida del presupuesto. El addin llena con él los parámetros de nivel **DYCUSA_PARTIDA 1 … 6**. Usa la **etapa** (u otro parámetro) como filtro, y revisa el resto de parámetros y los nombres para sugerir la partida cuando falta el código.

| Parámetro | `03.01.04.02.01.01` | `03.01.01.01.01` |
|---|---|---|
| DYCUSA_PARTIDA 1 | `03` (ARQUITECTURA, fijo) | `03` |
| DYCUSA_PARTIDA 2 | `03.01` (ETAPA 1, del filtro) | `03.01` |
| DYCUSA_PARTIDA 3 | `03.01.04` | `03.01.01` |
| DYCUSA_PARTIDA 4 | `03.01.04.02` | `03.01.01.01` |
| DYCUSA_PARTIDA 5 | `03.01.04.02.01` | `03.01.01.01.01` |
| DYCUSA_PARTIDA 6 | `03.01.04.02.01.01` | *(vacío)* |

## Paso 1 · Código de montaje
* **Cargar presupuesto…**: el addin busca en cualquier hoja la fila con *Item / Descripción / Und.*. Una fila con unidad es una partida final; una fila sin unidad es un título.
* **Niveles**: se detectan del presupuesto (6). La tabla muestra un ejemplo de cada nivel.
* **Parámetros de nivel**: se elige el prefijo (`DYCUSA_PARTIDA ` da DYCUSA_PARTIDA 1…6; con "dos cifras" da DYCUSA_01…06) y se crean con **Crear / actualizar**. Son compartidos, de texto, de ejemplar, en todas las categorías de modelo y pueden variar entre grupos.
* **Generar y cargar códigos de montaje del presupuesto**:
  - escribe la tabla de códigos de montaje con todos los códigos y descripciones del presupuesto en `%AppData%\EMASY\Codigos de montaje - <presupuesto>.txt`;
  - la carga en el modelo, reemplazando la tabla actual (p. ej. Uniformat);
  - desde entonces, en las propiedades de tipo, el *Código de montaje* ofrece las partidas y la *Descripción de montaje* muestra su nombre;
  - los códigos ya escritos en los tipos no se borran.

## Paso 2 · Filtros
Para cada nivel se elige su origen:
* **Del código de montaje** (lo normal).
* **Valor fijo**: por ejemplo, nivel 1 = `03`, porque todo es ARQUITECTURA.
* **Parámetro de Revit** (parámetro de regla): por ejemplo, nivel 2 desde `PR_Elemento_Etapa`.

**Leer valores de los parámetros del filtro** lista los valores que tiene el modelo y cuántos elementos tienen cada uno. En la **regla** eliges qué código corresponde a cada valor:
* *automático*: el código cuya descripción del presupuesto es igual al valor ("ETAPA 1" → 03.01);
* un código concreto;
* *ignorar este valor*.

Los valores sin coincidencia aparecen en naranja. **Detectar filtros automáticamente** propone el nivel 1 fijo y el parámetro de etapa.

El filtro decide la rama de cada elemento. El Código de montaje es de **tipo** y un mismo tipo puede estar en las dos etapas; por eso, si su código es de la otra etapa, se usa la **partida equivalente** (misma descripción y unidad) de la etapa del elemento. Si no hay equivalente, se avisa.

La casilla *El código de montaje manda* (activada por defecto) hace que el código de montaje prevalezca sobre lo ya escrito en los niveles.

## Paso 3 · Revisión
**Cómo se decide la partida de cada elemento:**
1. El **Código de montaje** de su tipo, ajustado a la etapa. Si es un título (p. ej. 03.01.11 OTROS), se sugiere por nombre la partida dentro de ese título.
2. Si no hay código de montaje válido, se leen **todos los parámetros de ejemplar y de tipo**:
   - un **código del presupuesto** escrito en cualquiera de ellos (p. ej. `DOC_N° PARTIDA 1 = 03.01.07.01.02`) se **sugiere**;
   - si es de la otra etapa, se sugiere su equivalente.
3. Si tampoco hay, se compara por **nombre**: familia, tipo y valores de texto de los parámetros (p. ej. `DOC_NOM_PARTIDA`) contra las descripciones del presupuesto de la rama del filtro. La comparación ignora tildes, plurales y palabras vacías, y da un porcentaje de parecido. Por ejemplo, la familia "Canaleta pluvial metálica" lleva a 03.02.13.07 CANALETA PLUVIAL METÁLICA… en ETAPA 2.

Las sugerencias **no se escriben solas**: aparecen como *Sugerencia (confirmar)* o, si hay empate, como *Conflicto*.

**La lista** se agrupa por **Categoría → estado y nota → elementos** y se expande al hacer clic. Abajo se ve el detalle: Código y Descripción de montaje, filtro, partida y sugerencias con su motivo.

* **Doble clic** en un grupo o elemento, o **Enlazar…**, abre la ventana de enlace:
  - muestra el Código y la Descripción de montaje, el filtro y las sugerencias;
  - permite buscar la partida **por código o por nombre**, dentro de la etapa del elemento o en todo el presupuesto;
  - **Asignar esta partida** escribe los niveles y, con la casilla *Guardar también como Código de montaje del tipo*, también el código de montaje del tipo. Se avisa si otros elementos del mismo tipo también lo tomarán; los de la otra etapa usan su equivalente.
* **Aceptar sugerencias**: confirma la sugerencia de todos los elementos con *Sugerencia* (o solo de los marcados).
* **Escribir pendientes**: escribe los niveles de los que ya tienen partida clara por su código de montaje, y los niveles 1–2 de los que solo tienen filtro.
* **Resolver en 3D…**: para conflictos y sugerencias. Pinta en rojo los elementos a decidir y en colores elementos que ya tienen cada opción; eliges con un botón o con un clic en el modelo.
* El presupuesto a la izquierda muestra cuántos elementos tiene cada partida. Con *Asignar esta partida a los marcados* se asigna directamente.
* **Usar selección de Revit**, **Seleccionar en Revit**, **Vaciar parámetros** y **Exportar CSV…** (cobertura por partida y elementos con su código de montaje).

### Estados

| Estado | Significado |
|---|---|
| **Conflicto** | Varias partidas posibles (códigos distintos en parámetros, o nombres con el mismo parecido) |
| **Sugerencia (confirmar)** | Sin código de montaje; un parámetro o el nombre apunta a una partida |
| **No coincide con el filtro** | El código es de otra rama y no tiene equivalente con la misma descripción |
| **No está en el presupuesto** | El código de montaje no existe en el presupuesto |
| **Es título** | El código de montaje es un título y no hay sugerencia dentro de él |
| **Difiere de lo escrito** | Lo escrito en los niveles y el código de montaje no coinciden |
| **Pendiente** | Partida clara por su código de montaje; falta escribir los niveles |
| **Solo niveles superiores** | Sin partida, pero el filtro da los niveles 1–2 |
| **Sin partida** | Nada apunta a una partida |
| **OK** | Niveles escritos y coherentes con el presupuesto |

Todas las escrituras se hacen en una transacción y se deshacen con Ctrl+Z.

## Limitaciones

* El Código de montaje es de tipo: si un tipo se usa para partidas distintas (no solo por etapa), conviene duplicar el tipo o asignar por elemento sin la casilla *Guardar también como Código de montaje*.
* No se leen los elementos de modelos vinculados.
* La cantidad BIM del CSV es orientativa: usa el área, la longitud o el volumen del elemento completo.

## Compilar

```bash
./build-zip.sh      # dist/EMASY-Partidas-2024.zip y dist/EMASY-Partidas-2024-Setup.exe
```

Requiere el SDK de .NET (net48 / Revit 2024). El instalador copia `EMASY-Partidas.addin` y `EMASY-Partidas-2024/` en `%AppData%\Autodesk\Revit\Addins\2024`.
