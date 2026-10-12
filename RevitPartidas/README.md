# EMASY · Partidas DYCUSA (Revit 2024)

Addin hermano de *EMASY DWG Tools* y *EMASY Parámetros*. Se encuentra en la pestaña **EMASY**, panel **Partidas**, botón **Partidas DYCUSA**.

Compara los códigos de partida que ya tiene el modelo con el presupuesto en Excel y los unifica en 6 parámetros nuevos, uno por nivel del código:

| Parámetro | Ejemplo (`03.01.04.02.01.01`) | Ejemplo (`03.01.01.01.01`) |
|---|---|---|
| DYCUSA_PARTIDA 1 | `03` | `03` |
| DYCUSA_PARTIDA 2 | `03.01` | `03.01` |
| DYCUSA_PARTIDA 3 | `03.01.04` | `03.01.01` |
| DYCUSA_PARTIDA 4 | `03.01.04.02` | `03.01.01.01` |
| DYCUSA_PARTIDA 5 | `03.01.04.02.01` | `03.01.01.01.01` |
| DYCUSA_PARTIDA 6 | `03.01.04.02.01.01` | *(vacío: no aplica)* |

Solo se guarda el código; las descripciones quedan para una fase posterior. Basta con el código más profundo para completar los niveles superiores. Por eso, si un elemento solo tiene la PARTIDA 6 (o la 5), el addin propone completar el resto.

## Flujo de trabajo

1. **Cargar presupuesto…**: elige el Excel del presupuesto (por ejemplo `PPTO_ARQUITECTURA_Comparativo_BIM.xlsx`). El addin busca en cualquier hoja la fila con *Item / Descripción / Und.*. Una fila con unidad es una partida final; una fila sin unidad es un título. También lee *Metrado* y *Tabla BIM / Observación*. La ruta queda guardada para la próxima vez.
2. **Crear parámetros DYCUSA**: crea los 6 parámetros compartidos con estas características:
   - de texto y de ejemplar, en *Datos de identidad*;
   - con GUID fijos, así que son los mismos en todos los modelos;
   - en todas las categorías de modelo;
   - con valores que pueden variar entre grupos.

   El archivo de parámetros compartidos queda en `%AppData%\EMASY\DYCUSA_Parametros.txt` (sirve para etiquetas y familias). No se cambia el archivo de parámetros compartidos que tengas configurado.
3. **Analizar modelo**: para cada elemento, busca códigos en los **parámetros de origen**, es decir, los que en su nombre contienen `partida` o `item`, como `DOC_N° PARTIDA 1`, `DO_PARTIDA 5`, `DO_Partida_1`, `ITEM`, `DO_E1_Item` o `DO_Código_Item`. No cuentan los que llevan `nom`, `unidad`, `alterno` o `descrip`. Las palabras clave se pueden editar. El orden de búsqueda es:
   1. parámetros de ejemplar;
   2. parámetros de tipo;
   3. solo si los anteriores no tienen nada, los parámetros de sus materiales.

   Solo valen los códigos con forma de partida, de 2 a 6 niveles (`03.01…`). Los códigos de especificación `OE.x.x` se ignoran.
4. Revisa la tabla (filtro **Por revisar** por defecto) y corrige.
5. **Escribir pendientes**: escribe la propuesta en todos los elementos *Pendientes*. Si hay filas seleccionadas, escribe solo en ellas.

### Estados

| Estado | Significado | Qué hacer |
|---|---|---|
| **Conflicto** | El elemento tiene varias partidas finales distintas (p. ej. un piso con contrapiso y piso) | Asignar la correcta desde el árbol |
| **No está en ppto.** | El código no existe en el presupuesto | Corregir el parámetro de origen o asignar desde el árbol |
| **Es título** | El código es un título, no una partida final | Asignar la partida final |
| **Difiere** | Ya tiene DYCUSA, pero los parámetros de origen dicen otra cosa | Revisar; la casilla *"Los parámetros de origen mandan…"* invierte la prioridad |
| **Pendiente** | Hay propuesta, pero aún no está escrita (o está incompleta) | *Escribir pendientes* |
| **Sin partida** | Ningún parámetro tiene código (solo se listan las categorías donde otros elementos sí lo tienen) | Asignar desde el árbol |
| **OK** | DYCUSA 1–6 completos y la partida existe en el presupuesto | — |

### Árbol del presupuesto (izquierda)

* Entre corchetes aparece el nº de elementos de cada partida o título.
* Los colores de las partidas finales significan:
  - **verde**: tiene elementos;
  - **rojo**: no tiene elementos;
  - **gris**: no tiene elementos y el comparativo ya indica *"Sin tabla en el modelo BIM"*.
* **Solo partidas sin elementos**: muestra lo que falta modelar o codificar.
* **Doble clic** en una partida: la tabla muestra sus elementos.
* **Asignar partida a N elemento(s)**: marca la partida en el árbol, selecciona los elementos en la tabla y pulsa el botón. Para usar lo que tienes seleccionado en la vista, pulsa *Usar selección de Revit* y la tabla mostrará esa selección. Se escriben los 6 niveles.

### Otros botones

* **Seleccionar en Revit** (o doble clic en una fila): selecciona y muestra los elementos.
* **Limpiar DYCUSA**: vacía los 6 parámetros de los elementos seleccionados.
* **Exportar CSV…** genera dos archivos (separador `;`, UTF-8 con BOM):
  - `… - cobertura.csv`: por partida, el metrado del presupuesto frente al nº de elementos y la cantidad BIM, sumada según la unidad (m² → área, m → longitud, m³ → volumen, und/pza/glb → cantidad), con la diferencia;
  - `… - elementos.csv`: todos los elementos con estado, origen, propuesta y cantidades.

Todas las escrituras se hacen en una transacción y se deshacen con Ctrl+Z.

## Limitaciones

* No se leen los elementos de modelos vinculados (los parámetros se escriben en el modelo que los contiene).
* La cantidad BIM del CSV es orientativa: usa el área, la longitud o el volumen calculados por Revit para el elemento completo, no por capa.
* Si un parámetro de origen guarda varios códigos en un mismo texto, no se reconoce: debe tener un código por parámetro.

## Compilar

```bash
./build-zip.sh      # dist/EMASY-Partidas-2024.zip y dist/EMASY-Partidas-2024-Setup.exe
```

Requiere el SDK de .NET (net48 / Revit 2024). El instalador copia `EMASY-Partidas.addin` y `EMASY-Partidas-2024/` en `%AppData%\Autodesk\Revit\Addins\2024` y no toca los demás addins de EMASY.
