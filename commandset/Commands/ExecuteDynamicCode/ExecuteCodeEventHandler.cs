using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// 处理代码执行的外部事件处理器
    /// </summary>
    public class ExecuteCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, IMcpEventHandler
    {
        public const string TransactionModeAuto = "auto";
        public const string TransactionModeNone = "none";

        // Compiled snippets are cached by source hash: assemblies cannot be unloaded from
        // the Revit process, so recompiling the same snippet (retries) would leak memory.
        private const int MaxCachedSnippets = 64;
        private static readonly Dictionary<string, MethodInfo> CompiledSnippets = new Dictionary<string, MethodInfo>();
        private static List<MetadataReference> _cachedReferences;
        private static int _cachedAssemblyCount;

        private static readonly JsonSerializer ResultSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            MaxDepth = 8
        });

        // 代码执行参数
        private string _generatedCode;
        private object[] _executionParameters;
        private string _transactionMode = TransactionModeAuto;

        // 执行结果信息
        public ExecutionResultInfo ResultInfo { get; private set; }

        // 状态同步对象
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public Exception LastError { get; private set; }

        public void PrepareForRaise()
        {
            LastError = null;
            _resetEvent.Reset();
        }

        // 设置要执行的代码和参数
        public void SetExecutionParameters(string code, object[] parameters = null, string transactionMode = TransactionModeAuto)
        {
            _generatedCode = code;
            _executionParameters = parameters ?? Array.Empty<object>();
            _transactionMode = transactionMode == TransactionModeNone ? TransactionModeNone : TransactionModeAuto;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        // 等待执行完成 - IWaitableExternalEventHandler接口实现
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document
                          ?? throw new InvalidOperationException("No active document in Revit.");
                ResultInfo = null;

                // Compile before opening a transaction: compilation errors must not touch the model.
                var entryPoint = GetOrCompile(_generatedCode);

                object result;
                if (_transactionMode == TransactionModeNone)
                {
                    result = Invoke(entryPoint, doc, _executionParameters);
                }
                else
                {
                    using (var transaction = new Transaction(doc, "执行AI代码"))
                    {
                        transaction.Start();
                        result = Invoke(entryPoint, doc, _executionParameters);

                        var status = transaction.Commit();
                        if (status != TransactionStatus.Committed)
                            throw new InvalidOperationException(
                                $"Revit did not commit the transaction (status: {status}). See the returned warnings for the reason.");
                    }
                }

                ResultInfo = new ExecutionResultInfo
                {
                    Success = true,
                    Result = ToJson(result)
                };
            }
            catch (Exception ex)
            {
                LastError = ex;
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private static object Invoke(MethodInfo entryPoint, Document doc, object[] parameters)
        {
            try
            {
                return entryPoint.Invoke(null, new object[] { doc, parameters });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                // Surface the snippet's own exception (with its line, thanks to the PDB) instead of
                // "Exception has been thrown by the target of an invocation".
                var inner = ex.InnerException;
                var location = FindSnippetFrame(inner);
                throw new InvalidOperationException(
                    $"{inner.GetType().Name}: {inner.Message}{(location != null ? $" ({location})" : "")}", inner);
            }
        }

        private static string FindSnippetFrame(Exception ex)
        {
            var trace = new System.Diagnostics.StackTrace(ex, true);
            foreach (var frame in trace.GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>())
            {
                if (frame.GetMethod()?.DeclaringType?.FullName == "AIGeneratedCode.CodeExecutor" && frame.GetFileLineNumber() > 0)
                    return $"line {frame.GetFileLineNumber()}";
            }
            return null;
        }

        private static JToken ToJson(object result)
        {
            if (result == null) return JValue.CreateNull();
            try
            {
                return JToken.FromObject(result, ResultSerializer);
            }
            catch (Exception)
            {
                // Revit API objects are often not serializable; fall back to their text form.
                return new JValue(result.ToString());
            }
        }

        private static MethodInfo GetOrCompile(string code)
        {
            var key = Hash(code);
            lock (CompiledSnippets)
            {
                if (CompiledSnippets.TryGetValue(key, out var cached))
                    return cached;
            }

            var entryPoint = Compile(code);

            lock (CompiledSnippets)
            {
                if (CompiledSnippets.Count >= MaxCachedSnippets)
                    CompiledSnippets.Clear();
                CompiledSnippets[key] = entryPoint;
            }
            return entryPoint;
        }

        private static MethodInfo Compile(string code)
        {
            // 包装代码以规范入口点. "#line 1" makes compiler and runtime line numbers refer to the
            // user's snippet instead of the wrapper.
            var wrappedCode = $@"
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;

namespace AIGeneratedCode
{{
    public static class CodeExecutor
    {{
        public static object Execute(Document document, object[] parameters)
        {{
#line 1 ""snippet.cs""
{code}
#line default
        }}
    }}
}}";

            var syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode, path: "snippet.cs", encoding: Encoding.UTF8);

            var compilation = CSharpCompilation.Create(
                "AIGeneratedCode_" + Guid.NewGuid().ToString("N"),
                syntaxTrees: new[] { syntaxTree },
                references: GetReferences(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug)
            );

            using (var peStream = new MemoryStream())
            using (var pdbStream = new MemoryStream())
            {
                var result = compilation.Emit(peStream, pdbStream,
                    options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));

                // 处理编译结果
                if (!result.Success)
                {
                    var errors = string.Join("\n", result.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => $"Line {d.Location.GetMappedLineSpan().StartLinePosition.Line + 1}: {d.Id} {d.GetMessage()}"));
                    throw new InvalidOperationException($"Compilation failed:\n{errors}");
                }

                var assembly = Assembly.Load(peStream.ToArray(), pdbStream.ToArray());
                return assembly.GetType("AIGeneratedCode.CodeExecutor").GetMethod("Execute");
            }
        }

        /// <summary>
        /// References to every loaded assembly, rebuilt only when new assemblies were loaded.
        /// Creating them reads hundreds of files, which used to happen on every call.
        /// </summary>
        private static List<MetadataReference> GetReferences()
        {
            // Snippet assemblies are loaded from bytes (no Location), so they do not invalidate the cache.
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .ToList();
            lock (CompiledSnippets)
            {
                if (_cachedReferences != null && _cachedAssemblyCount == assemblies.Count)
                    return _cachedReferences;
            }

            var references = assemblies
                .GroupBy(a => a.Location, StringComparer.OrdinalIgnoreCase)
                .Select(g => (MetadataReference)MetadataReference.CreateFromFile(g.Key))
                .ToList();

            lock (CompiledSnippets)
            {
                _cachedReferences = references;
                _cachedAssemblyCount = assemblies.Count;
            }
            return references;
        }

        private static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty)));
        }

        public string GetName()
        {
            return "执行AI代码";
        }
    }

    // 执行结果数据结构
    public class ExecutionResultInfo
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        /// <summary>Value returned by the snippet, as JSON (no longer a JSON string inside JSON).</summary>
        [JsonProperty("result")]
        public JToken Result { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
