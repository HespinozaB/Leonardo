#!/usr/bin/env bash
# Compila en Release y empaqueta el addin con la misma estructura que el original:
#   EMASY.addin + EMASY-2024/
set -euo pipefail
cd "$(dirname "$0")"
rm -rf dist bin/Release
dotnet build -c Release
mkdir -p dist/pkg
cp EMASY.addin dist/pkg/
cp -r bin/Release/EMASY-2024 dist/pkg/
rm -f dist/pkg/EMASY-2024/*.pdb
(cd dist/pkg && zip -qr ../EMASY-DWGTools-2024.zip .)
echo "Generado: dist/EMASY-DWGTools-2024.zip"
