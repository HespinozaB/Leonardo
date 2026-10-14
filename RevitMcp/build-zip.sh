#!/usr/bin/env bash
# Compila el addin y empaqueta todo lo necesario para instalar en Windows:
#   RevitMcp.addin + EMASY-MCP-2026/ + server/ (con node_modules) + install.ps1 + README.md
set -euo pipefail
cd "$(dirname "$0")"
rm -rf dist addin/bin addin/obj
dotnet build addin/RevitMcp.csproj -c Release

mkdir -p dist/pkg/server
cp RevitMcp.addin install.ps1 README.md dist/pkg/
cp -r addin/bin/Release/EMASY-MCP-2026 dist/pkg/
rm -f dist/pkg/EMASY-MCP-2026/*.pdb
cp server/package.json server/package-lock.json server/*.js dist/pkg/server/
(cd dist/pkg/server && npm ci --omit=dev --silent)

(cd dist/pkg && zip -qr ../EMASY-MCP-2026.zip .)
echo "Generado: dist/EMASY-MCP-2026.zip"
