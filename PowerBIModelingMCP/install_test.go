package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestClaudeConfigEntries(t *testing.T) {
	p := filepath.Join(t.TempDir(), "claude_desktop_config.json")
	os.WriteFile(p, []byte("\xef\xbb\xbf"+`{"mcpServers":{"other":{"command":"x"}},"preferences":{"a":1}}`), 0o644)
	tg := claudeTarget{"Claude Desktop", p, false}
	err := writeEntries(tg, map[string]serverEntry{
		mcpName:       {`C:\pbi\powerbi-modeling-mcp.exe`, []string{"--start"}},
		reportMcpName: {`C:\pbi\powerbi-report-mcp.exe`, []string{"serve"}},
	})
	if err != nil {
		t.Fatal(err)
	}
	m, _ := loadJSON(p)
	s := m["mcpServers"].(map[string]any)
	if s["other"] == nil || s[mcpName] == nil || dig(s, reportMcpName, "args", 0) != "serve" || dig(m, "preferences", "a") == nil {
		t.Fatalf("config = %v", m)
	}
	if removed, err := removeEntry(tg); !removed || err != nil {
		t.Fatal(removed, err)
	}
	m, _ = loadJSON(p)
	s = m["mcpServers"].(map[string]any)
	if len(s) != 1 || s["other"] == nil {
		t.Fatalf("tras quitar = %v", s)
	}
	baks, _ := filepath.Glob(p + ".bak-*")
	if len(baks) == 0 {
		t.Error("sin respaldo")
	}
}

func TestInvalidConfigUntouched(t *testing.T) {
	p := filepath.Join(t.TempDir(), "c.json")
	os.WriteFile(p, []byte(`{roto`), 0o644)
	if err := writeEntries(claudeTarget{"x", p, true}, map[string]serverEntry{mcpName: {"a", nil}}); err == nil || !strings.Contains(err.Error(), "JSON inválido") {
		t.Fatal(err)
	}
	if b, _ := os.ReadFile(p); string(b) != `{roto` {
		t.Fatal("se modificó un JSON inválido")
	}
}
