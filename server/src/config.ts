// Runtime settings, overridable through environment variables of the MCP client config.

function intFromEnv(name: string, fallback: number): number {
  const raw = process.env[name];
  if (!raw) return fallback;
  const value = Number.parseInt(raw, 10);
  return Number.isFinite(value) && value > 0 ? value : fallback;
}

export const config = {
  /** Host of the Revit plugin. Loopback only: the plugin does not listen on other interfaces. */
  revitHost: process.env.REVIT_MCP_HOST || "127.0.0.1",
  /** Port of the Revit plugin (plugin default: 8964, see commandRegistry.json "settings.port"). */
  revitPort: intFromEnv("REVIT_MCP_PORT", 8964),
  /** Time to establish the TCP connection. */
  connectTimeoutMs: intFromEnv("REVIT_MCP_CONNECT_TIMEOUT_MS", 5000),
  /**
   * Time to wait for a command result. Must be longer than the longest plugin-side
   * timeout (120 s for model statistics / material quantities) so the plugin reports first.
   */
  requestTimeoutMs: intFromEnv("REVIT_MCP_TIMEOUT_MS", 150000),
};
