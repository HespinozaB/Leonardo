// Prueba sin Revit: un puente falso imita el protocolo del addin y un cliente MCP llama al servidor.
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { tools } from "../tools.js";

const here = dirname(fileURLToPath(import.meta.url));
const TOKEN = "secreto";
const PNG = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

// Los nombres de tools.js deben existir en el registro del addin.
const registry = readFileSync(join(here, "../../addin/src/Tools/ToolRegistry.cs"), "utf8");
const registered = [...registry.matchAll(/\["(\w+)"\]\s*=/g)].map((m) => m[1]);
assert.deepEqual([...tools.map((t) => t.name)].sort(), [...registered].sort(), "tools.js y ToolRegistry.cs no coinciden");

const received = [];
const fake = createServer((socket) => {
  let buffer = "";
  socket.setEncoding("utf8");
  socket.on("data", (chunk) => {
    buffer += chunk;
    if (!buffer.includes("\n")) return;
    const req = JSON.parse(buffer.slice(0, buffer.indexOf("\n")));
    received.push(req);
    let res;
    if (req.token !== TOKEN) res = { ok: false, error: "Token inválido." };
    else if (req.tool === "export_view_image") res = { ok: true, result: { view: "Nivel 1", imageBase64: PNG, mimeType: "image/png" } };
    else if (req.tool === "delete_elements") res = { ok: false, error: "No existe el elemento 999." };
    else res = { ok: true, result: { echo: req.tool, args: req.args } };
    socket.end(JSON.stringify(res) + "\n");
  });
});
await new Promise((r) => fake.listen(0, "127.0.0.1", r));
const port = fake.address().port;

// bridge.json en un APPDATA temporal, como lo escribiría el addin (con BOM, por si acaso).
const appData = mkdtempSync(join(tmpdir(), "revitmcp-"));
mkdirSync(join(appData, "EMASY", "RevitMcp"), { recursive: true });
writeFileSync(join(appData, "EMASY", "RevitMcp", "bridge.json"), "﻿" + JSON.stringify({ Port: port, Token: TOKEN }));

const client = new Client({ name: "smoke", version: "1.0.0" });
await client.connect(new StdioClientTransport({
  command: process.execPath,
  args: [join(here, "../index.js")],
  env: { ...process.env, APPDATA: appData },
}));

const listed = await client.listTools();
assert.equal(listed.tools.length, tools.length);
assert.ok(listed.tools.find((t) => t.name === "create_wall").inputSchema.properties.points);

const info = await client.callTool({ name: "get_revit_info", arguments: {} });
assert.equal(JSON.parse(info.content[0].text).echo, "get_revit_info");

const wall = await client.callTool({
  name: "create_wall",
  arguments: { points: [{ x: 0, y: 0 }, { x: 5000, y: 0 }, { x: 5000, y: 4000 }], closed: true, heightMm: 2800 },
});
assert.equal(JSON.parse(wall.content[0].text).args.heightMm, 2800);

const image = await client.callTool({ name: "export_view_image", arguments: {} });
assert.equal(image.content[0].type, "image");
assert.equal(image.content[0].data, PNG);

const failed = await client.callTool({ name: "delete_elements", arguments: { ids: [999] } });
assert.equal(failed.isError, true);
assert.match(failed.content[0].text, /999/);

const invalid = await client.callTool({ name: "set_parameters", arguments: { updates: [] } });
assert.equal(invalid.isError, true, "zod debe rechazar updates vacío");

await client.close();
fake.close();

// Sin Revit escuchando: mensaje claro.
const client2 = new Client({ name: "smoke2", version: "1.0.0" });
await client2.connect(new StdioClientTransport({
  command: process.execPath,
  args: [join(here, "../index.js")],
  env: { ...process.env, APPDATA: appData, REVIT_MCP_PORT: String(port) },
}));
const down = await client2.callTool({ name: "get_revit_info", arguments: {} });
assert.equal(down.isError, true);
assert.match(down.content[0].text, /Abre Revit 2026/);
await client2.close();

assert.ok(received.every((r) => r.token === TOKEN));
console.log(`OK: ${listed.tools.length} herramientas, ${received.length} llamadas al puente falso.`);
