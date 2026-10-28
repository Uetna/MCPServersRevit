// End-to-end check of the MCP server over stdio (no Revit needed). Run after `npm run build`.
import { test } from "node:test";
import assert from "node:assert/strict";
import net from "node:net";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const serverPath = fileURLToPath(new URL("../build/index.js", import.meta.url));

async function freePort() {
  const probe = net.createServer();
  await new Promise((resolve) => probe.listen(0, "127.0.0.1", resolve));
  const { port } = probe.address();
  await new Promise((resolve) => probe.close(resolve));
  return port;
}

async function connect(env) {
  const client = new Client({ name: "test", version: "1.0.0" });
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [serverPath],
    env: { ...process.env, ...env },
    stderr: "ignore",
  });
  await client.connect(transport);
  return client;
}

test("lists all tools and flags failures with isError", async () => {
  const port = await freePort();
  const client = await connect({ REVIT_MCP_PORT: String(port), REVIT_MCP_CONNECT_TIMEOUT_MS: "1000" });
  try {
    const { tools } = await client.listTools();
    const names = tools.map((t) => t.name);
    for (const expected of ["send_code_to_revit", "get_current_view_elements", "create_level", "delete_element"]) {
      assert.ok(names.includes(expected), `missing tool ${expected}`);
    }

    const result = await client.callTool({ name: "get_current_view_info", arguments: {} });
    assert.equal(result.isError, true);
    assert.match(result.content[0].text, /Cannot connect to Revit/);
  } finally {
    await client.close();
  }
});
