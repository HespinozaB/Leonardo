package main

// Lectura y escritura de gráficos (visuales) de reportes de Power BI.
//
//   - .pbip / carpeta .Report con formato PBIR (definition/pages/...): lectura y escritura.
//   - .pbip con report.json (PBIR-Legacy) y .pbix: solo lectura.
//
// El modelo semántico (.SemanticModel en TMDL o model.bim) se lee para saber qué campos
// son medidas o columnas.

import (
	"archive/zip"
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"unicode/utf16"
)

const (
	schemaBase          = "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/"
	defaultVisualSchema = schemaBase + "visualContainer/1.0.0/schema.json"
	defaultPageSchema   = schemaBase + "page/1.0.0/schema.json"
	defaultPagesSchema  = schemaBase + "pagesMetadata/1.0.0/schema.json"
	reloadNote          = "Cambios guardados en disco. Cierre y vuelva a abrir el .pbip en Power BI Desktop para verlos (si lo tiene abierto, no guarde desde Desktop o sobrescribirá estos cambios)."
)

// ── Ubicación del reporte ────────────────────────────────────────────

type report struct {
	kind      string // "pbir", "legacy" (report.json) o "pbix"
	reportDir string
	layout    map[string]any // formato legacy (report.json o Report/Layout del .pbix)
	modelDir  string
}

func (r *report) defDir() string   { return filepath.Join(r.reportDir, "definition") }
func (r *report) pagesDir() string { return filepath.Join(r.defDir(), "pages") }

func openReport(p string) (*report, error) {
	if p == "" {
		return nil, errors.New("falta report_path")
	}
	p = filepath.Clean(p)
	st, err := os.Stat(p)
	if err != nil {
		return nil, fmt.Errorf("no existe %s", p)
	}

	if !st.IsDir() {
		switch strings.ToLower(filepath.Ext(p)) {
		case ".pbix":
			return openPbix(p)
		case ".pbip":
			dir := filepath.Dir(p)
			var pbip struct {
				Artifacts []struct {
					Report struct{ Path string } `json:"report"`
				} `json:"artifacts"`
			}
			if b, err := os.ReadFile(p); err == nil {
				json.Unmarshal(trimBOM(b), &pbip)
			}
			for _, a := range pbip.Artifacts {
				if a.Report.Path != "" {
					return openReportDir(filepath.Join(dir, a.Report.Path))
				}
			}
			return openReportDir(strings.TrimSuffix(p, filepath.Ext(p)) + ".Report")
		default: // definition.pbir, report.json, etc.
			return openReport(filepath.Dir(p))
		}
	}

	if isReportDir(p) {
		return openReportDir(p)
	}
	// Carpeta del proyecto: buscar *.Report dentro.
	cands, _ := filepath.Glob(filepath.Join(p, "*.Report"))
	if len(cands) == 1 {
		return openReportDir(cands[0])
	}
	if len(cands) > 1 {
		return nil, fmt.Errorf("hay varios reportes en %s, indique uno: %s", p, strings.Join(cands, ", "))
	}
	return nil, fmt.Errorf("%s no es un reporte de Power BI (.pbip, .pbix o carpeta .Report)", p)
}

func isReportDir(d string) bool {
	for _, f := range []string{"definition.pbir", "definition", "report.json"} {
		if _, err := os.Stat(filepath.Join(d, f)); err == nil {
			return true
		}
	}
	return false
}

func openReportDir(d string) (*report, error) {
	r := &report{reportDir: d}
	if _, err := os.Stat(filepath.Join(d, "definition", "pages")); err == nil {
		r.kind = "pbir"
	} else if b, err := os.ReadFile(filepath.Join(d, "report.json")); err == nil {
		r.kind = "legacy"
		if r.layout, err = decodeJSON(b); err != nil {
			return nil, fmt.Errorf("report.json inválido: %w", err)
		}
	} else if _, err := os.Stat(filepath.Join(d, "definition")); err == nil {
		r.kind = "pbir" // reporte PBIR sin páginas todavía
	} else {
		return nil, fmt.Errorf("no se encontró definition/ ni report.json en %s", d)
	}
	r.modelDir = findModelDir(d)
	return r, nil
}

func openPbix(p string) (*report, error) {
	zr, err := zip.OpenReader(p)
	if err != nil {
		return nil, fmt.Errorf("no se pudo abrir el .pbix: %w", err)
	}
	defer zr.Close()
	for _, f := range zr.File {
		if f.Name != "Report/Layout" {
			continue
		}
		rc, err := f.Open()
		if err != nil {
			return nil, err
		}
		b, err := io.ReadAll(rc)
		rc.Close()
		if err != nil {
			return nil, err
		}
		layout, err := decodeJSON(decodeUTF16(b))
		if err != nil {
			return nil, fmt.Errorf("Report/Layout inválido: %w", err)
		}
		return &report{kind: "pbix", reportDir: p, layout: layout}, nil
	}
	return nil, errors.New("el .pbix no contiene Report/Layout")
}

func findModelDir(reportDir string) string {
	var pbir struct {
		DatasetReference struct {
			ByPath *struct{ Path string } `json:"byPath"`
		} `json:"datasetReference"`
	}
	if b, err := os.ReadFile(filepath.Join(reportDir, "definition.pbir")); err == nil {
		json.Unmarshal(trimBOM(b), &pbir)
		if bp := pbir.DatasetReference.ByPath; bp != nil && bp.Path != "" {
			return filepath.Join(reportDir, filepath.FromSlash(bp.Path))
		}
	}
	d := strings.TrimSuffix(reportDir, ".Report") + ".SemanticModel"
	if _, err := os.Stat(d); err == nil {
		return d
	}
	return ""
}

func (r *report) requireWritable() error {
	switch r.kind {
	case "pbir":
		return nil
	case "pbix":
		return errors.New("los .pbix son de solo lectura. Guárdelo como proyecto (Archivo > Guardar como > .pbip) con el formato PBIR activado (Opciones > Características en versión preliminar > \"Almacenar informes con formato de metadatos mejorado (PBIR)\")")
	default:
		return errors.New("este .pbip usa el formato antiguo report.json (solo lectura). Active en Power BI Desktop: Opciones > Características en versión preliminar > \"Almacenar informes con formato de metadatos mejorado (PBIR)\" y vuelva a guardar el proyecto")
	}
}

// ── Modelo semántico ─────────────────────────────────────────────────

type modelColumn struct {
	Name        string `json:"name"`
	DataType    string `json:"dataType,omitempty"`
	SummarizeBy string `json:"summarizeBy,omitempty"`
	Hidden      bool   `json:"hidden,omitempty"`
}

type modelMeasure struct {
	Name       string `json:"name"`
	Expression string `json:"expression,omitempty"`
}

type modelTable struct {
	Name     string         `json:"name"`
	Columns  []modelColumn  `json:"columns"`
	Measures []modelMeasure `json:"measures,omitempty"`
}

type model struct {
	Source string       `json:"source"`
	Tables []modelTable `json:"tables"`
}

func (r *report) model() (*model, error) {
	if r.modelDir == "" {
		return nil, errors.New("no se encontró el modelo semántico local (.SemanticModel). Si el reporte usa conexión en vivo, consulte los campos con el servidor powerbi-modeling")
	}
	if b, err := os.ReadFile(filepath.Join(r.modelDir, "model.bim")); err == nil {
		return parseBIM(b, r.modelDir)
	}
	var files []string
	filepath.WalkDir(filepath.Join(r.modelDir, "definition"), func(p string, d os.DirEntry, err error) error {
		if err == nil && !d.IsDir() && strings.EqualFold(filepath.Ext(p), ".tmdl") {
			files = append(files, p)
		}
		return nil
	})
	if len(files) == 0 {
		return nil, fmt.Errorf("no hay model.bim ni archivos .tmdl en %s", r.modelDir)
	}
	sort.Strings(files)
	m := &model{Source: r.modelDir}
	for _, f := range files {
		b, err := os.ReadFile(f)
		if err != nil {
			return nil, err
		}
		m.Tables = append(m.Tables, parseTMDL(string(trimBOM(b)))...)
	}
	return m, nil
}

func parseBIM(b []byte, src string) (*model, error) {
	var bim struct {
		Model struct {
			Tables []struct {
				Name    string `json:"name"`
				Columns []struct {
					Name        string `json:"name"`
					DataType    string `json:"dataType"`
					SummarizeBy string `json:"summarizeBy"`
					IsHidden    bool   `json:"isHidden"`
					Type        string `json:"type"`
				} `json:"columns"`
				Measures []struct {
					Name       string          `json:"name"`
					Expression json.RawMessage `json:"expression"`
				} `json:"measures"`
			} `json:"tables"`
		} `json:"model"`
	}
	if err := json.Unmarshal(trimBOM(b), &bim); err != nil {
		return nil, fmt.Errorf("model.bim inválido: %w", err)
	}
	m := &model{Source: src}
	for _, t := range bim.Model.Tables {
		mt := modelTable{Name: t.Name}
		for _, c := range t.Columns {
			if c.Type == "rowNumber" {
				continue
			}
			mt.Columns = append(mt.Columns, modelColumn{c.Name, c.DataType, c.SummarizeBy, c.IsHidden})
		}
		for _, ms := range t.Measures {
			var expr string
			var lines []string
			if json.Unmarshal(ms.Expression, &expr) != nil && json.Unmarshal(ms.Expression, &lines) == nil {
				expr = strings.Join(lines, "\n")
			}
			mt.Measures = append(mt.Measures, modelMeasure{ms.Name, firstLine(expr)})
		}
		m.Tables = append(m.Tables, mt)
	}
	return m, nil
}

// parseTMDL extrae tablas, columnas y medidas de un archivo TMDL (sintaxis basada en tabulaciones).
func parseTMDL(src string) []modelTable {
	var tables []modelTable
	var cur *modelTable
	var col *modelColumn
	for _, raw := range strings.Split(strings.ReplaceAll(src, "\r\n", "\n"), "\n") {
		indent := len(raw) - len(strings.TrimLeft(raw, "\t"))
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "///") {
			continue
		}
		switch {
		case indent == 0 && strings.HasPrefix(line, "table "):
			name, _ := tmdlName(line[len("table "):])
			tables = append(tables, modelTable{Name: name})
			cur, col = &tables[len(tables)-1], nil
		case indent == 0:
			cur, col = nil, nil
		case cur == nil:
		case indent == 1 && strings.HasPrefix(line, "column "):
			name, _ := tmdlName(line[len("column "):])
			cur.Columns = append(cur.Columns, modelColumn{Name: name})
			col = &cur.Columns[len(cur.Columns)-1]
		case indent == 1 && strings.HasPrefix(line, "measure "):
			name, rest := tmdlName(line[len("measure "):])
			expr := strings.TrimSpace(strings.TrimPrefix(strings.TrimSpace(rest), "="))
			cur.Measures = append(cur.Measures, modelMeasure{Name: name, Expression: expr})
			col = nil
		case indent == 1:
			col = nil
		case indent == 2 && col != nil:
			if k, v, ok := strings.Cut(line, ":"); ok {
				switch strings.TrimSpace(k) {
				case "dataType":
					col.DataType = strings.TrimSpace(v)
				case "summarizeBy":
					col.SummarizeBy = strings.TrimSpace(v)
				}
			} else if line == "isHidden" {
				col.Hidden = true
			}
		}
	}
	return tables
}

// tmdlName lee un nombre TMDL ('Con espacios' o SinEspacios) y devuelve el resto de la línea.
func tmdlName(s string) (name, rest string) {
	s = strings.TrimSpace(s)
	if strings.HasPrefix(s, "'") {
		var b strings.Builder
		for i := 1; i < len(s); i++ {
			if s[i] == '\'' {
				if i+1 < len(s) && s[i+1] == '\'' {
					b.WriteByte('\'')
					i++
					continue
				}
				return b.String(), s[i+1:]
			}
			b.WriteByte(s[i])
		}
		return b.String(), ""
	}
	i := strings.IndexAny(s, " =")
	if i < 0 {
		return s, ""
	}
	return s[:i], s[i:]
}

// ── Referencias a campos ─────────────────────────────────────────────

type fieldRef struct {
	Table   string `json:"table"`
	Field   string `json:"field"`
	Kind    string `json:"kind"` // column, measure, aggregation, other
	Agg     string `json:"aggregation,omitempty"`
	Display string `json:"display"`
}

// Códigos QueryAggregateFunction usados por Power BI.
var aggs = []struct {
	keys  []string
	code  int
	name  string // prefijo de queryRef
	label string // prefijo de nativeQueryRef
	dax   string
}{
	{[]string{"sum"}, 0, "Sum", "Sum of", "SUM"},
	{[]string{"avg", "average"}, 1, "Avg", "Average of", "AVERAGE"},
	{[]string{"countd", "distinctcount"}, 2, "Count", "Count of", "DISTINCTCOUNT"},
	{[]string{"min"}, 3, "Min", "Min of", "MIN"},
	{[]string{"max"}, 4, "Max", "Max of", "MAX"},
	{[]string{"count"}, 5, "CountNonNull", "Count of", "COUNT"},
	{[]string{"median"}, 6, "Median", "Median of", "MEDIAN"},
}

func aggByKey(k string) (int, bool) {
	k = strings.ToLower(k)
	for i, a := range aggs {
		for _, key := range a.keys {
			if key == k {
				return i, true
			}
		}
	}
	return 0, false
}

func aggByCode(c int) (int, bool) {
	for i, a := range aggs {
		if a.code == c {
			return i, true
		}
	}
	return 0, false
}

var fieldRe = regexp.MustCompile(`^\s*(?:([A-Za-z]+)\s*\(\s*)?('(?:[^']|'')+'|[^\[\]()']+?)\s*\[((?:[^\]]|\]\])+)\]\s*\)?\s*$`)

// resolveField interpreta "Tabla[Campo]", "'Tabla con espacios'[Campo]", "sum(Tabla[Columna])"
// o "measure(Tabla[Medida])" y lo valida contra el modelo si está disponible.
func resolveField(s string, m *model, valueRole bool) (fieldRef, error) {
	mt := fieldRe.FindStringSubmatch(s)
	if mt == nil {
		return fieldRef{}, fmt.Errorf("campo %q inválido: use Tabla[Campo], 'Tabla con espacios'[Campo] o sum(Tabla[Columna])", s)
	}
	fn, table, field := strings.ToLower(mt[1]), mt[2], strings.ReplaceAll(mt[3], "]]", "]")
	if strings.HasPrefix(table, "'") {
		table = strings.ReplaceAll(table[1:len(table)-1], "''", "'")
	}
	f := fieldRef{Table: table, Field: field, Kind: "column"}
	if fn == "measure" {
		f.Kind = "measure"
	} else if fn != "" {
		i, ok := aggByKey(fn)
		if !ok {
			return f, fmt.Errorf("agregación %q no soportada (use sum, avg, count, countd, min, max, median o measure)", fn)
		}
		f.Kind, f.Agg = "aggregation", aggs[i].keys[0]
	}

	if m != nil {
		t := m.table(table)
		if t == nil {
			return f, fmt.Errorf("la tabla %q no existe en el modelo. Tablas: %s", table, strings.Join(m.tableNames(), ", "))
		}
		f.Table = t.Name
		found := false
		for _, ms := range t.Measures {
			if strings.EqualFold(ms.Name, field) {
				if f.Kind == "aggregation" {
					return f, fmt.Errorf("%s es una medida, no se puede agregar con %s()", ms.Name, fn)
				}
				f.Field, f.Kind, found = ms.Name, "measure", true
			}
		}
		for _, c := range t.Columns {
			if !found && strings.EqualFold(c.Name, field) {
				if f.Kind == "measure" {
					return f, fmt.Errorf("%s[%s] es una columna, no una medida", t.Name, c.Name)
				}
				f.Field, found = c.Name, true
				// En roles de valores, una columna numérica sin agregación usa su summarizeBy.
				if valueRole && f.Kind == "column" && c.SummarizeBy != "" && c.SummarizeBy != "none" {
					if _, ok := aggByKey(c.SummarizeBy); ok {
						f.Kind, f.Agg = "aggregation", strings.ToLower(c.SummarizeBy)
					} else if strings.EqualFold(c.SummarizeBy, "distinctCount") {
						f.Kind, f.Agg = "aggregation", "countd"
					}
				}
			}
		}
		if !found {
			return f, fmt.Errorf("el campo %q no existe en la tabla %s", field, t.Name)
		}
	}
	f.Display = f.String()
	return f, nil
}

func (f fieldRef) String() string {
	ref := daxTable(f.Table) + "[" + f.Field + "]"
	switch f.Kind {
	case "aggregation":
		return f.Agg + "(" + ref + ")"
	case "measure":
		return "measure(" + ref + ")"
	}
	return ref
}

func (m *model) table(name string) *modelTable {
	for i := range m.Tables {
		if strings.EqualFold(m.Tables[i].Name, name) {
			return &m.Tables[i]
		}
	}
	return nil
}

func (m *model) tableNames() []string {
	var n []string
	for _, t := range m.Tables {
		n = append(n, t.Name)
	}
	return n
}

// projection construye la proyección PBIR (queryState) de un campo.
func projection(f fieldRef) map[string]any {
	src := map[string]any{
		"Expression": map[string]any{"SourceRef": map[string]any{"Entity": f.Table}},
		"Property":   f.Field,
	}
	p := map[string]any{"queryRef": f.Table + "." + f.Field, "nativeQueryRef": f.Field}
	switch f.Kind {
	case "measure":
		p["field"] = map[string]any{"Measure": src}
	case "aggregation":
		i, _ := aggByKey(f.Agg)
		a := aggs[i]
		p["field"] = map[string]any{"Aggregation": map[string]any{
			"Expression": map[string]any{"Column": src},
			"Function":   a.code,
		}}
		p["queryRef"] = a.name + "(" + f.Table + "." + f.Field + ")"
		p["nativeQueryRef"] = a.label + " " + f.Field
	default:
		p["field"] = map[string]any{"Column": src}
	}
	return p
}

// fieldFromExpr interpreta una expresión de consulta semántica (PBIR o legacy).
func fieldFromExpr(e any, aliases map[string]string) *fieldRef {
	em, _ := e.(map[string]any)
	entity := func(x any) string {
		sr, _ := dig(x, "Expression", "SourceRef").(map[string]any)
		if s, ok := sr["Entity"].(string); ok {
			return s
		}
		if s, ok := sr["Source"].(string); ok {
			return aliases[s]
		}
		return ""
	}
	prop := func(x any) string { s, _ := dig(x, "Property").(string); return s }
	switch {
	case em["Column"] != nil:
		return &fieldRef{Table: entity(em["Column"]), Field: prop(em["Column"]), Kind: "column"}
	case em["Measure"] != nil:
		return &fieldRef{Table: entity(em["Measure"]), Field: prop(em["Measure"]), Kind: "measure"}
	case em["Aggregation"] != nil:
		inner := fieldFromExpr(dig(em["Aggregation"], "Expression"), aliases)
		if inner == nil || inner.Kind != "column" {
			return &fieldRef{Kind: "other"}
		}
		inner.Kind = "aggregation"
		if i, ok := aggByCode(int(numVal(dig(em["Aggregation"], "Function")))); ok {
			inner.Agg = aggs[i].keys[0]
		} else {
			inner.Agg = "sum"
		}
		return inner
	case em != nil:
		return &fieldRef{Kind: "other"}
	}
	return nil
}

// ── Lectura de páginas y visuales ────────────────────────────────────

type pageInfo struct {
	Name        string  `json:"name"`
	DisplayName string  `json:"displayName"`
	Width       float64 `json:"width"`
	Height      float64 `json:"height"`
	Visuals     int     `json:"visuals"`
	Hidden      bool    `json:"hidden,omitempty"`
}

type projInfo struct {
	Role     string    `json:"role"`
	QueryRef string    `json:"queryRef"`
	Field    *fieldRef `json:"field,omitempty"`
}

type visualInfo struct {
	Page   string              `json:"page"`
	Name   string              `json:"name"`
	Type   string              `json:"type"`
	Title  string              `json:"title,omitempty"`
	X      float64             `json:"x"`
	Y      float64             `json:"y"`
	Width  float64             `json:"width"`
	Height float64             `json:"height"`
	Z      float64             `json:"-"`
	Fields map[string][]string `json:"fields,omitempty"`
	Group  string              `json:"parentGroup,omitempty"`
	proj   []projInfo
	dir    string
	raw    map[string]any
}

func (r *report) pages() ([]pageInfo, error) {
	if r.kind != "pbir" {
		var out []pageInfo
		for _, s := range listOf(r.layout["sections"]) {
			out = append(out, pageInfo{
				Name:        str(dig(s, "name")),
				DisplayName: str(dig(s, "displayName")),
				Width:       numVal(dig(s, "width")),
				Height:      numVal(dig(s, "height")),
				Visuals:     len(listOf(dig(s, "visualContainers"))),
			})
		}
		return out, nil
	}
	entries, err := os.ReadDir(r.pagesDir())
	if err != nil && !errors.Is(err, os.ErrNotExist) {
		return nil, err
	}
	byName := map[string]pageInfo{}
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		pj, err := readJSON(filepath.Join(r.pagesDir(), e.Name(), "page.json"))
		if err != nil {
			continue
		}
		vis, _ := os.ReadDir(filepath.Join(r.pagesDir(), e.Name(), "visuals"))
		p := pageInfo{
			Name:        e.Name(),
			DisplayName: str(pj["displayName"]),
			Width:       numVal(pj["width"]),
			Height:      numVal(pj["height"]),
			Hidden:      str(pj["visibility"]) == "HiddenInViewMode",
		}
		for _, v := range vis {
			if v.IsDir() {
				p.Visuals++
			}
		}
		byName[e.Name()] = p
	}
	var out []pageInfo
	meta, _ := readJSON(filepath.Join(r.pagesDir(), "pages.json"))
	for _, n := range listOf(meta["pageOrder"]) {
		if p, ok := byName[str(n)]; ok {
			out = append(out, p)
			delete(byName, str(n))
		}
	}
	var rest []string
	for n := range byName {
		rest = append(rest, n)
	}
	sort.Strings(rest)
	for _, n := range rest {
		out = append(out, byName[n])
	}
	return out, nil
}

func (r *report) findPage(q string) (pageInfo, error) {
	ps, err := r.pages()
	if err != nil {
		return pageInfo{}, err
	}
	if len(ps) == 0 {
		return pageInfo{}, errors.New("el reporte no tiene páginas")
	}
	if q == "" {
		return ps[0], nil
	}
	for _, p := range ps {
		if p.Name == q || strings.EqualFold(p.DisplayName, q) {
			return p, nil
		}
	}
	var names []string
	for _, p := range ps {
		names = append(names, fmt.Sprintf("%q", p.DisplayName))
	}
	return pageInfo{}, fmt.Errorf("página %q no encontrada. Páginas: %s", q, strings.Join(names, ", "))
}

func (r *report) visuals(page string) ([]visualInfo, error) {
	var pages []pageInfo
	if page != "" {
		p, err := r.findPage(page)
		if err != nil {
			return nil, err
		}
		pages = []pageInfo{p}
	} else {
		var err error
		if pages, err = r.pages(); err != nil {
			return nil, err
		}
	}
	var out []visualInfo
	for _, p := range pages {
		var vs []visualInfo
		var err error
		if r.kind == "pbir" {
			vs, err = r.pbirVisuals(p)
		} else {
			vs = r.legacyVisuals(p)
		}
		if err != nil {
			return nil, err
		}
		sort.SliceStable(vs, func(i, j int) bool {
			if vs[i].Y != vs[j].Y {
				return vs[i].Y < vs[j].Y
			}
			return vs[i].X < vs[j].X
		})
		out = append(out, vs...)
	}
	return out, nil
}

func (r *report) pbirVisuals(p pageInfo) ([]visualInfo, error) {
	base := filepath.Join(r.pagesDir(), p.Name, "visuals")
	entries, err := os.ReadDir(base)
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	} else if err != nil {
		return nil, err
	}
	var out []visualInfo
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		vj, err := readJSON(filepath.Join(base, e.Name(), "visual.json"))
		if err != nil {
			continue
		}
		v := visualInfo{
			Page:   p.DisplayName,
			Name:   e.Name(),
			X:      numVal(dig(vj, "position", "x")),
			Y:      numVal(dig(vj, "position", "y")),
			Z:      numVal(dig(vj, "position", "z")),
			Width:  numVal(dig(vj, "position", "width")),
			Height: numVal(dig(vj, "position", "height")),
			Group:  str(vj["parentGroupName"]),
			dir:    filepath.Join(base, e.Name()),
			raw:    vj,
		}
		if vj["visualGroup"] != nil {
			v.Type = "group"
			v.Title = str(dig(vj, "visualGroup", "displayName"))
		} else {
			v.Type = str(dig(vj, "visual", "visualType"))
			v.Title = literalText(dig(vj, "visual", "visualContainerObjects", "title", 0, "properties", "text"))
			qs, _ := dig(vj, "visual", "query", "queryState").(map[string]any)
			for _, role := range sortedKeys(qs) {
				for _, pr := range listOf(dig(qs[role], "projections")) {
					v.proj = append(v.proj, projInfo{role, str(dig(pr, "queryRef")), fieldFromExpr(dig(pr, "field"), nil)})
				}
			}
		}
		v.fillFields()
		out = append(out, v)
	}
	return out, nil
}

func (r *report) legacyVisuals(p pageInfo) []visualInfo {
	var out []visualInfo
	for _, s := range listOf(r.layout["sections"]) {
		if str(dig(s, "name")) != p.Name {
			continue
		}
		for _, vc := range listOf(dig(s, "visualContainers")) {
			cfg, _ := decodeJSON([]byte(str(dig(vc, "config"))))
			v := visualInfo{
				Page:   p.DisplayName,
				Name:   str(cfg["name"]),
				X:      numVal(dig(vc, "x")),
				Y:      numVal(dig(vc, "y")),
				Z:      numVal(dig(vc, "z")),
				Width:  numVal(dig(vc, "width")),
				Height: numVal(dig(vc, "height")),
				Group:  str(cfg["parentGroupName"]),
				raw:    cfg,
			}
			if cfg["singleVisualGroup"] != nil {
				v.Type = "group"
				v.Title = str(dig(cfg, "singleVisualGroup", "displayName"))
			} else {
				sv := cfg["singleVisual"]
				v.Type = str(dig(sv, "visualType"))
				v.Title = literalText(dig(sv, "vcObjects", "title", 0, "properties", "text"))
				// Los campos están en prototypeQuery.Select; los roles en projections.
				aliases := map[string]string{}
				for _, f := range listOf(dig(sv, "prototypeQuery", "From")) {
					aliases[str(dig(f, "Name"))] = str(dig(f, "Entity"))
				}
				selects := map[string]*fieldRef{}
				for _, s := range listOf(dig(sv, "prototypeQuery", "Select")) {
					selects[str(dig(s, "Name"))] = fieldFromExpr(s, aliases)
				}
				projs, _ := dig(sv, "projections").(map[string]any)
				for _, role := range sortedKeys(projs) {
					for _, pr := range listOf(projs[role]) {
						qr := str(dig(pr, "queryRef"))
						v.proj = append(v.proj, projInfo{role, qr, selects[qr]})
					}
				}
			}
			v.fillFields()
			out = append(out, v)
		}
	}
	return out
}

func (v *visualInfo) fillFields() {
	for _, p := range v.proj {
		if v.Fields == nil {
			v.Fields = map[string][]string{}
		}
		label := p.QueryRef
		if p.Field != nil && p.Field.Kind != "other" && p.Field.Table != "" {
			label = p.Field.String()
		}
		v.Fields[p.Role] = append(v.Fields[p.Role], label)
	}
}

func (r *report) findVisual(page, q string) (visualInfo, error) {
	if q == "" {
		return visualInfo{}, errors.New("falta visual (nombre o título del gráfico)")
	}
	vs, err := r.visuals(page)
	if err != nil {
		return visualInfo{}, err
	}
	var byTitle []visualInfo
	for _, v := range vs {
		if v.Name == q {
			return v, nil
		}
		if strings.EqualFold(v.Title, q) {
			byTitle = append(byTitle, v)
		}
	}
	switch len(byTitle) {
	case 1:
		return byTitle[0], nil
	case 0:
		return visualInfo{}, fmt.Errorf("gráfico %q no encontrado; use list_visuals para ver nombres y títulos", q)
	}
	return visualInfo{}, fmt.Errorf("hay %d gráficos titulados %q; indique el nombre (id) o la página", len(byTitle), q)
}

// ── DAX para leer los datos de un gráfico ────────────────────────────

func daxTable(t string) string { return "'" + strings.ReplaceAll(t, "'", "''") + "'" }
func daxCol(f fieldRef) string {
	return daxTable(f.Table) + "[" + strings.ReplaceAll(f.Field, "]", "]]") + "]"
}
func daxString(s string) string { return `"` + strings.ReplaceAll(s, `"`, `""`) + `"` }

func visualDAX(v visualInfo, top int) (string, []string) {
	var groups, values, notes []string
	seen := map[string]bool{}
	for _, p := range v.proj {
		f := p.Field
		if f == nil || f.Kind == "other" || f.Table == "" {
			notes = append(notes, fmt.Sprintf("El campo %q (rol %s) no se puede traducir a DAX (p. ej. jerarquía de fechas automática); se omitió.", p.QueryRef, p.Role))
			continue
		}
		var expr string
		switch f.Kind {
		case "column":
			if !seen[daxCol(*f)] {
				seen[daxCol(*f)] = true
				groups = append(groups, daxCol(*f))
			}
			continue
		case "measure":
			expr = daxCol(*f)
		case "aggregation":
			i, _ := aggByKey(f.Agg)
			expr = aggs[i].dax + "(" + daxCol(*f) + ")"
		}
		if !seen[p.QueryRef] {
			seen[p.QueryRef] = true
			name := p.QueryRef
			if f.Kind == "measure" {
				name = f.Field
			}
			values = append(values, daxString(name)+", "+expr)
		}
	}
	if len(groups) == 0 && len(values) == 0 {
		return "", append(notes, "El gráfico no tiene campos de datos.")
	}
	var q string
	if len(groups) == 0 {
		q = "EVALUATE\nROW(\n    " + strings.Join(values, ",\n    ") + "\n)"
	} else {
		body := "SUMMARIZECOLUMNS(\n        " + strings.Join(append(append([]string{}, groups...), values...), ",\n        ") + "\n    )"
		q = fmt.Sprintf("EVALUATE\nTOPN(\n    %d,\n    %s\n)\nORDER BY %s", top, body, groups[0])
	}
	notes = append(notes, "La consulta no aplica los filtros del gráfico, de la página ni de las segmentaciones.")
	return q, notes
}

// ── Escritura (PBIR) ─────────────────────────────────────────────────

type typeSpec struct {
	visualType string
	category   string // rol para "category"
	values     string // rol para "values"
	legend     string // rol para "legend"
	w, h       float64
}

var visualTypes = map[string]typeSpec{
	"column":        {"clusteredColumnChart", "Category", "Y", "Series", 480, 300},
	"stackedcolumn": {"columnChart", "Category", "Y", "Series", 480, 300},
	"bar":           {"clusteredBarChart", "Category", "Y", "Series", 480, 300},
	"stackedbar":    {"barChart", "Category", "Y", "Series", 480, 300},
	"line":          {"lineChart", "Category", "Y", "Series", 480, 300},
	"area":          {"areaChart", "Category", "Y", "Series", 480, 300},
	"combo":         {"lineClusteredColumnComboChart", "Category", "Y", "Series", 480, 300},
	"pie":           {"pieChart", "Category", "Y", "", 360, 300},
	"donut":         {"donutChart", "Category", "Y", "", 360, 300},
	"treemap":       {"treemap", "Group", "Values", "", 480, 300},
	"funnel":        {"funnel", "Category", "Y", "", 400, 300},
	"waterfall":     {"waterfallChart", "Category", "Y", "Breakdown", 480, 300},
	"scatter":       {"scatterChart", "Category", "", "Series", 480, 300},
	"gauge":         {"gauge", "", "Y", "", 300, 200},
	"card":          {"card", "", "Values", "", 220, 120},
	"multirowcard":  {"multiRowCard", "", "Values", "", 300, 160},
	"table":         {"tableEx", "Values", "Values", "", 480, 300},
	"matrix":        {"pivotTable", "Rows", "Values", "Columns", 480, 300},
	"slicer":        {"slicer", "Values", "", "", 220, 200},
}

func specRoles(s typeSpec) map[string]bool {
	r := map[string]bool{}
	for _, k := range []string{s.category, s.values, s.legend} {
		if k != "" {
			r[k] = true
		}
	}
	switch s.visualType {
	case "scatterChart":
		r["X"], r["Y"], r["Size"] = true, true, true
	case "lineClusteredColumnComboChart":
		r["Y2"] = true
	}
	return r
}

func visualTypeNames() []string {
	var n []string
	for k := range visualTypes {
		n = append(n, k)
	}
	sort.Strings(n)
	return n
}

func lookupType(t string) (typeSpec, bool) {
	k := strings.ToLower(strings.TrimSuffix(strings.ReplaceAll(strings.ReplaceAll(t, " ", ""), "_", ""), "chart"))
	if s, ok := visualTypes[k]; ok {
		return s, true
	}
	for _, s := range visualTypes { // también acepta el nombre interno (clusteredColumnChart, ...)
		if strings.EqualFold(s.visualType, t) {
			return s, true
		}
	}
	return typeSpec{}, false
}

type createVisualReq struct {
	Page, Type, Title   string
	Category, Values    []string
	Legend, LineValues  []string
	Roles               map[string][]string
	X, Y, Width, Height *float64
}

func (r *report) createVisual(q createVisualReq) (map[string]any, error) {
	if err := r.requireWritable(); err != nil {
		return nil, err
	}
	page, err := r.findPage(q.Page)
	if err != nil {
		return nil, err
	}
	spec, known := lookupType(q.Type)
	if !known {
		if len(q.Roles) == 0 {
			return nil, fmt.Errorf("tipo %q desconocido. Tipos: %s (o indique roles para un visual personalizado)", q.Type, strings.Join(visualTypeNames(), ", "))
		}
		spec = typeSpec{visualType: q.Type, w: 480, h: 300}
	}
	m, _ := r.model() // sin modelo local se aceptan los campos tal cual

	// Asignar campos a roles.
	type roleField struct {
		role  string
		field string
	}
	var assigned []roleField
	add := func(role string, fields []string) error {
		if len(fields) > 0 && role == "" {
			return fmt.Errorf("el tipo %s no admite ese grupo de campos; use roles", q.Type)
		}
		for _, f := range fields {
			assigned = append(assigned, roleField{role, f})
		}
		return nil
	}
	if spec.visualType == "scatterChart" && len(q.Values) > 0 {
		if len(q.Values) < 2 {
			return nil, errors.New("scatter requiere dos valores: [eje X, eje Y]")
		}
		add("X", q.Values[:1])
		add("Y", q.Values[1:2])
		if len(q.Values) > 2 {
			add("Size", q.Values[2:3])
		}
	} else if err := add(spec.values, q.Values); err != nil {
		return nil, err
	}
	if err := add(spec.category, q.Category); err != nil {
		return nil, err
	}
	if err := add(spec.legend, q.Legend); err != nil {
		return nil, err
	}
	if len(q.LineValues) > 0 {
		if spec.visualType != "lineClusteredColumnComboChart" {
			return nil, errors.New("line_values solo aplica al tipo combo")
		}
		add("Y2", q.LineValues)
	}
	for _, role := range sortedKeys(q.Roles) {
		add(role, q.Roles[role])
	}
	if len(assigned) == 0 {
		return nil, errors.New("indique al menos un campo (category, values, legend o roles)")
	}

	// Orden de roles: categoría primero (como hace Desktop).
	queryState := map[string]any{}
	var used []string
	for _, a := range assigned {
		valueRole := a.role != spec.category && a.role != spec.legend && spec.visualType != "tableEx" && spec.visualType != "slicer"
		f, err := resolveField(a.field, m, valueRole)
		if err != nil {
			return nil, err
		}
		rs, _ := queryState[a.role].(map[string]any)
		if rs == nil {
			rs = map[string]any{"projections": []any{}}
			queryState[a.role] = rs
		}
		rs["projections"] = append(rs["projections"].([]any), projection(f))
		used = append(used, a.role+": "+f.String())
	}

	// Posición.
	existing, err := r.pbirVisuals(page)
	if err != nil {
		return nil, err
	}
	w, h := spec.w, spec.h
	if q.Width != nil {
		w = *q.Width
	}
	if q.Height != nil {
		h = *q.Height
	}
	var x, y float64
	if q.X != nil && q.Y != nil {
		x, y = *q.X, *q.Y
	} else {
		x, y = freeSpot(existing, page, w, h)
	}
	maxZ := -1000.0
	for _, v := range existing {
		if v.Z > maxZ {
			maxZ = v.Z
		}
	}

	name := newID()
	visual := map[string]any{
		"visualType":              spec.visualType,
		"query":                   map[string]any{"queryState": queryState},
		"drillFilterOtherVisuals": true,
	}
	if q.Title != "" {
		visual["visualContainerObjects"] = map[string]any{"title": titleObject(q.Title)}
	}
	vj := map[string]any{
		"$schema": r.schemaFor("visual.json", defaultVisualSchema),
		"name":    name,
		"position": map[string]any{
			"x": x, "y": y, "z": maxZ + 1000, "width": w, "height": h, "tabOrder": maxZ + 1000,
		},
		"visual": visual,
	}
	dir := filepath.Join(r.pagesDir(), page.Name, "visuals", name)
	if err := writeJSON(filepath.Join(dir, "visual.json"), vj); err != nil {
		return nil, err
	}
	return map[string]any{
		"ok": true, "page": page.DisplayName, "visual": name, "visualType": spec.visualType,
		"title": q.Title, "position": map[string]float64{"x": x, "y": y, "width": w, "height": h},
		"fields": used, "file": filepath.Join(dir, "visual.json"), "note": reloadNote,
	}, nil
}

func titleObject(t string) []any {
	return []any{map[string]any{"properties": map[string]any{
		"show": map[string]any{"expr": map[string]any{"Literal": map[string]any{"Value": "true"}}},
		"text": map[string]any{"expr": map[string]any{"Literal": map[string]any{"Value": "'" + strings.ReplaceAll(t, "'", "''") + "'"}}},
	}}}
}

// freeSpot busca el primer hueco libre (de arriba a la izquierda) donde quepa w×h.
func freeSpot(vs []visualInfo, p pageInfo, w, h float64) (float64, float64) {
	pw, ph := p.Width, p.Height
	if pw <= 0 {
		pw = 1280
	}
	if ph <= 0 {
		ph = 720
	}
	const step, margin = 10.0, 10.0
	overlaps := func(x, y float64) bool {
		for _, v := range vs {
			if v.Group != "" {
				continue
			}
			if x < v.X+v.Width+margin && x+w+margin > v.X && y < v.Y+v.Height+margin && y+h+margin > v.Y {
				return true
			}
		}
		return false
	}
	for y := margin; y+h <= ph; y += step {
		for x := margin; x+w <= pw; x += step {
			if !overlaps(x, y) {
				return x, y
			}
		}
	}
	// Sin espacio: debajo de todo (la página puede crecer o el usuario lo mueve).
	maxY := 0.0
	for _, v := range vs {
		if v.Y+v.Height > maxY {
			maxY = v.Y + v.Height
		}
	}
	return margin, maxY + margin
}

type updateVisualReq struct {
	Page, Visual, Title, Type string
	X, Y, Width, Height       *float64
}

func (r *report) updateVisual(q updateVisualReq) (map[string]any, error) {
	if err := r.requireWritable(); err != nil {
		return nil, err
	}
	v, err := r.findVisual(q.Page, q.Visual)
	if err != nil {
		return nil, err
	}
	vj := v.raw
	pos, _ := vj["position"].(map[string]any)
	if pos == nil {
		pos = map[string]any{}
		vj["position"] = pos
	}
	for k, p := range map[string]*float64{"x": q.X, "y": q.Y, "width": q.Width, "height": q.Height} {
		if p != nil {
			pos[k] = *p
		}
	}
	vis, _ := vj["visual"].(map[string]any)
	if q.Title != "" {
		if vis == nil {
			return nil, errors.New("solo se puede cambiar el título de gráficos, no de grupos")
		}
		objs, _ := vis["visualContainerObjects"].(map[string]any)
		if objs == nil {
			objs = map[string]any{}
			vis["visualContainerObjects"] = objs
		}
		objs["title"] = titleObject(q.Title)
	}
	if q.Type != "" {
		spec, ok := lookupType(q.Type)
		if !ok || vis == nil {
			return nil, fmt.Errorf("tipo %q desconocido. Tipos: %s", q.Type, strings.Join(visualTypeNames(), ", "))
		}
		allowed := specRoles(spec)
		qs, _ := dig(vis, "query", "queryState").(map[string]any)
		for role := range qs {
			if !allowed[role] {
				return nil, fmt.Errorf("no se puede convertir a %s: el rol %q no existe en ese tipo; cree un gráfico nuevo con create_visual", spec.visualType, role)
			}
		}
		vis["visualType"] = spec.visualType
	}
	if err := writeJSON(filepath.Join(v.dir, "visual.json"), vj); err != nil {
		return nil, err
	}
	return map[string]any{"ok": true, "visual": v.Name, "page": v.Page, "note": reloadNote}, nil
}

func (r *report) deleteVisual(page, visual string) (map[string]any, error) {
	if err := r.requireWritable(); err != nil {
		return nil, err
	}
	v, err := r.findVisual(page, visual)
	if err != nil {
		return nil, err
	}
	if v.Type == "group" {
		return nil, errors.New("el visual es un grupo; elimine primero los gráficos que contiene")
	}
	if err := os.RemoveAll(v.dir); err != nil {
		return nil, err
	}
	return map[string]any{"ok": true, "deleted": v.Name, "title": v.Title, "page": v.Page, "note": reloadNote}, nil
}

func (r *report) createPage(displayName string, w, h float64) (map[string]any, error) {
	if err := r.requireWritable(); err != nil {
		return nil, err
	}
	if displayName == "" {
		return nil, errors.New("falta display_name")
	}
	if ps, _ := r.pages(); ps != nil {
		for _, p := range ps {
			if strings.EqualFold(p.DisplayName, displayName) {
				return nil, fmt.Errorf("ya existe una página llamada %q", p.DisplayName)
			}
		}
	}
	if w <= 0 {
		w = 1280
	}
	if h <= 0 {
		h = 720
	}
	name := newID()
	pj := map[string]any{
		"$schema":       r.schemaFor("page.json", defaultPageSchema),
		"name":          name,
		"displayName":   displayName,
		"displayOption": "FitToPage",
		"height":        h,
		"width":         w,
	}
	if err := writeJSON(filepath.Join(r.pagesDir(), name, "page.json"), pj); err != nil {
		return nil, err
	}
	metaPath := filepath.Join(r.pagesDir(), "pages.json")
	meta, err := readJSON(metaPath)
	if err != nil {
		meta = map[string]any{"$schema": defaultPagesSchema}
	}
	meta["pageOrder"] = append(listOf(meta["pageOrder"]), name)
	if str(meta["activePageName"]) == "" {
		meta["activePageName"] = name
	}
	if err := writeJSON(metaPath, meta); err != nil {
		return nil, err
	}
	return map[string]any{"ok": true, "page": name, "displayName": displayName, "width": w, "height": h, "note": reloadNote}, nil
}

func (r *report) deletePage(q string) (map[string]any, error) {
	if err := r.requireWritable(); err != nil {
		return nil, err
	}
	if q == "" {
		return nil, errors.New("falta page")
	}
	p, err := r.findPage(q)
	if err != nil {
		return nil, err
	}
	ps, _ := r.pages()
	if len(ps) <= 1 {
		return nil, errors.New("no se puede eliminar la única página del reporte")
	}
	if err := os.RemoveAll(filepath.Join(r.pagesDir(), p.Name)); err != nil {
		return nil, err
	}
	metaPath := filepath.Join(r.pagesDir(), "pages.json")
	if meta, err := readJSON(metaPath); err == nil {
		var order []any
		for _, n := range listOf(meta["pageOrder"]) {
			if str(n) != p.Name {
				order = append(order, n)
			}
		}
		meta["pageOrder"] = order
		if str(meta["activePageName"]) == p.Name && len(order) > 0 {
			meta["activePageName"] = order[0]
		}
		if err := writeJSON(metaPath, meta); err != nil {
			return nil, err
		}
	}
	return map[string]any{"ok": true, "deleted": p.DisplayName, "note": reloadNote}, nil
}

// schemaFor reutiliza la versión de $schema que ya usa el reporte para ese tipo de archivo,
// así Power BI Desktop lo acepta aunque el reporte se haya creado con otra versión.
func (r *report) schemaFor(file, def string) string {
	var found string
	filepath.WalkDir(r.pagesDir(), func(p string, d os.DirEntry, err error) error {
		if err != nil || d.IsDir() || d.Name() != file {
			return nil
		}
		if j, err := readJSON(p); err == nil && str(j["$schema"]) != "" {
			found = str(j["$schema"])
			return filepath.SkipAll
		}
		return nil
	})
	if found != "" {
		return found
	}
	return def
}

// ── Utilidades JSON ──────────────────────────────────────────────────

func trimBOM(b []byte) []byte { return bytes.TrimPrefix(b, []byte("\xef\xbb\xbf")) }

func decodeUTF16(b []byte) []byte {
	if len(b) >= 2 && b[0] == 0xff && b[1] == 0xfe {
		b = b[2:]
	} else if len(b) < 2 || b[1] != 0 {
		return trimBOM(b) // ya es UTF-8
	}
	u := make([]uint16, len(b)/2)
	for i := range u {
		u[i] = uint16(b[2*i]) | uint16(b[2*i+1])<<8
	}
	return []byte(string(utf16.Decode(u)))
}

func decodeJSON(b []byte) (map[string]any, error) {
	d := json.NewDecoder(bytes.NewReader(trimBOM(b)))
	d.UseNumber() // conserva los números tal cual al reescribir
	m := map[string]any{}
	if err := d.Decode(&m); err != nil {
		return nil, err
	}
	return m, nil
}

func readJSON(p string) (map[string]any, error) {
	b, err := os.ReadFile(p)
	if err != nil {
		return nil, err
	}
	return decodeJSON(b)
}

func writeJSON(p string, v any) error {
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		return err
	}
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	enc.SetEscapeHTML(false)
	enc.SetIndent("", "  ")
	if err := enc.Encode(v); err != nil {
		return err
	}
	tmp := p + ".tmp"
	if err := os.WriteFile(tmp, buf.Bytes(), 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, p)
}

func dig(v any, path ...any) any {
	for _, k := range path {
		switch key := k.(type) {
		case string:
			m, ok := v.(map[string]any)
			if !ok {
				return nil
			}
			v = m[key]
		case int:
			l, ok := v.([]any)
			if !ok || key >= len(l) {
				return nil
			}
			v = l[key]
		}
	}
	return v
}

func listOf(v any) []any { l, _ := v.([]any); return l }

func str(v any) string {
	switch s := v.(type) {
	case string:
		return s
	case json.Number:
		return s.String()
	}
	return ""
}

func numVal(v any) float64 {
	switch n := v.(type) {
	case json.Number:
		f, _ := n.Float64()
		return f
	case float64:
		return n
	case int:
		return float64(n)
	}
	return 0
}

// literalText extrae el texto de {"expr":{"Literal":{"Value":"'Título'"}}}.
func literalText(v any) string {
	s := str(dig(v, "expr", "Literal", "Value"))
	if len(s) >= 2 && s[0] == '\'' && s[len(s)-1] == '\'' {
		s = strings.ReplaceAll(s[1:len(s)-1], "''", "'")
	}
	return s
}

func sortedKeys[V any](m map[string]V) []string {
	k := make([]string, 0, len(m))
	for key := range m {
		k = append(k, key)
	}
	sort.Strings(k)
	return k
}

func firstLine(s string) string {
	s = strings.TrimSpace(s)
	if i := strings.IndexByte(s, '\n'); i >= 0 {
		return strings.TrimSpace(s[:i]) + " ..."
	}
	return s
}

func newID() string {
	b := make([]byte, 10)
	rand.Read(b)
	return hex.EncodeToString(b)
}
