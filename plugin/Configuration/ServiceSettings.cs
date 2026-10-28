using Newtonsoft.Json;

namespace revit_mcp_plugin.Configuration
{
    /// <summary>
    /// <para>服务设置类</para>
    /// <para>Service settings.</para>
    /// </summary>
    public class ServiceSettings
    {
        /// <summary>
        /// <para>日志级别</para>
        /// <para>Log level.</para>
        /// </summary>
        [JsonProperty("logLevel")]
        public string LogLevel { get; set; } = "Info";

        /// <summary>
        /// <para>socket服务端口</para>
        /// <para>Socket service port.</para>
        /// </summary>
        [JsonProperty("port")]
        public int Port { get; set; } = 8964;

        /// <summary>
        /// Seconds of inactivity after which an MCP client loses the exclusive right to modify
        /// the model (see WriteLease). 0 disables the protection.
        /// </summary>
        [JsonProperty("writeLeaseSeconds")]
        public int WriteLeaseSeconds { get; set; } = 120;

    }
}
