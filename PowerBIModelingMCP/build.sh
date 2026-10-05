#!/usr/bin/env bash
# Compila el instalador como .exe para Windows x64 (no requiere Windows para compilar).
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p dist
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -ldflags "-s -w" -o dist/PowerBI-Modeling-MCP-Setup.exe .
echo "Generado: dist/PowerBI-Modeling-MCP-Setup.exe"
