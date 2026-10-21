# EMASY · Rellenar parámetros (Revit 2024)

Addin de la pestaña **EMASY**, panel **Rellenar**, botón **Rellenar parámetros**.

Llena una lista de parámetros (por ejemplo los de la **Tabla 5 de SUNAT**) en todos los elementos del modelo, cada uno según su regla:
* desde un dato que ya existe: categoría, familia, tipo, marca, material, nivel o cualquier parámetro;
* con un **valor fijo**;
* por **sectorización**, dibujada en una planta con **trazo de Revit** o con un **DWG**;
* **libre**, en modo **masivo** o **personalizado**.

Cuando falta el dato, **deduce** un valor con una lógica y explica el motivo. Los valores deducidos **no se escriben** hasta que los aceptes.

## 1 · Reglas
Una fila por parámetro destino:

| Columna | Qué es |
|---|---|
| Usar | Activa o desactiva la regla |
| Parámetro destino | Se autocompleta con los parámetros del modelo; "En el modelo" indica si existe (✗ = falta) |
| Origen | Categoría · Familia · Nombre de tipo · Familia : Tipo · Marca · Marca de tipo · Fabricante · Modelo · Nivel · Material · Parámetro · Valor fijo · Sectorización · Libre · N/A |
| Detalle | El parámetro de origen (Parámetro), el valor (Valor fijo) o el conjunto de sectores (Sectorización) |
| Prefijo / Sufijo | Se agregan al valor |
| Equivalencias | Valor leído → valor a escribir (p. ej. "Muros" → "MURO"), sin distinguir tildes ni mayúsculas |
| Si falta… | Cómo deducir el valor (ver abajo) |
| Sobrescribir | Si no está marcada, se conservan los valores que el elemento ya tiene |

Botones:
* **Preajuste SUNAT (Tabla 5)**: carga las 10 filas:

  | Nº | Parámetro | Origen |
  |---|---|---|
  | 1 | Categoría de Familia | Categoría |
  | 2 | Tipo de Elemento | Tipo |
  | 3 | Nivel de Elemento | Nivel |
  | 4 | Material | Material |
  | 5 | Código de Elemento | sectores "Local" |
  | 6 | Código Sitio | N/A |
  | 7 | Código Edificio | sectores "Edificio" |
  | 8 | Código Unidad Operativa | Libre |
  | 9 | Especialidad | Parámetro (escribe cuál) |
  | 10 | Sistema | Fijo "General" |
* **Crear parámetros que faltan**: crea los que no existen como compartidos, de texto, de ejemplar y en todas las categorías de modelo.
* **Guardar… / Cargar…**: las reglas en JSON. Las últimas usadas se guardan solas en `%AppData%\EMASY\Rellenar - reglas.json`.

**Material**: primero un parámetro de material del ejemplar o del tipo; si no hay, la capa más gruesa de la estructura (muros, suelos, techos, cubiertas); si tampoco, el material con más área del elemento.

**Nivel**: el nivel del elemento; si no tiene, el nivel de planificación, el de la familia o la restricción de base.

## 2 · Sectorización
Conjuntos de sectores con nombre, por ejemplo "Edificio" o "Local"; las reglas los usan por ese nombre. Para cada conjunto:
* **Trazo en planta**: en la vista elegida, las **regiones rellenas** (de un tipo o de todos) o los **lazos de líneas de detalle** (de un estilo o de todos).
* **DWG vinculado o importado** de esa vista: las **polilíneas cerradas** (o líneas que forman lazos) de una **capa**. **Leer capas** las lista. Si el DWG está importado (no vinculado), indica su archivo para leer los textos.
* **Nombre del sector**: el **texto que cae dentro** (nota de texto de Revit o texto del DWG; si hay varios, el más cercano al centro), el **tipo de región o capa**, o los **Comentarios** de la región.
* **Leer sectores** muestra la lista con su área. Los que no tienen nombre salen en rojo. **Ver en Revit** selecciona el trazo.
* Opción: **solo los elementos del nivel de la vista**. Si no se marca, el sector vale para todos los niveles.

Cada elemento cae en el sector que contiene su punto en planta: su punto de inserción, el medio de su línea o el centro de su caja. Si cae en varios sectores, gana el más pequeño.

## 3 · Vista previa
**Calcular** aplica las reglas y agrupa el resultado como **Parámetro → estado · valor → elementos**:
* **Directo** (verde): sale del origen. Va **marcado**.
* **Inferido** (azul): deducido, con su motivo. Va **desmarcado** hasta que lo aceptes con **Aceptar inferidos seleccionados** (o marcando su casilla).
* **Sin dato** (rojo), **Ya tiene el valor**, **Se conserva**, **Bloqueado** (solo lectura o tipo de dato no admitido).

**Escribir marcados** escribe todo en una transacción (sin avisos; se deshace con Ctrl+Z). Los parámetros **de tipo** se escriben una vez en el tipo. También están **Seleccionar en Revit** (o doble clic) y **Exportar CSV…**.

### Deducción ("Si falta…")
En este orden, según lo activado en cada regla:
1. **Sectorización**: el **sector más cercano**, si el elemento queda fuera de todos (indica la distancia).
2. **Nivel**: el **nivel por la altura** del elemento (el inmediatamente por debajo de su base).
3. **Mismo tipo**: el valor más frecuente entre los elementos de su tipo que sí lo tienen.
4. **Elemento cercano**: el valor del elemento con dato más próximo, hasta 15 m, primero en su mismo nivel.
5. **Palabras clave**: palabra en familia, tipo o categoría → valor (p. ej. drywall → PLACA DE YESO), o parecido del nombre con valores ya usados.
6. **Valor por defecto** de la regla.

## 4 · Libre
* **Masivo**: un parámetro y un valor para la **selección actual de Revit** (pulsa *Leer selección*), lo **marcado en la vista previa** o **toda una categoría**.
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
