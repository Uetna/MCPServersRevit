import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { runRevitCommand } from "../utils/runRevitCommand.js";

export function registerWriteLeaseTool(server: McpServer) {
  server.tool(
    "revit_write_lease",
    "Protection against two agents modifying the same Revit model. The first MCP client that changes the model holds a write lease; changes from other clients are refused (error -33004) until the holder is idle for the configured timeout (120 s by default), disconnects, or releases it. Reading is never blocked. Use action 'status' to see who holds the lease and 'release' when you have finished a batch of changes.",
    {
      action: z.enum(["status", "release"]).describe("'status' shows the holder; 'release' frees the lease held by this client"),
    },
    async (args) => runRevitCommand(args.action === "release" ? "release_write_lease" : "get_write_lease")
  );
}
