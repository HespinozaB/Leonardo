// Instalador del Power BI Modeling MCP Server (Microsoft) para Claude Desktop y Claude Code.
//
// No incluye el binario de Microsoft: lo descarga del Visual Studio Marketplace oficial
// (extensión analysis-services.powerbi-modeling-mcp, plataforma win32-x64), lo extrae en
// %LOCALAPPDATA%\PowerBIModelingMCP y registra el servidor en la configuración de Claude.
package main

import (
	"archive/zip"
	"bufio"
	"bytes"
	"compress/gzip"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

const (
	extensionID    = "analysis-services.powerbi-modeling-mcp"
	fallbackVer    = "0.4.0"
	serverExe      = "powerbi-modeling-mcp.exe"
	mcpName        = "powerbi-modeling"
	queryURL       = "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery"
	downloadURLFmt = "https://marketplace.visualstudio.com/_apis/public/gallery/publishers/analysis-services/vsextensions/powerbi-modeling-mcp/%s/vspackage?targetPlatform=win32-x64"
)

var (
	stdin     = bufio.NewReader(os.Stdin)
	assumeYes bool
)

func main() {
	dir := flag.String("dir", defaultInstallDir(), "carpeta de instalación")
	ver := flag.String("version", "", "versión del servidor (por defecto la última del Marketplace)")
	readOnly := flag.Bool("readonly", false, "registrar el servidor en modo solo lectura (--readonly)")
	flag.BoolVar(&assumeYes, "yes", false, "responder sí a todo (modo desatendido)")
	flag.Usage = func() {
		fmt.Println("Uso: PowerBI-Modeling-MCP-Setup.exe [install|uninstall|status] [opciones]")
		flag.PrintDefaults()
	}
	flag.Parse()

	fmt.Println("==============================================")
	fmt.Println("  Power BI Modeling MCP - Instalador para Claude")
	fmt.Println("==============================================")
	if runtime.GOOS != "windows" {
		fmt.Println("AVISO: el servidor solo funciona en Windows (requiere Power BI Desktop).")
	}

	cmd := flag.Arg(0)
	if cmd == "" {
		cmd = menu()
	}

	var err error
	switch cmd {
	case "install", "1":
		err = install(*dir, *ver, *readOnly)
	case "uninstall", "2":
		err = uninstall(*dir)
	case "status", "3":
		status(*dir)
	case "4", "exit":
		return
	default:
		flag.Usage()
		os.Exit(2)
	}
	if err != nil {
		fmt.Println("\nERROR:", err)
	}
	pause()
	if err != nil {
		os.Exit(1)
	}
}

func menu() string {
	fmt.Println()
	fmt.Println("  1) Instalar / actualizar")
	fmt.Println("  2) Desinstalar")
	fmt.Println("  3) Ver estado")
	fmt.Println("  4) Salir")
	fmt.Print("\nOpción [1]: ")
	line, _ := stdin.ReadString('\n')
	line = strings.TrimSpace(line)
	if line == "" {
		return "1"
	}
	return line
}

func pause() {
	if assumeYes {
		return
	}
	fmt.Print("\nPresione Enter para cerrar...")
	stdin.ReadString('\n')
}

func askYesNo(q string, def bool) bool {
	if assumeYes {
		return true
	}
	hint := "[S/n]"
	if !def {
		hint = "[s/N]"
	}
	fmt.Printf("%s %s: ", q, hint)
	line, _ := stdin.ReadString('\n')
	switch strings.ToLower(strings.TrimSpace(line)) {
	case "":
		return def
	case "s", "si", "sí", "y", "yes":
		return true
	}
	return false
}

// ── Rutas ────────────────────────────────────────────────────────────

func defaultInstallDir() string {
	if d := os.Getenv("LOCALAPPDATA"); d != "" {
		return filepath.Join(d, "PowerBIModelingMCP")
	}
	h, _ := os.UserHomeDir()
	return filepath.Join(h, "AppData", "Local", "PowerBIModelingMCP")
}

type claudeTarget struct {
	name string
	path string
	code bool // Claude Code usa "type": "stdio"
}

// claudeTargets devuelve los archivos de configuración de Claude que existen o que tiene
// sentido crear: Claude Desktop (instalador clásico y versión de Microsoft Store) y Claude Code.
func claudeTargets() []claudeTarget {
	home, _ := os.UserHomeDir()
	appData := os.Getenv("APPDATA")
	if appData == "" {
		appData = filepath.Join(home, "AppData", "Roaming")
	}
	localAppData := os.Getenv("LOCALAPPDATA")
	if localAppData == "" {
		localAppData = filepath.Join(home, "AppData", "Local")
	}

	var t []claudeTarget
	t = append(t, claudeTarget{"Claude Desktop", filepath.Join(appData, "Claude", "claude_desktop_config.json"), false})
	// Claude Desktop instalado desde Microsoft Store guarda su config virtualizada.
	if pkgs, _ := filepath.Glob(filepath.Join(localAppData, "Packages", "Claude_*")); len(pkgs) > 0 {
		for _, p := range pkgs {
			t = append(t, claudeTarget{"Claude Desktop (Store)", filepath.Join(p, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"), false})
		}
	}
	t = append(t, claudeTarget{"Claude Code", filepath.Join(home, ".claude.json"), true})
	return t
}

// ── Instalar ─────────────────────────────────────────────────────────

func install(dir, ver string, readOnly bool) error {
	if ver == "" {
		fmt.Print("\n> Consultando la última versión en el Marketplace... ")
		v, err := latestVersion()
		if err != nil {
			fmt.Printf("no disponible (%v). Se usará %s.\n", err, fallbackVer)
			ver = fallbackVer
		} else {
			fmt.Println(v)
			ver = v
		}
	}
	fmt.Println("  Versión :", ver)
	fmt.Println("  Carpeta :", dir)

	fmt.Println("\n> Descargando desde marketplace.visualstudio.com ...")
	data, err := download(fmt.Sprintf(downloadURLFmt, ver))
	if err != nil {
		return fmt.Errorf("descarga fallida: %w", err)
	}
	fmt.Printf("  %.1f MB descargados\n", float64(len(data))/1e6)

	fmt.Println("\n> Extrayendo...")
	if _, err := os.Stat(dir); err == nil {
		if err := os.RemoveAll(dir); err != nil {
			return fmt.Errorf("no se pudo borrar la versión anterior (¿Claude está abierto?). Cierre Claude Desktop / Claude Code y reintente: %w", err)
		}
	}
	if err := unzip(data, dir); err != nil {
		return fmt.Errorf("extracción fallida: %w", err)
	}
	exe := filepath.Join(dir, "extension", "server", serverExe)
	if _, err := os.Stat(exe); err != nil {
		if exe = findFile(dir, serverExe); exe == "" {
			return errors.New("el paquete descargado no contiene " + serverExe)
		}
	}
	fmt.Println("  Servidor:", exe)

	args := []string{"--start"}
	if readOnly {
		args = append(args, "--readonly")
	}

	fmt.Println()
	configured := 0
	for _, t := range claudeTargets() {
		_, statErr := os.Stat(t.path)
		exists := statErr == nil
		q := fmt.Sprintf("¿Configurar %s?", t.name)
		if !exists {
			q = fmt.Sprintf("¿Configurar %s? (aún no existe %s)", t.name, t.path)
		}
		if !askYesNo(q, exists) {
			continue
		}
		if err := writeEntry(t, exe, args); err != nil {
			fmt.Printf("  ! %s: %v\n", t.name, err)
			continue
		}
		fmt.Printf("  OK %s -> %s\n", t.name, t.path)
		configured++
	}

	fmt.Println("\n==============================================")
	fmt.Println("  Instalación completa")
	fmt.Println("==============================================")
	if configured == 0 {
		fmt.Println("No se configuró ningún cliente. Agregue esto manualmente en \"mcpServers\":")
		b, _ := json.MarshalIndent(map[string]any{mcpName: map[string]any{"command": exe, "args": args}}, "  ", "  ")
		fmt.Println("  " + string(b))
	}
	fmt.Println("Siguientes pasos:")
	fmt.Println("  1. Abra su archivo .pbix en Power BI Desktop.")
	fmt.Println("  2. Reinicie Claude Desktop / Claude Code.")
	fmt.Println("  3. Pida: \"Conéctate a <nombre del archivo> en Power BI Desktop\".")
	fmt.Println("\nLicencia del servidor (Microsoft, PREVIEW):", filepath.Join(dir, "extension", "LICENSE.txt"))
	return nil
}

func latestVersion() (string, error) {
	body := fmt.Sprintf(`{"filters":[{"criteria":[{"filterType":7,"value":%q}]}],"flags":512}`, extensionID)
	req, _ := http.NewRequest("POST", queryURL, strings.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json;api-version=3.0-preview.1")
	resp, err := (&http.Client{Timeout: 15 * time.Second}).Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return "", fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	var r struct {
		Results []struct {
			Extensions []struct {
				Versions []struct {
					Version        string `json:"version"`
					TargetPlatform string `json:"targetPlatform"`
				} `json:"versions"`
			} `json:"extensions"`
		} `json:"results"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&r); err != nil {
		return "", err
	}
	if len(r.Results) == 0 || len(r.Results[0].Extensions) == 0 {
		return "", errors.New("extensión no encontrada")
	}
	vs := r.Results[0].Extensions[0].Versions
	for _, v := range vs {
		if v.TargetPlatform == "win32-x64" {
			return v.Version, nil
		}
	}
	if len(vs) > 0 {
		return vs[0].Version, nil
	}
	return "", errors.New("sin versiones")
}

func download(url string) ([]byte, error) {
	resp, err := (&http.Client{Timeout: 10 * time.Minute}).Get(url)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return nil, fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	data, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, err
	}
	// El Marketplace a veces entrega el VSIX comprimido con gzip.
	if len(data) > 2 && data[0] == 0x1f && data[1] == 0x8b {
		zr, err := gzip.NewReader(bytes.NewReader(data))
		if err != nil {
			return nil, err
		}
		if data, err = io.ReadAll(zr); err != nil {
			return nil, err
		}
	}
	if len(data) < 4 || data[0] != 'P' || data[1] != 'K' {
		return nil, errors.New("el archivo descargado no es un VSIX/ZIP válido")
	}
	return data, nil
}

func unzip(data []byte, dest string) error {
	zr, err := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if err != nil {
		return err
	}
	destAbs, _ := filepath.Abs(dest)
	for _, f := range zr.File {
		// Los nombres en el VSIX vienen URL-encoded en algunos casos (p. ej. %5B).
		name := strings.ReplaceAll(f.Name, "%5B", "[")
		name = strings.ReplaceAll(name, "%5D", "]")
		target := filepath.Join(destAbs, filepath.FromSlash(name))
		if !strings.HasPrefix(target, destAbs+string(os.PathSeparator)) {
			return fmt.Errorf("ruta inválida en el paquete: %s", f.Name)
		}
		if f.FileInfo().IsDir() {
			if err := os.MkdirAll(target, 0o755); err != nil {
				return err
			}
			continue
		}
		if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
			return err
		}
		rc, err := f.Open()
		if err != nil {
			return err
		}
		out, err := os.OpenFile(target, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o755)
		if err != nil {
			rc.Close()
			return err
		}
		_, err = io.Copy(out, rc)
		rc.Close()
		out.Close()
		if err != nil {
			return err
		}
	}
	return nil
}

func findFile(root, name string) string {
	var found string
	filepath.WalkDir(root, func(p string, d os.DirEntry, err error) error {
		if err == nil && !d.IsDir() && strings.EqualFold(d.Name(), name) {
			found = p
			return filepath.SkipAll
		}
		return nil
	})
	return found
}

// ── Configuración de Claude ──────────────────────────────────────────

func loadJSON(path string) (map[string]any, error) {
	b, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return map[string]any{}, nil
	}
	if err != nil {
		return nil, err
	}
	b = bytes.TrimPrefix(b, []byte("\xef\xbb\xbf")) // BOM de Notepad
	if len(bytes.TrimSpace(b)) == 0 {
		return map[string]any{}, nil
	}
	m := map[string]any{}
	if err := json.Unmarshal(b, &m); err != nil {
		return nil, fmt.Errorf("JSON inválido, no se modifica: %w", err)
	}
	return m, nil
}

func saveJSON(path string, m map[string]any) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	if old, err := os.ReadFile(path); err == nil {
		backup := path + ".bak-" + time.Now().Format("20060102-150405")
		if err := os.WriteFile(backup, old, 0o644); err != nil {
			return fmt.Errorf("no se pudo crear respaldo: %w", err)
		}
	}
	b, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, b, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func writeEntry(t claudeTarget, exe string, args []string) error {
	m, err := loadJSON(t.path)
	if err != nil {
		return err
	}
	servers, _ := m["mcpServers"].(map[string]any)
	if servers == nil {
		servers = map[string]any{}
	}
	entry := map[string]any{"command": exe, "args": args}
	if t.code {
		entry["type"] = "stdio"
		entry["env"] = map[string]any{}
	}
	servers[mcpName] = entry
	m["mcpServers"] = servers
	return saveJSON(t.path, m)
}

func removeEntry(t claudeTarget) (bool, error) {
	if _, err := os.Stat(t.path); err != nil {
		return false, nil
	}
	m, err := loadJSON(t.path)
	if err != nil {
		return false, err
	}
	servers, _ := m["mcpServers"].(map[string]any)
	if _, ok := servers[mcpName]; !ok {
		return false, nil
	}
	delete(servers, mcpName)
	return true, saveJSON(t.path, m)
}

// ── Desinstalar / estado ─────────────────────────────────────────────

func uninstall(dir string) error {
	if !askYesNo("¿Quitar el servidor de Claude y borrar "+dir+"?", true) {
		return nil
	}
	for _, t := range claudeTargets() {
		removed, err := removeEntry(t)
		switch {
		case err != nil:
			fmt.Printf("  ! %s: %v\n", t.name, err)
		case removed:
			fmt.Printf("  OK quitado de %s\n", t.name)
		}
	}
	if err := os.RemoveAll(dir); err != nil {
		return fmt.Errorf("no se pudo borrar %s (¿Claude está abierto?): %w", dir, err)
	}
	fmt.Println("  OK carpeta eliminada")
	return nil
}

func status(dir string) {
	fmt.Println()
	exe := findFile(dir, serverExe)
	if exe == "" {
		fmt.Println("Servidor : NO instalado en", dir)
	} else {
		fmt.Println("Servidor :", exe)
	}
	for _, t := range claudeTargets() {
		m, err := loadJSON(t.path)
		state := "no configurado"
		if _, statErr := os.Stat(t.path); statErr != nil {
			state = "sin archivo de configuración"
		} else if err != nil {
			state = err.Error()
		} else if s, _ := m["mcpServers"].(map[string]any); s != nil && s[mcpName] != nil {
			state = "configurado"
		}
		fmt.Printf("%-24s: %s\n", t.name, state)
	}
}
