// Transport tests against a fake Revit plugin. Run after `npm run build`: `npm test`.
import { test } from "node:test";
import assert from "node:assert/strict";
import net from "node:net";
import { RevitClientConnection, RevitCommandError } from "../build/utils/SocketClient.js";

/** Starts a fake plugin; `onRequest(req, socket)` decides what to write back. */
async function startFakePlugin(onRequest) {
  const sockets = new Set();
  const server = net.createServer((socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
    socket.setEncoding("utf8");
    let buffer = "";
    socket.on("data", (chunk) => {
      buffer += chunk;
      let i;
      while ((i = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, i);
        buffer = buffer.slice(i + 1);
        onRequest(JSON.parse(line), socket);
      }
    });
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  return {
    port: server.address().port,
    dropClients: () => sockets.forEach((s) => s.destroy()),
    close: () => new Promise((resolve) => { sockets.forEach((s) => s.destroy()); server.close(resolve); }),
  };
}

const reply = (socket, id, result, extra = {}) =>
  socket.write(JSON.stringify({ jsonrpc: "2.0", id, result, ...extra }) + "\n");

test("round-trips a request larger than the old 8 KB read buffer", async () => {
  const plugin = await startFakePlugin((req, socket) => reply(socket, req.id, { length: req.params.code.length }));
  const client = new RevitClientConnection("127.0.0.1", plugin.port);
  try {
    const code = "x".repeat(300_000);
    const result = await client.sendCommand("send_code_to_revit", { code });
    assert.equal(result.length, code.length);
  } finally {
    client.disconnect();
    await plugin.close();
  }
});

test("reassembles a response split into tiny chunks, including multi-byte UTF-8", async () => {
  const plugin = await startFakePlugin((req, socket) => {
    const bytes = Buffer.from(JSON.stringify({ jsonrpc: "2.0", id: req.id, result: { name: "Стена 200 мм" } }) + "\n");
    let offset = 0;
    const timer = setInterval(() => {
      socket.write(bytes.subarray(offset, offset + 3));
      offset += 3;
      if (offset >= bytes.length) clearInterval(timer);
    }, 1);
  });
  const client = new RevitClientConnection("127.0.0.1", plugin.port);
  try {
    const result = await client.sendCommand("get_selected_elements");
    assert.equal(result.name, "Стена 200 мм");
  } finally {
    client.disconnect();
    await plugin.close();
  }
});

test("matches concurrent responses by id when they arrive out of order", async () => {
  const held = [];
  const plugin = await startFakePlugin((req, socket) => {
    held.push([req, socket]);
    if (held.length === 3) held.reverse().forEach(([r, s]) => reply(s, r.id, r.params.n));
  });
  const client = new RevitClientConnection("127.0.0.1", plugin.port);
  try {
    const results = await Promise.all([1, 2, 3].map((n) => client.sendCommand("echo", { n })));
    assert.deepEqual(results, [1, 2, 3]);
  } finally {
    client.disconnect();
    await plugin.close();
  }
});

test("rejects pending requests immediately when the connection drops", async () => {
  const plugin = await startFakePlugin(() => plugin.dropClients());
  const client = new RevitClientConnection("127.0.0.1", plugin.port, { requestTimeoutMs: 60_000 });
  try {
    const started = Date.now();
    await assert.rejects(client.sendCommand("get_current_view_info"), /closed/);
    assert.ok(Date.now() - started < 5_000, "should not wait for the request timeout");
  } finally {
    client.disconnect();
    await plugin.close();
  }
});

test("times out a request that never gets an answer", async () => {
  const plugin = await startFakePlugin(() => {});
  const client = new RevitClientConnection("127.0.0.1", plugin.port, { requestTimeoutMs: 200 });
  try {
    await assert.rejects(client.sendCommand("analyze_model_statistics"), /timed out/);
  } finally {
    client.disconnect();
    await plugin.close();
  }
});

test("accepts responses from legacy plugins without a trailing newline", async () => {
  const plugin = await startFakePlugin((req, socket) =>
    socket.write(JSON.stringify({ jsonrpc: "2.0", id: req.id, result: "legacy" })));
  const client = new RevitClientConnection("127.0.0.1", plugin.port);
  try {
    assert.equal(await client.sendCommand("say_hello"), "legacy");
  } finally {
    client.disconnect();
    await plugin.close();
  }
});

test("turns JSON-RPC errors and success=false results into RevitCommandError", async () => {
  const plugin = await startFakePlugin((req, socket) => {
    if (req.method === "rpc_error") {
      socket.write(JSON.stringify({ jsonrpc: "2.0", id: req.id, error: { code: -33002, message: "busy" } }) + "\n");
    } else {
      reply(socket, req.id, { Success: false, Message: "Level not found" });
    }
  });
  const client = new RevitClientConnection("127.0.0.1", plugin.port);
  try {
    await assert.rejects(client.sendCommand("rpc_error"), (e) => e instanceof RevitCommandError && e.code === -33002);
    await assert.rejects(client.sendCommand("create_level"), /Level not found/);
  } finally {
    client.disconnect();
    await plugin.close();
  }
});

test("exposes Revit warnings next to the result", async () => {
  const plugin = await startFakePlugin((req, socket) =>
    reply(socket, req.id, { created: 2 }, { warnings: ["Warning: Walls overlap"] }));
  const client = new RevitClientConnection("127.0.0.1", plugin.port);
  try {
    const result = await client.sendCommand("create_line_based_element");
    assert.deepEqual(result, { created: 2, revitWarnings: ["Warning: Walls overlap"] });
  } finally {
    client.disconnect();
    await plugin.close();
  }
});

test("sends the client name so the plugin can name the write-lease holder", async () => {
  const seen = [];
  const plugin = await startFakePlugin((req, socket) => {
    seen.push(req.client);
    reply(socket, req.id, "ok");
  });
  const client = new RevitClientConnection("127.0.0.1", plugin.port, { clientName: "agent A" });
  try {
    await client.sendCommand("get_project_units");
    client.clientName = "claude-code (pid 1)";
    await client.sendCommand("get_project_units");
    assert.deepEqual(seen, ["agent A", "claude-code (pid 1)"]);
  } finally {
    client.disconnect();
    await plugin.close();
  }
});

test("surfaces a write-lease refusal as RevitCommandError -33004", async () => {
  const plugin = await startFakePlugin((req, socket) =>
    socket.write(JSON.stringify({
      jsonrpc: "2.0",
      id: req.id,
      error: { code: -33004, message: "The Revit model is being modified by another MCP client 'agent A'" },
    }) + "\n"));
  const client = new RevitClientConnection("127.0.0.1", plugin.port);
  try {
    await assert.rejects(
      client.sendCommand("set_element_parameters", { changes: [] }),
      (e) => e instanceof RevitCommandError && e.code === -33004 && /agent A/.test(e.message)
    );
  } finally {
    client.disconnect();
    await plugin.close();
  }
});

test("reports a clear error when Revit is not listening", async () => {
  const probe = net.createServer();
  await new Promise((resolve) => probe.listen(0, "127.0.0.1", resolve));
  const port = probe.address().port;
  await new Promise((resolve) => probe.close(resolve));

  const client = new RevitClientConnection("127.0.0.1", port, { connectTimeoutMs: 2_000 });
  await assert.rejects(client.sendCommand("say_hello"), /Cannot connect to Revit .*Revit MCP Switch/);
});

test("reconnects after the plugin restarts", async () => {
  const plugin = await startFakePlugin((req, socket) => reply(socket, req.id, "ok"));
  const client = new RevitClientConnection("127.0.0.1", plugin.port);
  try {
    assert.equal(await client.sendCommand("say_hello"), "ok");
    plugin.dropClients();
    await new Promise((resolve) => setTimeout(resolve, 50));
    assert.equal(await client.sendCommand("say_hello"), "ok");
  } finally {
    client.disconnect();
    await plugin.close();
  }
});
