// Cliente del puente TCP que abre el addin EMASY MCP dentro de Revit.
import { readFileSync } from "node:fs";
import { createConnection } from "node:net";
import { homedir } from "node:os";
import { join } from "node:path";

const DEFAULT_PORT = 8765;
const TIMEOUT_MS = 150_000;

export function configPath() {
  const appData = process.env.APPDATA || join(homedir(), "AppData", "Roaming");
  return join(appData, "EMASY", "RevitMcp", "bridge.json");
}

// Se relee en cada llamada: el addin crea el archivo la primera vez que arranca Revit.
function loadConfig() {
  let file = {};
  try {
    file = JSON.parse(readFileSync(configPath(), "utf8").replace(/^﻿/, ""));
  } catch {
    // Sin archivo: se usan variables de entorno o valores por defecto.
  }
  return {
    host: process.env.REVIT_MCP_HOST || "127.0.0.1",
    port: Number(process.env.REVIT_MCP_PORT || file.Port || DEFAULT_PORT),
    token: process.env.REVIT_MCP_TOKEN || file.Token || "",
  };
}

export function callRevit(tool, args = {}) {
  const { host, port, token } = loadConfig();
  return new Promise((resolve, reject) => {
    let buffer = "";
    let settled = false;
    const finish = (fn, value) => {
      if (!settled) {
        settled = true;
        socket.destroy();
        fn(value);
      }
    };

    const socket = createConnection({ host, port }, () => {
      socket.write(JSON.stringify({ token, tool, args }) + "\n");
    });
    socket.setEncoding("utf8");
    socket.setTimeout(TIMEOUT_MS, () => finish(reject, new Error("Revit no respondió a tiempo.")));
    socket.on("data", (chunk) => {
      buffer += chunk;
      const newline = buffer.indexOf("\n");
      if (newline >= 0) {
        parse(buffer.slice(0, newline));
      }
    });
    socket.on("end", () => buffer && parse(buffer));
    socket.on("error", (err) => {
      if (err.code === "ECONNREFUSED") {
        finish(reject, new Error(
          `No hay conexión con Revit en ${host}:${port}. Abre Revit 2026 con el addin EMASY MCP instalado ` +
          `(pestaña EMASY > Claude MCP > Estado MCP).`));
      } else {
        finish(reject, err);
      }
    });

    function parse(line) {
      try {
        const response = JSON.parse(line);
        if (response.ok) {
          finish(resolve, response.result);
        } else {
          finish(reject, new Error(response.error || "Error desconocido en Revit."));
        }
      } catch (err) {
        finish(reject, new Error("Respuesta no válida de Revit: " + err.message));
      }
    }
  });
}
