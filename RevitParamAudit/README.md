# EMASY · Auditoría de parámetros (Revit 2024)

Addin hermano de *EMASY DWG Tools*: aparece en la misma pestaña **EMASY**, panel **Parámetros**, botón **Auditar Parámetros**.

Revisa todos los parámetros del proyecto (de proyecto, compartidos y globales) y los clasifica en cuatro pestañas:

| Pestaña | Qué contiene |
|---|---|
| **En planos** | Parámetros con valor en planos, viewports, vistas colocadas en planos, cajetines o Información de proyecto, y los usados en filtros de vistas colocadas en planos (o en sus plantillas). |
| **En tablas** | Parámetros usados como campo (incluidos los parámetros combinados) en cualquier tabla de planificación; se indica cuáles tablas y si están colocadas en un plano. |
| **Sin uso (residuales)** | Los que no aparecen ni en planos ni en tablas. Se pueden marcar y eliminar. |
| **Todos** | El total, con su estado. |

Cada fila muestra origen (Proyecto / Compartido / Global), tipo de dato, grupo, vínculo (Ejemplar / Tipo / Solo familias), categorías y, en los residuales, **advertencias** (en naranja) antes de borrar:

* usado en un filtro de vista que no está en planos,
* enlazado a Planos / Vistas / Cajetín / Información de proyecto (puede ser una etiqueta de cajetín aunque esté vacío),
* compartido sin enlace a categorías (viene de familias cargadas y reaparece al recargarlas),
* tiene valores escritos en elementos (se perderían).

## Botones

* **Análisis profundo**: además cuenta como "en planos" un parámetro con valor en los elementos visibles en las vistas colocadas en planos. Es más lento y es una señal débil (un valor no implica que se muestre), por eso está apagado por defecto.
* **Exportar CSV**: todos los parámetros con su estado (separador `;`, UTF-8 con BOM, abre bien en Excel).
* **Eliminar marcados** (solo en la pestaña de residuales): vuelve a comprobar que siguen sin uso, pide confirmación y borra en una sola transacción (se deshace con Ctrl+Z).

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
