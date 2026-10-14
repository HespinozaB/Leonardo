#!/usr/bin/env node
// Servidor MCP (stdio) para Revit 2026. Reenvía cada herramienta al addin EMASY MCP.
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { callRevit } from "./bridge.js";
import { tools } from "./tools.js";

const server = new McpServer(
  { name: "emasy-revit", version: "1.0.0" },
  {
    instructions:
      "Controla Revit 2026 abierto en este equipo. Longitudes y coordenadas en milímetros, ángulos en grados. " +
      "Empieza con get_revit_info. Consulta antes de modificar (list_levels, list_family_types) y usa export_view_image " +
      "para comprobar el resultado. Cada modificación es una transacción que el usuario puede deshacer con Ctrl+Z.",
  },
);

for (const tool of tools) {
  server.registerTool(
    tool.name,
    { description: tool.description, inputSchema: tool.schema, annotations: tool.annotations },
    async (args) => {
      try {
        return toContent(await callRevit(tool.name, args ?? {}));
      } catch (err) {
        return { isError: true, content: [{ type: "text", text: err.message }] };
      }
    },
  );
}

function toContent(result) {
  if (result && typeof result === "object" && typeof result.imageBase64 === "string") {
    const { imageBase64, mimeType, ...rest } = result;
    return {
      content: [
        { type: "image", data: imageBase64, mimeType: mimeType || "image/png" },
        { type: "text", text: JSON.stringify(rest, null, 2) },
      ],
    };
  }
  return { content: [{ type: "text", text: JSON.stringify(result, null, 2) }] };
}

await server.connect(new StdioServerTransport());
