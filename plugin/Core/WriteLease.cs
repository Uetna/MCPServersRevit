using System;
using System.Collections.Generic;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Keeps two MCP clients (two agents) from modifying the model at the same time.
    ///
    /// Every TCP connection is one MCP server process, i.e. one agent. The first connection that
    /// runs a modifying command takes the write lease; while it holds the lease, modifying
    /// commands from other connections are refused with <see cref="ErrorLocked"/>. Reading is
    /// always allowed. The lease is renewed by every modifying command and is released when its
    /// owner disconnects, calls <c>release_write_lease</c>, or stays idle longer than the timeout.
    ///
    /// Commands always run against the active document of this Revit instance, so the lease
    /// covers that Revit instance. The class has no Revit dependencies and is thread-safe.
    /// </summary>
    public class WriteLease
    {
        public const int ErrorLocked = -33004;
        public const string ReleaseMethod = "release_write_lease";
        public const string StatusMethod = "get_write_lease";

        /// <summary>Commands that never modify the document.</summary>
        private static readonly HashSet<string> ReadOnlyCommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "say_hello",
            "get_current_view_info",
            "get_current_view_elements",
            "get_selected_elements",
            "get_available_family_types",
            "ai_element_filter",
            "analyze_model_statistics",
            "get_material_quantities",
            "export_room_data",
            "get_project_units",
            "get_element_parameters",
            StatusMethod,
            ReleaseMethod
        };

        private readonly object _lock = new object();
        private readonly Func<DateTime> _clock;
        private string _ownerId;
        private string _ownerName;
        private DateTime _acquiredAt;
        private DateTime _lastWriteAt;

        public WriteLease(TimeSpan idleTimeout, Func<DateTime> clock = null)
        {
            IdleTimeout = idleTimeout;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>Lease expiry after the last modifying command; zero disables the protection.</summary>
        public TimeSpan IdleTimeout { get; set; }

        public static bool IsReadOnly(string method) => method != null && ReadOnlyCommands.Contains(method);

        /// <summary>
        /// Called before a modifying command. Returns null when the client may proceed (the lease is
        /// taken or renewed), otherwise a message explaining who holds the lease.
        /// </summary>
        public string TryAcquire(string clientId, string clientName)
        {
            if (IdleTimeout <= TimeSpan.Zero) return null;

            lock (_lock)
            {
                var now = _clock();
                ExpireIfIdle(now);

                if (_ownerId == null || _ownerId == clientId)
                {
                    if (_ownerId == null) _acquiredAt = now;
                    _ownerId = clientId;
                    _ownerName = clientName;
                    _lastWriteAt = now;
                    return null;
                }

                var idle = now - _lastWriteAt;
                var left = IdleTimeout - idle;
                return $"The Revit model is being modified by another MCP client '{_ownerName}' " +
                       $"(last change {Math.Round(idle.TotalSeconds)} s ago). Reading is allowed; retry modifying in " +
                       $"{Math.Max(1, Math.Ceiling(left.TotalSeconds))} s or after that client disconnects or calls {ReleaseMethod}.";
            }
        }

        /// <summary>Marks the end of a modifying command so the idle timeout counts from its completion.</summary>
        public void Touch(string clientId)
        {
            lock (_lock)
            {
                if (_ownerId == clientId) _lastWriteAt = _clock();
            }
        }

        /// <summary>Releases the lease if this client holds it. Returns true when released.</summary>
        public bool Release(string clientId)
        {
            lock (_lock)
            {
                if (_ownerId != clientId) return false;
                _ownerId = null;
                _ownerName = null;
                return true;
            }
        }

        public object GetStatus(string clientId)
        {
            lock (_lock)
            {
                var now = _clock();
                ExpireIfIdle(now);
                if (_ownerId == null)
                    return new { locked = false, enabled = IdleTimeout > TimeSpan.Zero, idleTimeoutSeconds = IdleTimeout.TotalSeconds };

                return new
                {
                    locked = true,
                    enabled = true,
                    heldByYou = _ownerId == clientId,
                    owner = _ownerName,
                    heldForSeconds = Math.Round((now - _acquiredAt).TotalSeconds),
                    expiresInSeconds = Math.Max(0, Math.Ceiling((IdleTimeout - (now - _lastWriteAt)).TotalSeconds)),
                    idleTimeoutSeconds = IdleTimeout.TotalSeconds
                };
            }
        }

        private void ExpireIfIdle(DateTime now)
        {
            if (_ownerId != null && now - _lastWriteAt >= IdleTimeout)
            {
                _ownerId = null;
                _ownerName = null;
            }
        }
    }
}
