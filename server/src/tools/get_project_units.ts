import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { runRevitCommand } from "../utils/runRevitCommand.js";

export function registerGetProjectUnitsTool(server: McpServer) {
  server.tool(
    "get_project_units",
    "Get the units of the active Revit project (Manage > Project Units): length, area, volume and angle. Every length, coordinate, area, volume and angle sent to or returned by the other tools uses these units, e.g. millimeters in a typical metric project. Call this before creating or measuring geometry if the units are unknown.",
    {},
    async () => runRevitCommand("get_project_units")
  );
}
