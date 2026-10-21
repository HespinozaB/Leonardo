# EMASY · Rellenar parámetros (Revit 2024)

Addin de la pestaña **EMASY**, panel **Rellenar**, botón **Rellenar parámetros**.

Llena una lista de parámetros (por ejemplo los de la **Tabla 5 de SUNAT**) en todos los elementos del modelo, cada uno según su regla:
* desde un dato que ya existe: categoría, familia, tipo, marca, material, nivel o cualquier parámetro;
* con un **valor fijo**;
* por **sectorización**, dibujada en una planta con **trazo de Revit** o con un **DWG**;
* **libre**, en modo **masivo** o **personalizado**.

Cuando falta el dato, **deduce** un valor con una lógica y explica el motivo. Los valores deducidos **no se escriben** hasta que los aceptes.

## 1 · Parámetros
Una fila por parámetro a llenar:

| Columna | Qué es |
|---|---|
| Parámetro destino | El parámetro que se llena; se autocompleta con los del modelo |
| Parámetro enlazado | De dónde sale el valor: Categoría · Familia · Nombre de tipo · Familia : Tipo · Marca · Marca de tipo · Fabricante · Modelo · Nivel · Material · Parámetro · Valor fijo · Sectorización · Libre · N/A |
| Contenido de parámetro | Solo para *Parámetro* (cuál), *Valor fijo* (el valor) y *Sectorización* (el conjunto de la hoja 3); en naranja si falta |
| Prefijo / Sufijo | Se agregan al valor |
| Sobrescribir | Si no se marca, se conservan los valores que el elemento ya tiene |
| En el modelo | ✓ existe · ✗ se creará |

* **+ Añadir parámetro / − Quitar parámetro**.
* **Crear contenido de parámetros →**: crea los parámetros que no existen, si confirmas, como compartidos de texto, de ejemplar y en todas las categorías de modelo. Luego **calcula** el contenido de todos los elementos y abre la hoja 2 para validarlo.
* **Barra inferior**:
  - **Configuración actual**;
  - **Cargar configuración de parámetros ▾**: desde un archivo o con el *Preajuste SUNAT (Tabla 5)*;
  - **Guardar configuración**: en el archivo actual, o pregunta dónde.

  La última configuración usada se recuerda sola.

Preajuste SUNAT:

| Nº | Parámetro | Parámetro enlazado |
|---|---|---|
| 1 | Categoría de Familia | Categoría |
| 2 | Tipo de Elemento | Tipo |
| 3 | Nivel de Elemento | Nivel |
| 4 | Material | Material |
| 5 | Código de Elemento | sectores "Local" |
| 6 | Código Sitio | N/A |
| 7 | Código Edificio | sectores "Edificio" |
| 8 | Código Unidad Operativa | Libre |
| 9 | Especialidad | Parámetro (indica cuál) |
| 10 | Sistema | Fijo "General" |

**Material**: primero un parámetro de material del ejemplar o del tipo; si no hay, la capa más gruesa de la estructura; si tampoco, el material con más área del elemento.

**Nivel**: el nivel del elemento; si no tiene, el nivel de planificación, el de la familia o la restricción de base.

## 2 · Validar contenido
Una **tabla por elemento**:
* columnas fijas: *Escribir* · Id · Categoría · Familia : Tipo · Nivel;
* después, **una columna por parámetro** con el valor que quedaría.

Cada celda tiene un color:
* **verde**: dato directo;
* **azul**: deducido (más intenso cuando ya lo aceptaste);
* **rojo**: sin dato;
* **gris**: ya lo tiene, o se conserva;
* **naranja**: bloqueado (solo lectura);
* **amarillo**: editado a mano;
* **·**: el elemento no tiene ese parámetro.

Al pasar el ratón por una celda se ven el valor actual, el nuevo y el motivo.

* **Escribir en una celda** la corrige: queda *editada a mano* y se escribe.
* **Desmarcar *Escribir*** en una fila la excluye.
* **Filtros**: categoría; mostrar todos, con deducidos, con sin dato o con cambios; buscar.
* **Aceptar deducidos de las filas seleccionadas** / **Aceptar todos los deducidos mostrados**.
* **Escribir en el modelo**: escribe los directos, los editados y los deducidos aceptados de las filas marcadas, en una transacción que se deshace con Ctrl+Z. Los parámetros de tipo se escriben una vez en el tipo.
* **Seleccionar en Revit** (o doble clic en el Id), **Exportar tabla (CSV)** y **Recalcular**.

### Deducción (cuando falta el dato)
Siempre activa, en este orden:
1. **Sectorización**: el sector más cercano, si el elemento queda fuera de todos.
2. **Nivel**: el nivel por la altura del elemento.
3. **Mismo tipo**: el valor más frecuente entre los elementos de su tipo.
4. **Elemento cercano**: el valor del elemento con dato más próximo, hasta 15 m, primero en su nivel.
5. **Nombre**: el parecido del nombre de familia o tipo con valores ya usados.

Los deducidos solo se escriben si los aceptas.

## 3 · Sectorización
Conjuntos de sectores con nombre, por ejemplo "Edificio" o "Local"; las reglas los usan por ese nombre. Para cada conjunto:
* **Trazo en planta**: en la vista elegida, las **regiones rellenas** (de un tipo o de todos) o los **lazos de líneas de detalle** (de un estilo o de todos).
* **DWG vinculado o importado** de esa vista: las **polilíneas cerradas** (o líneas que forman lazos) de una **capa**. **Leer capas** las lista. Si el DWG está importado (no vinculado), indica su archivo para leer los textos.
* **Nombre del sector**: el **texto que cae dentro** (nota de texto de Revit o texto del DWG; si hay varios, el más cercano al centro), el **tipo de región o capa**, o los **Comentarios** de la región.
* **Leer sectores** muestra la lista con su área. Los que no tienen nombre salen en rojo. **Ver en Revit** selecciona el trazo.
* Opción: **solo los elementos del nivel de la vista**. Si no se marca, el sector vale para todos los niveles.

Cada elemento cae en el sector que contiene su punto en planta: su punto de inserción, el medio de su línea o el centro de su caja. Si cae en varios sectores, gana el más pequeño.

## 4 · Libre
* **Masivo**: un parámetro y un valor para la **selección actual de Revit** (pulsa *Leer selección*), las **filas marcadas en la tabla** (hoja 2) o **toda una categoría**.
* **Personalizado**: eliges el parámetro y cómo agrupar (Categoría, Familia : Tipo, Nivel o Elemento). **Cargar** lista los grupos con sus valores actuales; escribe el **valor nuevo** en los que quieras y pulsa **Aplicar personalizado**.

## Limitaciones
* Los sectores son en planta (2D): con la opción de nivel desmarcada, valen para todos los niveles.
* Los textos de un DWG **importado** solo se leen si indicas su archivo. La geometría sí se lee siempre.
* Los elementos dentro de grupos pueden no admitir la escritura (se cuentan en el aviso).

## Compilar

```bash
./build-zip.sh      # dist/EMASY-Rellenar-2024.zip y dist/EMASY-Rellenar-2024-Setup.exe
```

Requiere el SDK de .NET (net48 / Revit 2024). Reutiliza la lectura de DWG de `RevitDwgExploder` (ACadSharp, en `RevitDwgExploder/lib`). El instalador copia `EMASY-Rellenar.addin` y `EMASY-Rellenar-2024/` en `%AppData%\Autodesk\Revit\Addins\2024`.
