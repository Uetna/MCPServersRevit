#!/usr/bin/env node
import { createRequire } from "module";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerTools } from "./tools/register.js";
import { config } from "./config.js";

const { version } = createRequire(import.meta.url)("../package.json") as { version: string };

// 创建服务器实例
const server = new McpServer({
  name: "mcp-server-for-revit",
  version,
});

// 启动服务器
async function main() {
  // 注册工具
  await registerTools(server);

  // 连接到传输层
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error(`Revit MCP Server ${version} started (Revit plugin at ${config.revitHost}:${config.revitPort})`);
}

main().catch((error) => {
  console.error("Error starting Revit MCP Server:", error);
  process.exit(1);
});
