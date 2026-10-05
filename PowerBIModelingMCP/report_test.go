package main

import (
	"archive/zip"
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"unicode/utf16"
)

const salesTMDL = "table Sales\n" +
	"\tlineageTag: 1\n\n" +
	"\tmeasure 'Total Sales' = SUM(Sales[Amount])\n" +
	"\t\tformatString: #,0\n\n" +
	"\tcolumn Region\n" +
	"\t\tdataType: string\n" +
	"\t\tsummarizeBy: none\n\n" +
	"\tcolumn Amount\n" +
	"\t\tdataType: decimal\n" +
	"\t\tsummarizeBy: sum\n\n" +
	"\tcolumn 'Order Date'\n" +
	"\t\tdataType: dateTime\n" +
	"\t\tisHidden\n\n" +
	"\tpartition Sales = m\n" +
	"\t\tmode: import\n"

func write(t *testing.T, p, content string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(p, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}
}

// newProject crea un proyecto .pbip PBIR mínimo con una página y un gráfico.
func newProject(t *testing.T) string {
	d := t.TempDir()
	write(t, filepath.Join(d, "Ventas.pbip"), `{"version":"1.0","artifacts":[{"report":{"path":"Ventas.Report"}}]}`)
	write(t, filepath.Join(d, "Ventas.Report", "definition.pbir"), `{"version":"4.0","datasetReference":{"byPath":{"path":"../Ventas.SemanticModel"}}}`)
	pages := filepath.Join(d, "Ventas.Report", "definition", "pages")
	write(t, filepath.Join(pages, "pages.json"), `{"$schema":"https://x/pagesMetadata/1.0.0/schema.json","pageOrder":["p1"],"activePageName":"p1"}`)
	write(t, filepath.Join(pages, "p1", "page.json"), `{"$schema":"https://x/page/2.0.0/schema.json","name":"p1","displayName":"Resumen","displayOption":"FitToPage","height":720,"width":1280}`)
	write(t, filepath.Join(pages, "p1", "visuals", "v1", "visual.json"), `{
  "$schema": "https://x/visualContainer/2.1.0/schema.json",
  "name": "v1",
  "position": {"x": 10, "y": 10, "z": 0, "width": 400, "height": 300, "tabOrder": 0},
  "visual": {
    "visualType": "card",
    "query": {"queryState": {"Values": {"projections": [
      {"field": {"Measure": {"Expression": {"SourceRef": {"Entity": "Sales"}}, "Property": "Total Sales"}}, "queryRef": "Sales.Total Sales", "nativeQueryRef": "Total Sales"}
    ]}}},
    "visualContainerObjects": {"title": [{"properties": {"text": {"expr": {"Literal": {"Value": "'Ventas ''totales'''"}}}}}]}
  }
}`)
	write(t, filepath.Join(d, "Ventas.SemanticModel", "definition", "tables", "Sales.tmdl"), salesTMDL)
	return filepath.Join(d, "Ventas.pbip")
}

func TestReadPBIR(t *testing.T) {
	r, err := openReport(newProject(t))
	if err != nil {
		t.Fatal(err)
	}
	if r.kind != "pbir" {
		t.Fatalf("kind = %s", r.kind)
	}
	ps, _ := r.pages()
	if len(ps) != 1 || ps[0].DisplayName != "Resumen" || ps[0].Visuals != 1 {
		t.Fatalf("pages = %+v", ps)
	}
	vs, err := r.visuals("")
	if err != nil || len(vs) != 1 {
		t.Fatalf("visuals = %+v, %v", vs, err)
	}
	v := vs[0]
	if v.Type != "card" || v.Title != "Ventas 'totales'" || v.Fields["Values"][0] != "measure('Sales'[Total Sales])" {
		t.Fatalf("visual = %+v", v)
	}
	m, err := r.model()
	if err != nil {
		t.Fatal(err)
	}
	tb := m.table("sales")
	if tb == nil || len(tb.Columns) != 3 || len(tb.Measures) != 1 || tb.Columns[2].Name != "Order Date" || !tb.Columns[2].Hidden || tb.Columns[1].SummarizeBy != "sum" {
		t.Fatalf("model = %+v", m)
	}
}

func TestCreateUpdateDeleteVisual(t *testing.T) {
	path := newProject(t)
	r, _ := openReport(path)

	res, err := r.createVisual(createVisualReq{
		Page: "resumen", Type: "column", Title: "Ventas por región",
		Category: []string{"sales[region]"}, Values: []string{"Sales[Total Sales]", "Sales[Amount]"},
	})
	if err != nil {
		t.Fatal(err)
	}
	name := res["visual"].(string)
	pos := res["position"].(map[string]float64)
	if pos["x"] < 410 { // no debe solaparse con el gráfico existente (x 10..410)
		t.Fatalf("posición solapada: %+v", pos)
	}

	vj, err := readJSON(filepath.Join(filepath.Dir(path), "Ventas.Report", "definition", "pages", "p1", "visuals", name, "visual.json"))
	if err != nil {
		t.Fatal(err)
	}
	if str(vj["$schema"]) != "https://x/visualContainer/2.1.0/schema.json" {
		t.Errorf("schema no reutilizado: %v", vj["$schema"])
	}
	if str(dig(vj, "visual", "visualType")) != "clusteredColumnChart" {
		t.Errorf("visualType = %v", dig(vj, "visual", "visualType"))
	}
	if got := str(dig(vj, "visual", "query", "queryState", "Category", "projections", 0, "queryRef")); got != "Sales.Region" {
		t.Errorf("category queryRef = %s", got)
	}
	if dig(vj, "visual", "query", "queryState", "Y", "projections", 0, "field", "Measure") == nil {
		t.Errorf("Total Sales debería ser medida")
	}
	if got := str(dig(vj, "visual", "query", "queryState", "Y", "projections", 1, "queryRef")); got != "Sum(Sales.Amount)" {
		t.Errorf("Amount debería sumarse por summarizeBy: %s", got)
	}

	// Leer de vuelta + DAX.
	r, _ = openReport(path)
	v, err := r.findVisual("", "ventas por región")
	if err != nil {
		t.Fatal(err)
	}
	dax, _ := visualDAX(v, 100)
	for _, want := range []string{"SUMMARIZECOLUMNS(", "'Sales'[Region]", `"Total Sales", 'Sales'[Total Sales]`, `"Sum(Sales.Amount)", SUM('Sales'[Amount])`, "TOPN(\n    100", "ORDER BY 'Sales'[Region]"} {
		if !strings.Contains(dax, want) {
			t.Errorf("DAX sin %q:\n%s", want, dax)
		}
	}
	card, _ := r.findVisual("", "v1")
	if dax, _ := visualDAX(card, 10); !strings.HasPrefix(dax, "EVALUATE\nROW(") {
		t.Errorf("card DAX = %s", dax)
	}

	// Actualizar.
	x := 600.0
	if _, err := r.updateVisual(updateVisualReq{Visual: name, Title: "Nuevo", Type: "bar", X: &x}); err != nil {
		t.Fatal(err)
	}
	if _, err := r.updateVisual(updateVisualReq{Visual: name, Type: "card"}); err == nil {
		t.Error("cambiar a card debería fallar (roles incompatibles)")
	}
	r, _ = openReport(path)
	v, _ = r.findVisual("", name)
	if v.Title != "Nuevo" || v.Type != "clusteredBarChart" || v.X != 600 {
		t.Errorf("update = %+v", v)
	}
	// El JSON reescrito conserva enteros (UseNumber).
	b, _ := os.ReadFile(filepath.Join(v.dir, "visual.json"))
	if strings.Contains(string(b), "e+") {
		t.Errorf("números reformateados: %s", b)
	}

	if _, err := r.deleteVisual("", name); err != nil {
		t.Fatal(err)
	}
	if vs, _ := r.visuals(""); len(vs) != 1 {
		t.Errorf("tras borrar quedan %d", len(vs))
	}
}

func TestCreateVisualErrors(t *testing.T) {
	r, _ := openReport(newProject(t))
	cases := []createVisualReq{
		{Type: "column", Category: []string{"Nope[Region]"}},
		{Type: "column", Category: []string{"Sales[Nope]"}},
		{Type: "column", Values: []string{"sum(Sales[Total Sales])"}},
		{Type: "pie", Legend: []string{"Sales[Region]"}},
		{Type: "rocket", Values: []string{"Sales[Amount]"}},
		{Type: "column"},
		{Type: "column", Page: "No existe", Values: []string{"Sales[Amount]"}},
	}
	for _, c := range cases {
		if _, err := r.createVisual(c); err == nil {
			t.Errorf("se esperaba error para %+v", c)
		}
	}
}

func TestPages(t *testing.T) {
	path := newProject(t)
	r, _ := openReport(path)
	res, err := r.createPage("Detalle", 0, 0)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := r.createPage("detalle", 0, 0); err == nil {
		t.Error("página duplicada debería fallar")
	}
	ps, _ := r.pages()
	if len(ps) != 2 || ps[1].DisplayName != "Detalle" || ps[1].Width != 1280 {
		t.Fatalf("pages = %+v", ps)
	}
	if _, err := r.createVisual(createVisualReq{Page: "Detalle", Type: "card", Values: []string{"Sales[Total Sales]"}}); err != nil {
		t.Fatal(err)
	}
	pj, _ := readJSON(filepath.Join(r.pagesDir(), res["page"].(string), "page.json"))
	if str(pj["$schema"]) != "https://x/page/2.0.0/schema.json" {
		t.Errorf("page schema = %v", pj["$schema"])
	}
	if _, err := r.deletePage("Resumen"); err != nil {
		t.Fatal(err)
	}
	meta, _ := readJSON(filepath.Join(r.pagesDir(), "pages.json"))
	if order := listOf(meta["pageOrder"]); len(order) != 1 || str(meta["activePageName"]) != res["page"] {
		t.Errorf("pages.json = %+v", meta)
	}
	if _, err := r.deletePage("Detalle"); err == nil {
		t.Error("borrar la única página debería fallar")
	}
}

func TestReadPbix(t *testing.T) {
	cfg, _ := json.Marshal(map[string]any{
		"name": "abc",
		"singleVisual": map[string]any{
			"visualType":  "lineChart",
			"projections": map[string]any{"Category": []any{map[string]any{"queryRef": "s.Region"}}, "Y": []any{map[string]any{"queryRef": "Sum(s.Amount)"}}},
			"prototypeQuery": map[string]any{
				"From": []any{map[string]any{"Name": "s", "Entity": "Sales", "Type": 0}},
				"Select": []any{
					map[string]any{"Column": map[string]any{"Expression": map[string]any{"SourceRef": map[string]any{"Source": "s"}}, "Property": "Region"}, "Name": "s.Region"},
					map[string]any{"Aggregation": map[string]any{"Expression": map[string]any{"Column": map[string]any{"Expression": map[string]any{"SourceRef": map[string]any{"Source": "s"}}, "Property": "Amount"}}, "Function": 1}, "Name": "Sum(s.Amount)"},
				},
			},
		},
	})
	layout, _ := json.Marshal(map[string]any{"sections": []any{map[string]any{
		"name": "ReportSection", "displayName": "Página 1", "width": 1280, "height": 720,
		"visualContainers": []any{map[string]any{"x": 5, "y": 6, "z": 0, "width": 300, "height": 200, "config": string(cfg)}},
	}}})
	u := utf16.Encode([]rune(string(layout)))
	var le bytes.Buffer
	le.Write([]byte{0xff, 0xfe})
	for _, c := range u {
		le.Write([]byte{byte(c), byte(c >> 8)})
	}
	p := filepath.Join(t.TempDir(), "x.pbix")
	f, _ := os.Create(p)
	zw := zip.NewWriter(f)
	w, _ := zw.Create("Report/Layout")
	w.Write(le.Bytes())
	zw.Close()
	f.Close()

	r, err := openReport(p)
	if err != nil {
		t.Fatal(err)
	}
	vs, err := r.visuals("Página 1")
	if err != nil || len(vs) != 1 {
		t.Fatalf("visuals = %+v, %v", vs, err)
	}
	if vs[0].Type != "lineChart" || vs[0].Fields["Y"][0] != "avg('Sales'[Amount])" || vs[0].X != 5 {
		t.Fatalf("visual = %+v", vs[0])
	}
	dax, _ := visualDAX(vs[0], 500)
	if !strings.Contains(dax, "AVERAGE('Sales'[Amount])") || !strings.Contains(dax, "'Sales'[Region]") {
		t.Errorf("dax = %s", dax)
	}
	if _, err := r.createVisual(createVisualReq{Type: "card", Values: []string{"Sales[Amount]"}}); err == nil || !strings.Contains(err.Error(), "solo lectura") {
		t.Errorf("pbix debería ser solo lectura: %v", err)
	}
}

func TestResolveFieldWithoutModel(t *testing.T) {
	f, err := resolveField("'My ''Table'''[A]]B]", nil, true)
	if err != nil || f.Table != "My 'Table'" || f.Field != "A]B" || f.Kind != "column" {
		t.Fatalf("%+v %v", f, err)
	}
	if f, _ := resolveField("measure(T[M])", nil, true); f.Kind != "measure" {
		t.Errorf("%+v", f)
	}
	if f, _ := resolveField("countd(T[c])", nil, true); f.Agg != "countd" || projection(f)["queryRef"] != "Count(T.c)" {
		t.Errorf("%+v", projection(f))
	}
	if _, err := resolveField("foo(T[c])", nil, true); err == nil {
		t.Error("agregación inválida debería fallar")
	}
}

func TestMCPProtocol(t *testing.T) {
	path := newProject(t)
	reqs := []map[string]any{
		{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": map[string]any{"protocolVersion": "2025-06-18"}},
		{"jsonrpc": "2.0", "method": "notifications/initialized"},
		{"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
		{"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": map[string]any{"name": "create_visual", "arguments": map[string]any{
			"report_path": path, "visual_type": "pie", "category": "Sales[Region]", "values": []any{"Sales[Total Sales]"}, "x": 0, "y": 400}}},
		{"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": map[string]any{"name": "list_visuals", "arguments": map[string]any{"report_path": path}}},
		{"jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": map[string]any{"name": "list_pages", "arguments": map[string]any{"report_path": "/no/existe"}}},
		{"jsonrpc": "2.0", "id": 6, "method": "bogus"},
	}
	var in bytes.Buffer
	for _, r := range reqs {
		b, _ := json.Marshal(r)
		in.Write(append(b, '\n'))
	}
	var out bytes.Buffer
	if err := serveMCP(&in, &out); err != nil {
		t.Fatal(err)
	}
	lines := strings.Split(strings.TrimSpace(out.String()), "\n")
	if len(lines) != 6 { // la notificación no tiene respuesta
		t.Fatalf("respuestas = %d:\n%s", len(lines), out.String())
	}
	var resp []map[string]any
	for _, l := range lines {
		var m map[string]any
		json.Unmarshal([]byte(l), &m)
		resp = append(resp, m)
	}
	if dig(resp[0], "result", "protocolVersion") != "2025-06-18" {
		t.Errorf("initialize = %v", resp[0])
	}
	if n := len(listOf(dig(resp[1], "result", "tools"))); n != 10 {
		t.Errorf("tools = %d", n)
	}
	if dig(resp[2], "result", "isError") != nil {
		t.Errorf("create_visual falló: %v", resp[2])
	}
	if txt := str(dig(resp[3], "result", "content", 0, "text")); !strings.Contains(txt, `"count": 2`) || !strings.Contains(txt, "pieChart") {
		t.Errorf("list_visuals = %s", txt)
	}
	if dig(resp[4], "result", "isError") != true {
		t.Errorf("ruta inválida debería ser isError: %v", resp[4])
	}
	if dig(resp[5], "error", "code") != float64(-32601) {
		t.Errorf("bogus = %v", resp[5])
	}
}
