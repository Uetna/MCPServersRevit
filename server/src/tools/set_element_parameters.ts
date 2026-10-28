import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { runRevitCommand } from "../utils/runRevitCommand.js";

export function registerSetElementParametersTool(server: McpServer) {
  server.tool(
    "set_element_parameters",
    "Set parameter values of Revit elements in one transaction: built-in, shared, project or family parameters. Numbers for measurable parameters are in the project display units of the parameter (e.g. mm for length in a metric project); a string value for a numeric parameter is parsed like input typed in Revit. Returns per-change results; failed changes do not block the others.",
    {
      changes: z
        .array(
          z.object({
            elementId: z.number().describe("Element id"),
            parameter: z
              .string()
              .describe("BuiltInParameter name (ALL_MODEL_MARK), shared parameter GUID, or parameter name"),
            value: z
              .union([z.string(), z.number(), z.boolean(), z.null()])
              .describe("New value: text, number in project units, 0/1 or true/false for Yes/No, element id for references"),
          })
        )
        .min(1)
        .describe("Changes to apply"),
      allowTypeParameters: z
        .boolean()
        .optional()
        .describe("Allow changing type parameters when the instance has no such parameter (affects every element of that type). Default false."),
    },
    async (args) =>
      runRevitCommand("set_element_parameters", {
        changes: args.changes,
        allowTypeParameters: args.allowTypeParameters ?? false,
      })
  );
}
