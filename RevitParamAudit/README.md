# EMASY · Auditoría de parámetros (Revit 2024)

Addin hermano de *EMASY DWG Tools*: aparece en la misma pestaña **EMASY**, panel **Parámetros**, botón **Auditar Parámetros**.

Revisa todos los parámetros del proyecto (de proyecto, compartidos y globales) y los muestra en **una sola tabla**:

| Parámetro | Plano | Tabla | Ninguna | Con valores |
|---|---|---|---|---|
| Parámetro 1 | ✗ | ✗ | ✓ | No |

* **Plano ✓**: valor en planos, viewports, vistas colocadas, cajetines o Información de proyecto, o usado en filtros de vistas colocadas en planos.
* **Tabla ✓**: usado como campo (incluidos los combinados) en alguna tabla de planificación (el detalle indica cuáles).
* **Ninguna ✓**: no está ni en planos ni en tablas. Los residuales aparecen primero.
* **Con valores**: (en los residuales) algún elemento tiene un valor escrito; se perdería al eliminarlo.

Tú decides: marca con la casilla los que quieras eliminar (cualquier fila, no solo "Ninguna") y pulsa **Eliminar marcados**. Un filtro permite ver solo los residuales, los residuales sin valores, etc.

Además de lo anterior, cada fila muestra origen (Proyecto / Compartido / Global), tipo de dato, grupo, vínculo (Ejemplar / Tipo / Solo familias), categorías y, en los residuales, **advertencias** (en naranja) antes de borrar:

* usado en un filtro de vista que no está en planos,
* enlazado a Planos / Vistas / Cajetín / Información de proyecto (puede ser una etiqueta de cajetín aunque esté vacío),
* compartido sin enlace a categorías (viene de familias cargadas y reaparece al recargarlas),
* tiene valores escritos en elementos (se perderían).

## Botones

* **Análisis profundo**: además cuenta como "en planos" un parámetro con valor en los elementos visibles en las vistas colocadas en planos. Es más lento y es una señal débil (un valor no implica que se muestre), por eso está apagado por defecto.
* **Exportar CSV**: todos los parámetros con su estado (separador `;`, UTF-8 con BOM, abre bien en Excel).
* **Eliminar marcados**: pide confirmación (avisa de los que están en uso o tienen valores) y borra en una sola transacción (se deshace con Ctrl+Z).

## Limitaciones

La API de Revit no expone las etiquetas de los cajetines ni de las familias de etiquetas (están dentro de la familia), ni dónde se usan los parámetros globales. Por eso:

* un parámetro mostrado **solo** por una etiqueta o un cajetín y sin valores/filtros/tablas puede aparecer como residual; las advertencias cubren el caso del cajetín por categoría, pero revisa antes de borrar;
* los parámetros globales se listan como "Global (no evaluado)" y nunca se eliminan desde aquí;
* no se analizan los parámetros dentro de familias (solo los del proyecto).

## Compilar

```bash
./build-zip.sh      # dist/EMASY-Parametros-2024.zip y dist/EMASY-Parametros-2024-Setup.exe
```

Requiere el SDK de .NET (net48 / Revit 2024). El instalador copia `EMASY-Parametros.addin` y `EMASY-Parametros-2024/` a `%AppData%\Autodesk\Revit\Addins\2024` y no toca el addin DWG Tools.
