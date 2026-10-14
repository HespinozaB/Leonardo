// Definición de las herramientas MCP. Cada nombre debe existir en addin/src/Tools/ToolRegistry.cs.
import { z } from "zod";

const id = z.union([z.number().int(), z.string()]).describe("Id de elemento de Revit");
const ids = z.array(id).min(1);
const point = z.object({ x: z.number(), y: z.number(), z: z.number().optional() }).describe("Punto en mm");

const READ = { readOnlyHint: true, destructiveHint: false, openWorldHint: false };
const WRITE = { readOnlyHint: false, destructiveHint: false, openWorldHint: false };
const DESTRUCTIVE = { readOnlyHint: false, destructiveHint: true, openWorldHint: false };

export const tools = [
  {
    name: "get_revit_info",
    description: "Versión de Revit, documento y vista activos, documentos abiertos. Úsala primero para comprobar la conexión.",
    schema: {},
    annotations: READ,
  },
  {
    name: "get_selection",
    description: "Elementos seleccionados ahora mismo en Revit.",
    schema: { includeParameters: z.boolean().optional().describe("Incluir todos los parámetros de cada elemento") },
    annotations: READ,
  },
  {
    name: "list_elements",
    description:
      "Busca elementos del modelo. Filtra por categoría (nombre visible como 'Muros' o BuiltInCategory como 'OST_Walls'), " +
      "clase de la API ('Wall', 'FamilyInstance'), vista, nombre o valor de parámetro. Paginado con limit/offset.",
    schema: {
      category: z.string().optional(),
      className: z.string().optional(),
      viewId: id.optional().describe("Solo elementos visibles en esta vista"),
      activeViewOnly: z.boolean().optional(),
      typesOnly: z.boolean().optional().describe("Devolver tipos en lugar de ejemplares"),
      nameContains: z.string().optional(),
      parameterName: z.string().optional(),
      parameterValue: z.string().optional().describe("Valor tal como se muestra en Revit; sin él, basta con que exista el parámetro"),
      limit: z.number().int().optional().describe("Por defecto 100, máximo 2000"),
      offset: z.number().int().optional(),
    },
    annotations: READ,
  },
  {
    name: "get_element",
    description: "Datos completos de uno o varios elementos: ubicación (mm) y todos sus parámetros.",
    schema: { ids, includeTypeParameters: z.boolean().optional() },
    annotations: READ,
  },
  {
    name: "list_views",
    description: "Vistas y planos del proyecto. viewType: FloorPlan, CeilingPlan, Section, Elevation, ThreeD, DrawingSheet, Schedule, Legend, DraftingView...",
    schema: { viewType: z.string().optional(), includeTemplates: z.boolean().optional() },
    annotations: READ,
  },
  {
    name: "list_levels",
    description: "Niveles con su cota en mm.",
    schema: {},
    annotations: READ,
  },
  {
    name: "list_family_types",
    description: "Tipos disponibles (familias cargadas y de sistema) para crear elementos. Filtra por categoría o texto.",
    schema: { category: z.string().optional(), nameContains: z.string().optional(), limit: z.number().int().optional() },
    annotations: READ,
  },
  {
    name: "set_selection",
    description: "Selecciona elementos en Revit y (por defecto) hace zoom a ellos.",
    schema: { ids: z.array(id), zoom: z.boolean().optional() },
    annotations: WRITE,
  },
  {
    name: "activate_view",
    description: "Abre una vista o plano en Revit.",
    schema: { viewId: id },
    annotations: WRITE,
  },
  {
    name: "export_view_image",
    description: "Captura una vista o plano como PNG para poder verlo. Sin viewId, la vista activa.",
    schema: {
      viewId: id.optional(),
      pixelSize: z.number().int().optional().describe("Ancho en píxeles (256-4096, por defecto 1600)"),
      savePath: z.string().optional().describe("Ruta de Windows donde guardar también el PNG"),
    },
    annotations: READ,
  },
  {
    name: "set_parameters",
    description:
      "Cambia valores de parámetros. Un número en parámetros con unidades se interpreta en mm/m²/m³/grados según el tipo; " +
      "un texto se interpreta como si se escribiera en Revit ('3000', '3 m'). onType=true cambia el parámetro del tipo.",
    schema: {
      updates: z.array(z.object({
        ids,
        parameter: z.string().describe("Nombre visible o BuiltInParameter"),
        value: z.union([z.string(), z.number(), z.boolean(), z.null()]),
        onType: z.boolean().optional(),
      })).min(1),
    },
    annotations: WRITE,
  },
  {
    name: "delete_elements",
    description: "Elimina elementos (y los que dependen de ellos). Se deshace con Ctrl+Z en Revit.",
    schema: { ids },
    annotations: DESTRUCTIVE,
  },
  {
    name: "transform_elements",
    description:
      "Mueve, copia, gira o hace simetría de elementos. move/copy: vector (mm). rotate: angleDeg y center opcional (eje vertical). " +
      "mirror: center y normal del plano vertical; copy=false mueve en lugar de copiar.",
    schema: {
      ids,
      operation: z.enum(["move", "copy", "rotate", "mirror"]),
      vector: point.optional(),
      angleDeg: z.number().optional(),
      center: point.optional(),
      normal: point.optional(),
      copy: z.boolean().optional(),
    },
    annotations: WRITE,
  },
  {
    name: "create_level",
    description: "Crea un nivel (y por defecto su vista de planta).",
    schema: { elevationMm: z.number(), name: z.string().optional(), createPlanView: z.boolean().optional() },
    annotations: WRITE,
  },
  {
    name: "create_grid",
    description: "Crea una rejilla recta entre dos puntos (mm).",
    schema: { start: point, end: point, name: z.string().optional() },
    annotations: WRITE,
  },
  {
    name: "create_wall",
    description:
      "Crea muros. Usa start/end para uno, walls:[{start,end}] para varios, o points (polilínea, closed=true para cerrarla). " +
      "Coordenadas en mm relativas al nivel.",
    schema: {
      start: point.optional(),
      end: point.optional(),
      walls: z.array(z.object({ start: point, end: point })).optional(),
      points: z.array(point).optional(),
      closed: z.boolean().optional(),
      level: z.string().optional().describe("Nombre o id del nivel; por defecto el de la vista activa"),
      wallType: z.string().optional().describe("Nombre o id del tipo de muro"),
      heightMm: z.number().optional().describe("Por defecto 3000"),
      baseOffsetMm: z.number().optional(),
      structural: z.boolean().optional(),
    },
    annotations: WRITE,
  },
  {
    name: "create_floor",
    description: "Crea un suelo a partir de un contorno cerrado de puntos (mm).",
    schema: {
      points: z.array(point).min(3),
      level: z.string().optional(),
      floorType: z.string().optional(),
      offsetMm: z.number().optional(),
    },
    annotations: WRITE,
  },
  {
    name: "place_family_instance",
    description:
      "Coloca un ejemplar de familia (mobiliario, pilares, equipos...). familyType: 'Familia : Tipo', nombre del tipo o id. " +
      "Para puertas/ventanas indica hostId (el muro). point en mm, z relativa al nivel.",
    schema: {
      familyType: z.string(),
      category: z.string().optional(),
      point,
      level: z.string().optional(),
      hostId: id.optional(),
      rotationDeg: z.number().optional(),
    },
    annotations: WRITE,
  },
  {
    name: "create_text_note",
    description: "Crea una nota de texto en una vista (por defecto la activa). point en mm del modelo.",
    schema: { text: z.string(), point, viewId: id.optional(), textType: z.string().optional() },
    annotations: WRITE,
  },
  {
    name: "create_sheet",
    description: "Crea un plano. titleBlock: nombre del cajetín, 'none' para ninguno, o vacío para el primero disponible.",
    schema: { number: z.string().optional(), name: z.string().optional(), titleBlock: z.string().optional() },
    annotations: WRITE,
  },
  {
    name: "place_view_on_sheet",
    description: "Coloca una vista en un plano. point en mm del plano; por defecto el centro.",
    schema: { sheetId: id, viewId: id, point: point.optional() },
    annotations: WRITE,
  },
  {
    name: "run_csharp",
    description:
      "Ejecuta código C# (script de Roslyn) dentro de Revit para todo lo que las demás herramientas no cubren. " +
      "Variables: uiapp, uidoc, doc. Funciones: Print(x), Mm(mm)->pies, ToMm(pies). Imports: System, System.Linq, " +
      "Autodesk.Revit.DB(.Architecture/.Structure), Autodesk.Revit.UI. Termina con una expresión o 'return x;' para devolver un valor. " +
      "Por defecto va dentro de una transacción (Ctrl+Z la deshace); transaction=false para abrir transacciones propias o solo leer. " +
      "Unidades internas de Revit: pies.",
    schema: {
      code: z.string(),
      transaction: z.boolean().optional(),
      transactionName: z.string().optional(),
    },
    annotations: DESTRUCTIVE,
  },
];
