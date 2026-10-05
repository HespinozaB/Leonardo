package main

// Servidor MCP "powerbi-report" (stdio, JSON-RPC 2.0 por líneas): lee y genera gráficos
// en reportes de Power BI. Se ejecuta con: PowerBI-Modeling-MCP-Setup.exe serve

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"strings"
	"sync"
)

const serverVersion = "1.1.0"

type tool struct {
	Name        string         `json:"name"`
	Description string         `json:"description"`
	InputSchema map[string]any `json:"inputSchema"`
	handler     func(args map[string]any) (any, error)
}

func obj(props map[string]any, required ...string) map[string]any {
	s := map[string]any{"type": "object", "properties": props}
	if len(required) > 0 {
		s["required"] = required
	}
	return s
}

func strP(desc string) map[string]any { return map[string]any{"type": "string", "description": desc} }
func numP(desc string) map[string]any { return map[string]any{"type": "number", "description": desc} }
func listP(desc string) map[string]any {
	return map[string]any{"type": "array", "items": map[string]any{"type": "string"}, "description": desc}
}

var (
	pathP      = strP("Ruta al archivo .pbip, a la carpeta .Report o al archivo .pbix (solo lectura). Ej: C:\\Reportes\\Ventas.pbip")
	pageP      = strP("Nombre visible de la página (o su id). Si se omite, la primera página.")
	visualP    = strP("Id del gráfico o su título (ver list_visuals).")
	fieldsHelp = "Campos como Tabla[Campo] o 'Tabla con espacios'[Campo]. Las medidas se detectan solas desde el modelo; " +
		"para columnas numéricas puede indicar la agregación: sum(T[c]), avg(...), count(...), countd(...) (distintos), min, max, median."
)

func tools() []tool {
	return []tool{
		{
			Name:        "list_pages",
			Description: "Lista las páginas de un reporte de Power BI con su tamaño y número de gráficos.",
			InputSchema: obj(map[string]any{"report_path": pathP}, "report_path"),
			handler: func(a map[string]any) (any, error) {
				r, err := openReport(argStr(a, "report_path"))
				if err != nil {
					return nil, err
				}
				ps, err := r.pages()
				return map[string]any{"format": r.kind, "writable": r.kind == "pbir", "pages": ps}, err
			},
		},
		{
			Name: "list_visuals",
			Description: "Lee los gráficos (visuales) de un reporte: tipo, título, posición y campos de cada rol (eje, valores, leyenda...). " +
				"Funciona con .pbip y .pbix. Para ver los datos que muestra un gráfico use visual_dax_query.",
			InputSchema: obj(map[string]any{"report_path": pathP, "page": strP("Página a leer; si se omite, todas.")}, "report_path"),
			handler: func(a map[string]any) (any, error) {
				r, err := openReport(argStr(a, "report_path"))
				if err != nil {
					return nil, err
				}
				vs, err := r.visuals(argStr(a, "page"))
				if vs == nil {
					vs = []visualInfo{}
				}
				return map[string]any{"count": len(vs), "visuals": vs}, err
			},
		},
		{
			Name:        "get_visual",
			Description: "Devuelve la definición JSON completa de un gráfico (formato, colores, filtros, etc.).",
			InputSchema: obj(map[string]any{"report_path": pathP, "page": pageP, "visual": visualP}, "report_path", "visual"),
			handler: func(a map[string]any) (any, error) {
				r, err := openReport(argStr(a, "report_path"))
				if err != nil {
					return nil, err
				}
				v, err := r.findVisual(argStr(a, "page"), argStr(a, "visual"))
				if err != nil {
					return nil, err
				}
				return v.raw, nil
			},
		},
		{
			Name: "visual_dax_query",
			Description: "Genera la consulta DAX que devuelve los datos que muestra un gráfico. Ejecútela con la herramienta de consultas DAX " +
				"del servidor powerbi-modeling (conectado al modelo abierto en Power BI Desktop) para leer los valores del gráfico.",
			InputSchema: obj(map[string]any{
				"report_path": pathP, "page": pageP, "visual": visualP,
				"top": numP("Máximo de filas (por defecto 500)."),
			}, "report_path", "visual"),
			handler: func(a map[string]any) (any, error) {
				r, err := openReport(argStr(a, "report_path"))
				if err != nil {
					return nil, err
				}
				v, err := r.findVisual(argStr(a, "page"), argStr(a, "visual"))
				if err != nil {
					return nil, err
				}
				top := 500
				if n := argNum(a, "top"); n != nil && *n > 0 {
					top = int(*n)
				}
				q, notes := visualDAX(v, top)
				return map[string]any{"visual": v.Name, "title": v.Title, "type": v.Type, "dax": q, "notes": notes}, nil
			},
		},
		{
			Name: "list_model_fields",
			Description: "Lista tablas, columnas (tipo y agregación por defecto) y medidas del modelo semántico del proyecto .pbip, " +
				"para saber qué campos usar al crear gráficos.",
			InputSchema: obj(map[string]any{"report_path": pathP, "table": strP("Filtrar por una tabla (opcional).")}, "report_path"),
			handler: func(a map[string]any) (any, error) {
				r, err := openReport(argStr(a, "report_path"))
				if err != nil {
					return nil, err
				}
				m, err := r.model()
				if err != nil {
					return nil, err
				}
				if t := argStr(a, "table"); t != "" {
					mt := m.table(t)
					if mt == nil {
						return nil, fmt.Errorf("la tabla %q no existe. Tablas: %s", t, strings.Join(m.tableNames(), ", "))
					}
					return mt, nil
				}
				return m, nil
			},
		},
		{
			Name: "create_visual",
			Description: "Crea un gráfico en una página de un reporte .pbip (formato PBIR). Tipos: " + strings.Join(visualTypeNames(), ", ") + ". " +
				"category = eje/categorías/filas; values = valores (scatter: [X, Y, tamaño opcional]); legend = leyenda/series/columnas de matriz; " +
				"line_values = valores de línea del combo. " + fieldsHelp + " " +
				"Si no se indica posición, se ubica en el primer espacio libre. Después hay que reabrir el .pbip en Power BI Desktop.",
			InputSchema: obj(map[string]any{
				"report_path": pathP,
				"page":        pageP,
				"visual_type": strP("Tipo de gráfico, p. ej. column, bar, line, pie, card, table, matrix, slicer."),
				"title":       strP("Título visible del gráfico (opcional)."),
				"category":    listP("Campos de categoría / eje X / filas."),
				"values":      listP("Campos de valores (medidas o columnas agregadas)."),
				"legend":      listP("Campos de leyenda / series."),
				"line_values": listP("Solo combo: valores de la línea."),
				"roles": map[string]any{
					"type":                 "object",
					"description":          "Avanzado: asignación directa rol -> campos (p. ej. {\"Tooltips\": [\"Ventas[Margen]\"]}).",
					"additionalProperties": map[string]any{"type": "array", "items": map[string]any{"type": "string"}},
				},
				"x": numP("Posición X en píxeles."), "y": numP("Posición Y en píxeles."),
				"width": numP("Ancho en píxeles."), "height": numP("Alto en píxeles."),
			}, "report_path", "visual_type"),
			handler: func(a map[string]any) (any, error) {
				r, err := openReport(argStr(a, "report_path"))
				if err != nil {
					return nil, err
				}
				roles := map[string][]string{}
				if m, ok := a["roles"].(map[string]any); ok {
					for k, v := range m {
						roles[k] = toStrings(v)
					}
				}
				return r.createVisual(createVisualReq{
					Page: argStr(a, "page"), Type: argStr(a, "visual_type"), Title: argStr(a, "title"),
					Category: toStrings(a["category"]), Values: toStrings(a["values"]),
					Legend: toStrings(a["legend"]), LineValues: toStrings(a["line_values"]), Roles: roles,
					X: argNum(a, "x"), Y: argNum(a, "y"), Width: argNum(a, "width"), Height: argNum(a, "height"),
				})
			},
		},
		{
			Name:        "update_visual",
			Description: "Mueve, redimensiona, cambia el título o el tipo de un gráfico existente (.pbip PBIR). El tipo solo se puede cambiar entre gráficos con roles compatibles (p. ej. column ↔ bar ↔ line).",
			InputSchema: obj(map[string]any{
				"report_path": pathP, "page": pageP, "visual": visualP,
				"title": strP("Nuevo título."), "visual_type": strP("Nuevo tipo."),
				"x": numP("X"), "y": numP("Y"), "width": numP("Ancho"), "height": numP("Alto"),
			}, "report_path", "visual"),
			handler: func(a map[string]any) (any, error) {
				r, err := openReport(argStr(a, "report_path"))
				if err != nil {
					return nil, err
				}
				return r.updateVisual(updateVisualReq{
					Page: argStr(a, "page"), Visual: argStr(a, "visual"), Title: argStr(a, "title"), Type: argStr(a, "visual_type"),
					X: argNum(a, "x"), Y: argNum(a, "y"), Width: argNum(a, "width"), Height: argNum(a, "height"),
				})
			},
		},
		{
			Name:        "delete_visual",
			Description: "Elimina un gráfico de un reporte .pbip (PBIR).",
			InputSchema: obj(map[string]any{"report_path": pathP, "page": pageP, "visual": visualP}, "report_path", "visual"),
			handler: func(a map[string]any) (any, error) {
				r, err := openReport(argStr(a, "report_path"))
				if err != nil {
					return nil, err
				}
				return r.deleteVisual(argStr(a, "page"), argStr(a, "visual"))
			},
		},
		{
			Name:        "create_page",
			Description: "Agrega una página nueva al reporte .pbip (PBIR). Tamaño por defecto 1280x720.",
			InputSchema: obj(map[string]any{
				"report_path": pathP, "display_name": strP("Nombre visible de la página."),
				"width": numP("Ancho (por defecto 1280)."), "height": numP("Alto (por defecto 720)."),
			}, "report_path", "display_name"),
			handler: func(a map[string]any) (any, error) {
				r, err := openReport(argStr(a, "report_path"))
				if err != nil {
					return nil, err
				}
				var w, h float64
				if n := argNum(a, "width"); n != nil {
					w = *n
				}
				if n := argNum(a, "height"); n != nil {
					h = *n
				}
				return r.createPage(argStr(a, "display_name"), w, h)
			},
		},
		{
			Name:        "delete_page",
			Description: "Elimina una página (y sus gráficos) de un reporte .pbip (PBIR).",
			InputSchema: obj(map[string]any{"report_path": pathP, "page": strP("Nombre visible o id de la página.")}, "report_path", "page"),
			handler: func(a map[string]any) (any, error) {
				r, err := openReport(argStr(a, "report_path"))
				if err != nil {
					return nil, err
				}
				return r.deletePage(argStr(a, "page"))
			},
		},
	}
}

func argStr(a map[string]any, k string) string { s, _ := a[k].(string); return strings.TrimSpace(s) }

func argNum(a map[string]any, k string) *float64 {
	switch n := a[k].(type) {
	case float64:
		return &n
	case json.Number:
		f, err := n.Float64()
		if err == nil {
			return &f
		}
	}
	return nil
}

func toStrings(v any) []string {
	switch x := v.(type) {
	case string:
		if strings.TrimSpace(x) != "" {
			return []string{x}
		}
	case []any:
		var out []string
		for _, e := range x {
			if s, ok := e.(string); ok && strings.TrimSpace(s) != "" {
				out = append(out, s)
			}
		}
		return out
	}
	return nil
}

// ── Protocolo ────────────────────────────────────────────────────────

type rpcMsg struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id,omitempty"`
	Method  string          `json:"method,omitempty"`
	Params  json.RawMessage `json:"params,omitempty"`
}

func serveMCP(in io.Reader, out io.Writer) error {
	ts := tools()
	byName := map[string]tool{}
	for _, t := range ts {
		byName[t.Name] = t
	}
	var mu sync.Mutex
	send := func(id json.RawMessage, result any, rpcErr map[string]any) {
		msg := map[string]any{"jsonrpc": "2.0", "id": id}
		if rpcErr != nil {
			msg["error"] = rpcErr
		} else {
			msg["result"] = result
		}
		b, _ := json.Marshal(msg)
		mu.Lock()
		out.Write(append(b, '\n'))
		mu.Unlock()
	}

	sc := bufio.NewScanner(in)
	sc.Buffer(make([]byte, 1024*1024), 64*1024*1024)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" {
			continue
		}
		var m rpcMsg
		if err := json.Unmarshal([]byte(line), &m); err != nil {
			send(json.RawMessage("null"), nil, map[string]any{"code": -32700, "message": "parse error"})
			continue
		}
		isNotification := len(m.ID) == 0 || string(m.ID) == "null"
		switch m.Method {
		case "initialize":
			var p struct {
				ProtocolVersion string `json:"protocolVersion"`
			}
			json.Unmarshal(m.Params, &p)
			if p.ProtocolVersion == "" {
				p.ProtocolVersion = "2024-11-05"
			}
			send(m.ID, map[string]any{
				"protocolVersion": p.ProtocolVersion,
				"capabilities":    map[string]any{"tools": map[string]any{}},
				"serverInfo":      map[string]any{"name": "powerbi-report", "version": serverVersion},
				"instructions": "Lee y genera gráficos en reportes de Power BI (.pbip en formato PBIR; .pbix solo lectura). " +
					"Flujo: list_pages -> list_visuals / list_model_fields -> create_visual. Para leer los valores de un gráfico, " +
					"use visual_dax_query y ejecute el DAX con el servidor powerbi-modeling. Tras escribir, el usuario debe cerrar y reabrir el .pbip en Power BI Desktop.",
			}, nil)
		case "ping":
			send(m.ID, map[string]any{}, nil)
		case "tools/list":
			send(m.ID, map[string]any{"tools": ts}, nil)
		case "tools/call":
			var p struct {
				Name      string         `json:"name"`
				Arguments map[string]any `json:"arguments"`
			}
			if err := json.Unmarshal(m.Params, &p); err != nil {
				send(m.ID, nil, map[string]any{"code": -32602, "message": "parámetros inválidos"})
				continue
			}
			t, ok := byName[p.Name]
			if !ok {
				send(m.ID, nil, map[string]any{"code": -32602, "message": "herramienta desconocida: " + p.Name})
				continue
			}
			if p.Arguments == nil {
				p.Arguments = map[string]any{}
			}
			res, err := callTool(t, p.Arguments)
			if err != nil {
				send(m.ID, map[string]any{"content": []any{map[string]any{"type": "text", "text": "Error: " + err.Error()}}, "isError": true}, nil)
				continue
			}
			b, _ := json.MarshalIndent(res, "", "  ")
			send(m.ID, map[string]any{"content": []any{map[string]any{"type": "text", "text": string(b)}}}, nil)
		default:
			if !isNotification {
				send(m.ID, nil, map[string]any{"code": -32601, "message": "método no soportado: " + m.Method})
			}
		}
	}
	return sc.Err()
}

func callTool(t tool, args map[string]any) (res any, err error) {
	defer func() {
		if r := recover(); r != nil {
			err = errors.New(fmt.Sprint("error interno: ", r))
		}
	}()
	return t.handler(args)
}

func runServer() {
	if err := serveMCP(os.Stdin, os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, "powerbi-report:", err)
		os.Exit(1)
	}
}
