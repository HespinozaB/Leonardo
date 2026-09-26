#!/usr/bin/env bash
# Compila en Release y empaqueta el addin con la misma estructura que el original:
#   EMASY.addin + EMASY-2024/
set -euo pipefail
cd "$(dirname "$0")"
rm -rf dist bin/Release installer/bin installer/obj
dotnet build -c Release
mkdir -p dist/pkg
cp EMASY.addin dist/pkg/
cp -r bin/Release/EMASY-2024 dist/pkg/
rm -f dist/pkg/EMASY-2024/*.pdb
(cd dist/pkg && zip -qr ../EMASY-DWGTools-2024.zip .)
echo "Generado: dist/EMASY-DWGTools-2024.zip"

# Instalador .exe (lleva el ZIP dentro; instala, desbloquea y desinstala).
dotnet build installer/EMASY.Installer.csproj -c Release -o dist/installer-build
cp dist/installer-build/EMASY-DWGTools-2024-Setup.exe dist/
rm -rf dist/installer-build
echo "Generado: dist/EMASY-DWGTools-2024-Setup.exe"
