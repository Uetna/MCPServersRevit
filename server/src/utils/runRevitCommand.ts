import { withRevitConnection } from "./ConnectionManager.js";

/** Sends one command to Revit and wraps the result (or the error) as an MCP tool result. */
export async function runRevitCommand(method: string, params: Record<string, unknown> = {}) {
  try {
    const response = await withRevitConnection((client) => client.sendCommand(method, params));
    return { content: [{ type: "text" as const, text: JSON.stringify(response) }] };
  } catch (error) {
    return {
      isError: true,
      content: [
        {
          type: "text" as const,
          text: `${method} failed: ${error instanceof Error ? error.message : String(error)}`,
        },
      ],
    };
  }
}
