# EMASY · Partidas (Revit 2024)

Addin hermano de *EMASY DWG Tools* y *EMASY Parámetros*. Está en la pestaña **EMASY**, panel **Partidas**, botón **Partidas DYCUSA**.

Compara los códigos de partida que ya tiene el modelo con el presupuesto (Excel) y los unifica en un parámetro por nivel de la codificación. Por defecto son **DYCUSA_PARTIDA 1 … 6**:

| Parámetro | `03.01.04.02.01.01` | `03.01.01.01.01` |
|---|---|---|
| DYCUSA_PARTIDA 1 | `03` (ARQUITECTURA) | `03` |
| DYCUSA_PARTIDA 2 | `03.01` (ETAPA 1) | `03.01` |
| DYCUSA_PARTIDA 3 | `03.01.04` | `03.01.01` |
| DYCUSA_PARTIDA 4 | `03.01.04.02` | `03.01.01.01` |
| DYCUSA_PARTIDA 5 | `03.01.04.02.01` | `03.01.01.01.01` |
| DYCUSA_PARTIDA 6 | `03.01.04.02.01.01` | *(vacío)* |

Solo se guardan códigos. Las descripciones quedan para una fase posterior.

## Los 4 pasos (pestañas de la ventana)

### 1 · Jerarquía
* **Cargar presupuesto…**: el addin busca en cualquier hoja la fila con *Item / Descripción / Und.*. Una fila con unidad es una partida final; una fila sin unidad es un título. También lee *Metrado* y *Tabla BIM / Observación*.
* **Número de niveles** (órdenes): se detecta del presupuesto (6 en este caso) y se puede cambiar (de 1 a 8). Una tabla muestra un ejemplo de cada nivel con su descripción.

### 2 · Parámetros
* **Prefijo** del nombre: `DYCUSA_PARTIDA ` da DYCUSA_PARTIDA 1…6. Con `DYCUSA_` y "dos cifras" da DYCUSA_01…06.
* **Crear / actualizar parámetros en el modelo**: crea parámetros compartidos con estas características:
  - de texto y de ejemplar, en *Datos de identidad*;
  - en todas las categorías de modelo;
  - con valores que pueden variar entre grupos.

  El GUID depende del nombre, así que el mismo nombre es el mismo parámetro en todos los modelos. El archivo de parámetros compartidos queda en `%AppData%\EMASY\DYCUSA_Parametros.txt`.
* La tabla indica qué parámetros ya existen en el modelo.

### 3 · Enlaces
Indica de dónde sale cada nivel:
* **Del código de partida** (lo normal).
* **Valor fijo**: por ejemplo, nivel 1 = `03` (ARQUITECTURA) para todo el modelo.
* **Parámetro de Revit**: uno cuyo valor es la descripción del nivel. Por ejemplo, nivel 2 desde `PR_Elemento_Etapa`, donde "ETAPA 1" se convierte en `03.01` y "ETAPA 2" en `03.02`.

**Detectar enlaces automáticamente** propone el valor fijo del nivel 1 y busca en el modelo el parámetro cuyos valores coinciden con las descripciones de los niveles 2 y 3.

Los niveles enlazados sirven para tres cosas:
* **Filtrar** los códigos que no corresponden. Un código de ETAPA 2 en un elemento de ETAPA 1 se descarta; si es el único que tiene, el elemento queda como *No coincide*.
* **Ordenar** los elementos sin partida: si tienen la etapa, se escriben al menos los niveles 1 y 2.
* **Avisar** cuando un valor (p. ej. "ETAPA 3") no está en el presupuesto.

También se configuran aquí las palabras que identifican los parámetros donde ya hay códigos (`partida, item`). Con eso se reconocen, por ejemplo, `DOC_N° PARTIDA 1`, `DO_PARTIDA 5`, `ITEM` o `DO_E1_Item`. No cuentan los que llevan `nom`, `unidad`, `alterno` o `descrip`.

### 4 · Revisión
**Cómo se elige el código de cada elemento.** Se busca por prioridad:
1. parámetros de **ejemplar**;
2. si no resuelven, parámetros de **tipo**;
3. si tampoco, los de sus **materiales**.

Así un tipo compartido entre etapas no genera conflictos falsos. Dentro de un mismo nivel de prioridad se descartan los códigos que no coinciden con los enlaces y se prefieren las partidas finales del presupuesto.

**La lista de la derecha** es compacta y se expande en tres niveles: **Categoría → grupo (estado + nota) → elementos**. Los elementos se cargan al expandir el grupo, así la lista sale rápido aunque haya decenas de miles. Abajo se ve el detalle del grupo o del elemento: sus opciones con descripción, los niveles escritos, los propuestos y los enlazados.

* **Casillas**: marca categorías, grupos o elementos; las acciones se aplican a lo marcado. Si no hay nada marcado, se aplican al nodo seleccionado.
* **Escribir pendientes**: escribe los *Pendientes* y los *Solo niveles superiores* (todos, o solo los marcados). Después de escribir, la lista se actualiza sin volver a analizar el modelo.
* **Asignar esta partida a los marcados**: marca la partida en el presupuesto (izquierda) y los elementos o grupos a la derecha, y pulsa el botón.
* **Resolver en 3D…**: para grupos de *Conflicto* (o *No coincide*). Abre la vista 3D `EMASY - Revisión partidas` con:
  - los elementos en conflicto en **rojo**;
  - elementos que ya tienen cada opción en **azul, verde, naranja o morado**, preferentemente de los mismos niveles;
  - todo lo demás aislado temporalmente.

  Una ventana pequeña muestra la leyenda. Para decidir, pulsas el botón del color correcto o **"Elegir con un clic en el modelo"** y haces clic en un elemento de ese color. Se asigna a todo el grupo en conflicto y se quitan los colores. Con *Cerrar sin decidir* también se quitan.
* **Usar selección de Revit**: muestra y marca lo que tienes seleccionado en Revit.
* **Seleccionar en Revit** (o doble clic en un elemento): selecciona y muestra.
* **Vaciar parámetros**: deja en blanco los niveles de lo marcado.
* **Exportar CSV…** genera dos archivos (separador `;`, UTF-8 con BOM):
  - `… - cobertura.csv`: por partida, el metrado del presupuesto frente al nº de elementos y la cantidad BIM;
  - `… - elementos.csv`: por elemento, con su grupo, opciones y niveles escritos y propuestos.

### Estados

| Estado | Significado |
|---|---|
| **Conflicto** | Varias partidas finales posibles en el mismo nivel de prioridad → *Resolver en 3D* |
| **No coincide con el enlace** | Su código es de otra rama (p. ej. ETAPA 2 en un elemento de ETAPA 1) |
| **No está en el presupuesto** | El código no existe en el presupuesto |
| **Es título** | El código es un título, no una partida final |
| **Difiere de lo escrito** | Lo escrito y el origen no coinciden (en el paso 3 se elige cuál manda) |
| **Pendiente** | Hay propuesta y falta escribirla |
| **Solo niveles superiores** | Sin partida, pero la etapa da los niveles 1–2: se pueden escribir ya |
| **Sin partida** | Ningún dato. Solo se listan las categorías donde otros elementos sí tienen partida (salvo en *Selección de Revit*) |
| **OK** | Niveles escritos y la partida existe en el presupuesto |

Todas las escrituras se hacen en una transacción y se deshacen con Ctrl+Z. Los errores se muestran una sola vez, en la barra de estado.

## Limitaciones

* No se leen los elementos de modelos vinculados.
* La cantidad BIM del CSV es orientativa: usa el área, la longitud o el volumen del elemento completo, no por capa.
* Para saber qué parámetros de ejemplar tiene un elemento, se mira el primer elemento de su tipo, ya que todos los del mismo tipo tienen los mismos parámetros. Lo mismo vale para los materiales, que solo se consultan cuando el elemento y su tipo no tienen código.

## Compilar

```bash
./build-zip.sh      # dist/EMASY-Partidas-2024.zip y dist/EMASY-Partidas-2024-Setup.exe
```

Requiere el SDK de .NET (net48 / Revit 2024). El instalador copia `EMASY-Partidas.addin` y `EMASY-Partidas-2024/` en `%AppData%\Autodesk\Revit\Addins\2024` y no toca los demás addins de EMASY.
