#!/usr/bin/env bash
# Compila en Release y empaqueta el addin con la misma estructura que el original:
#   RevitDwgExploder.addin + RevitDwgExploder-2024/
set -euo pipefail
cd "$(dirname "$0")"
dotnet build -c Release
rm -rf dist && mkdir -p dist/pkg
cp RevitDwgExploder.addin dist/pkg/
cp -r bin/Release/RevitDwgExploder-2024 dist/pkg/
rm -f dist/pkg/RevitDwgExploder-2024/*.pdb
(cd dist/pkg && zip -qr ../RevitDwgExploder-2024.zip .)
echo "Generado: dist/RevitDwgExploder-2024.zip"
