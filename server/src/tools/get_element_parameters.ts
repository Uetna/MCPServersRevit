import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { runRevitCommand } from "../utils/runRevitCommand.js";

export function registerGetElementParametersTool(server: McpServer) {
  server.tool(
    "get_element_parameters",
    "Read parameters of Revit elements, including built-in (system) parameters, shared parameters, project parameters and family parameters, for the instance and its type. Each parameter reports its kind (builtIn/shared/project/family), builtInParameter name or shared GUID, storage type, value and displayValue. Numeric values are in the project display units of the parameter.",
    {
      elementIds: z.array(z.number()).min(1).max(200).describe("Element ids (at most 200)"),
      parameterNames: z
        .array(z.string())
        .optional()
        .describe(
          "Parameters to read. Each key is a BuiltInParameter name (ALL_MODEL_MARK), a shared parameter GUID, or the parameter name shown in Revit. Omit to return all parameters."
        ),
      kinds: z
        .array(z.enum(["builtIn", "shared", "project", "family"]))
        .optional()
        .describe("When returning all parameters, keep only these kinds (e.g. ['shared','project'])"),
      includeTypeParameters: z
        .boolean()
        .optional()
        .describe("Also read parameters of the element type (default true)"),
    },
    async (args) =>
      runRevitCommand("get_element_parameters", {
        elementIds: args.elementIds,
        parameterNames: args.parameterNames ?? [],
        kinds: args.kinds ?? [],
        includeTypeParameters: args.includeTypeParameters ?? true,
      })
  );
}
