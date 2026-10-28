import * as net from "net";

export interface RevitClientOptions {
  connectTimeoutMs?: number;
  requestTimeoutMs?: number;
  /** Readable client name sent with every request (shown in write-lease refusals). */
  clientName?: string;
}

interface PendingRequest {
  method: string;
  resolve: (value: any) => void;
  reject: (error: Error) => void;
  timer: NodeJS.Timeout;
}

/** Error returned by the Revit plugin (JSON-RPC error or a result with success=false). */
export class RevitCommandError extends Error {
  constructor(message: string, public readonly code?: number, public readonly data?: unknown) {
    super(message);
    this.name = "RevitCommandError";
  }
}

/**
 * Persistent JSON-RPC connection to the Revit plugin.
 *
 * Wire format: one JSON object per line (NDJSON). Responses are matched to requests by id,
 * so several requests may be in flight; the plugin executes them one at a time.
 * The socket is opened lazily and re-opened after it drops.
 */
export class RevitClientConnection {
  readonly host: string;
  readonly port: number;
  private readonly connectTimeoutMs: number;
  private readonly requestTimeoutMs: number;
  /** Readable name of this MCP client, sent as "client" with every request. */
  clientName: string;

  private socket: net.Socket | null = null;
  private connecting: Promise<net.Socket> | null = null;
  private buffer = "";
  private nextId = 1;
  private readonly pending = new Map<string, PendingRequest>();

  constructor(host: string, port: number, options: RevitClientOptions = {}) {
    this.host = host;
    this.port = port;
    this.connectTimeoutMs = options.connectTimeoutMs ?? 5000;
    this.requestTimeoutMs = options.requestTimeoutMs ?? 150000;
    this.clientName = options.clientName ?? `mcp-server-for-revit (pid ${process.pid})`;
  }

  get isConnected(): boolean {
    return this.socket !== null && !this.socket.destroyed;
  }

  /** Opens the connection if needed. Concurrent callers share one attempt. */
  async connect(): Promise<void> {
    await this.ensureSocket();
  }

  disconnect(): void {
    const socket = this.socket;
    this.socket = null;
    socket?.destroy();
    this.failAll(new Error("Connection to Revit closed"));
  }

  async sendCommand(method: string, params: any = {}, timeoutMs?: number): Promise<any> {
    const socket = await this.ensureSocket();
    const id = `${Date.now()}-${this.nextId++}`;
    const payload = JSON.stringify({ jsonrpc: "2.0", method, params, id, client: this.clientName }) + "\n";
    const timeout = timeoutMs ?? this.requestTimeoutMs;

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        if (this.pending.delete(id)) {
          reject(new Error(`Command '${method}' timed out after ${Math.round(timeout / 1000)} s waiting for Revit`));
        }
      }, timeout);

      this.pending.set(id, { method, resolve, reject, timer });
      socket.write(payload, (error) => {
        if (error) this.settle(id, undefined, new Error(`Failed to send '${method}' to Revit: ${error.message}`));
      });
    });
  }

  private ensureSocket(): Promise<net.Socket> {
    if (this.isConnected) return Promise.resolve(this.socket!);
    if (this.connecting) return this.connecting;

    this.connecting = new Promise<net.Socket>((resolve, reject) => {
      const socket = net.createConnection({ host: this.host, port: this.port });
      socket.setNoDelay(true);
      socket.setEncoding("utf8");

      const timer = setTimeout(() => {
        socket.destroy();
        reject(new Error(`Timed out connecting to Revit at ${this.host}:${this.port}`));
      }, this.connectTimeoutMs);

      socket.once("connect", () => {
        clearTimeout(timer);
        this.buffer = "";
        this.socket = socket;
        resolve(socket);
      });

      socket.on("data", (chunk: string) => this.onData(chunk));

      socket.on("error", (error: NodeJS.ErrnoException) => {
        clearTimeout(timer);
        if (this.socket !== socket) {
          reject(
            new Error(
              `Cannot connect to Revit at ${this.host}:${this.port} (${error.code ?? error.message}). ` +
                "Make sure Revit is running and the MCP service is switched on (Add-Ins > Revit MCP Switch)."
            )
          );
        }
      });

      socket.on("close", () => {
        if (this.socket === socket) {
          this.socket = null;
          this.failAll(new Error("Connection to Revit was closed before the response arrived"));
        }
      });
    }).finally(() => {
      this.connecting = null;
    });

    return this.connecting;
  }

  private onData(chunk: string): void {
    this.buffer += chunk;

    let newline: number;
    while ((newline = this.buffer.indexOf("\n")) >= 0) {
      const line = this.buffer.slice(0, newline).trim();
      this.buffer = this.buffer.slice(newline + 1);
      if (line) this.handleMessage(line);
    }

    // Older plugins answer without a trailing newline: accept a complete JSON object as-is.
    const rest = this.buffer.trim();
    if (rest.endsWith("}")) {
      try {
        JSON.parse(rest);
        this.buffer = "";
        this.handleMessage(rest);
      } catch {
        // incomplete, wait for more data
      }
    }
  }

  private handleMessage(line: string): void {
    let response: any;
    try {
      response = JSON.parse(line);
    } catch (error) {
      console.error("Invalid JSON from Revit:", line.slice(0, 200));
      return;
    }

    const id = response?.id != null ? String(response.id) : undefined;
    if (!id || !this.pending.has(id)) {
      // Error responses for unparseable requests carry no id; fail the oldest request instead of hanging.
      if (!id && response?.error && this.pending.size > 0) {
        const [firstId] = this.pending.keys();
        this.settle(firstId, undefined, new RevitCommandError(response.error.message ?? "Unknown error from Revit", response.error.code));
      }
      return;
    }

    if (response.error) {
      this.settle(id, undefined, new RevitCommandError(response.error.message || "Unknown error from Revit", response.error.code, response.error.data));
      return;
    }

    let result = response.result;
    if (result && typeof result === "object" && (result.success === false || result.Success === false)) {
      const message = result.message ?? result.Message ?? result.errorMessage ?? result.ErrorMessage ?? JSON.stringify(result);
      this.settle(id, undefined, new RevitCommandError(String(message), undefined, result));
      return;
    }

    if (Array.isArray(response.warnings) && response.warnings.length > 0) {
      result = result && typeof result === "object" && !Array.isArray(result)
        ? { ...result, revitWarnings: response.warnings }
        : { result, revitWarnings: response.warnings };
    }

    this.settle(id, result);
  }

  private settle(id: string, result: any, error?: Error): void {
    const request = this.pending.get(id);
    if (!request) return;
    this.pending.delete(id);
    clearTimeout(request.timer);
    if (error) request.reject(error);
    else request.resolve(result);
  }

  private failAll(error: Error): void {
    for (const id of [...this.pending.keys()]) this.settle(id, undefined, error);
  }
}
