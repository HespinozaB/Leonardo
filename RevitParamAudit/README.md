# EMASY · Depurador de modelo (Revit 2024)

Addin hermano de *EMASY DWG Tools*: aparece en la pestaña **EMASY**, panel **Depurar Modelo**, con cuatro botones que se usan en este orden:

| Paso | Botón | Qué muestra |
|---|---|---|
| 1 | **Planos sin uso** | Número, nombre, vistas colocadas y tablas de cada plano. Vacío → ✓, con vistas o tablas → ✗. |
| 2 | **Vistas sin plano** | Nombre de vista, tipo y **nombre de plano** (o `NA` si no está en ninguno). Las vistas 3D, las que tienen dependientes y las tablas sin plano salen con ⚠. |
| 3 | **Filtros sin uso** | Nombre de filtro, **activo en vista**, **activo en plano** y en plantillas de vista. |
| 4 | **Parámetros sin uso** | Parámetro, **activo en plano**, **activo en tabla**, **proceder a eliminar** y **valores** (vacío / con información). |

El orden importa: al eliminar planos, sus vistas quedan sin plano (paso 2); al eliminar vistas, sus filtros dejan de usarse (paso 3); y al final los parámetros que ya no aparecen en planos, tablas ni filtros (paso 4). Cada ventana tiene un botón **Siguiente ▸** que abre el paso siguiente.

## Símbolos

* **✓ verde**: sí / se puede eliminar.
* **✗ rojo**: no / en uso.
* **⚠ ámbar**: sin uso, pero con algo que revisar antes de eliminar (vista 3D, información escrita, filtro en vistas fuera de planos, cajetín, familias…). El motivo aparece en la columna *Alertas* / *Advertencias*.

## Ventana (común a los cuatro pasos)

* **Casilla** de cada fila: tú eliges qué eliminar. Las marcas se mantienen al cambiar el filtro o la búsqueda.
* **Mostrar**: filtros rápidos (solo ✓, solo ⚠, sin plano, vistas 3D, con información…).
* **Columnas ▾**: columnas ocultas que se activan una a una (Origen, Tipo de dato, Grupo, Vínculo, Categorías, Id…). En parámetros, también el **análisis profundo**.
* **Eliminar marcados**: pide confirmación con la lista y los avisos; una sola transacción (Ctrl+Z la deshace).
* **Ubicar** (o doble clic): abre la vista o plano; en filtros abre una vista donde está activo.
* **Seleccionar**: selecciona en Revit los planos/vistas, o los elementos que cumplen un filtro.
* **Exportar CSV**: todas las filas y columnas (✓/✗/⚠ pasan a Sí/No/Revisar).

## Parámetros: qué cuenta como uso

* **Activo en plano**: valor en planos, viewports, vistas colocadas, cajetines (incluidos los compartidos de la familia del cajetín) o Información de proyecto, o usado en un filtro de una vista colocada en un plano.
* **Activo en tabla**: campo (incluidos los combinados) de alguna tabla de planificación.
* **Valores**: si algún elemento tiene información escrita. Se calcula con el filtro nativo de Revit "tiene valor", por eso carga mucho más rápido que la versión anterior. Un parámetro sin uso pero **con información** sale con ⚠, no con ✓.

## Limitaciones

* Las **tablas no usan filtros de vista** (sus filtros son internos de cada tabla), por eso el depurador de filtros no tiene columna "activo en tabla": borrar un filtro de vista nunca afecta a una tabla.
* La API de Revit no expone las etiquetas de las familias de etiquetas ni dónde se usan los parámetros globales: los globales salen como "—" y no se eliminan desde aquí.
* La vista activa no se puede eliminar: abre otra vista y repite.

## Compilar

```bash
python3 tools/make_icons.py   # solo si cambian los iconos (requiere Pillow)
./build-zip.sh                # dist/EMASY-Depurador-2024.zip y dist/EMASY-Depurador-2024-Setup.exe
```

Requiere el SDK de .NET (compila para net48 / Revit 2024 también desde Linux). El instalador copia `EMASY-Depurador.addin` y `EMASY-Depurador-2024/` a `%AppData%\Autodesk\Revit\Addins\2024`, quita la versión anterior *EMASY Parámetros* y no toca DWG Tools.
