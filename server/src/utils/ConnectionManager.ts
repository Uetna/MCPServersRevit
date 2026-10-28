import { config } from "../config.js";
import { RevitClientConnection } from "./SocketClient.js";

// One persistent connection per MCP server process. It is opened on first use and
// re-opened automatically after Revit restarts or the MCP service is toggled.
let sharedClient: RevitClientConnection | null = null;

export function getRevitClient(): RevitClientConnection {
  if (!sharedClient) {
    sharedClient = new RevitClientConnection(config.revitHost, config.revitPort, {
      connectTimeoutMs: config.connectTimeoutMs,
      requestTimeoutMs: config.requestTimeoutMs,
      clientName: config.clientName || undefined,
    });
  }
  return sharedClient;
}

/**
 * Runs an operation against the Revit plugin.
 * @param operation function that sends commands through the connected client
 * @returns the operation result
 */
export async function withRevitConnection<T>(
  operation: (client: RevitClientConnection) => Promise<T>
): Promise<T> {
  const client = getRevitClient();
  await client.connect();
  return operation(client);
}
