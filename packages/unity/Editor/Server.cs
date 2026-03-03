#if !NO_MCP

using System;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using UnityEditor;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UnityEngine;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Nurture.MCP.Editor
{
    [InitializeOnLoad]
    public class Server
    {
        private static CancellationTokenSource _cancellationTokenSource;
        private static McpServerOptions _options;
        private static IServiceProvider _services;

        /// <summary>
        /// When set, Unity listens on this TCP port for one MCP client instead of using stdio.
        /// Enables connecting to an already-running Unity (e.g. started from Hub) from node-runner with -connectPort.
        /// </summary>
        private static int? _mcpPort;

        static Server()
        {
            // Register for domain reload to stop the server
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            string[] cmdArgs = System.Environment.GetCommandLineArgs();
            bool mcp = cmdArgs.Any(arg => string.Equals(arg, "-mcp", StringComparison.OrdinalIgnoreCase));
            if (!mcp)
            {
                return;
            }

            // Optional: -mcpPort &lt;port&gt; for TCP listen mode (connect to existing Unity from node-runner)
            for (int i = 0; i < cmdArgs.Length - 1; i++)
            {
                if (string.Equals(cmdArgs[i], "-mcpPort", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(cmdArgs[i + 1], out int port)
                    && port > 0 && port < 65536)
                {
                    _mcpPort = port;
                    break;
                }
            }

            Start();
        }

        private static void OnBeforeAssemblyReload()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            Stop();
        }

        private static void Start()
        {
            Debug.Log("[MCP] Starting server");

            Debug.unityLogger.logHandler = new UnityMcpLogHandler();

            _options = new()
            {
                ServerInfo = new() { Name = "Nurture Unity MCP", Version = "0.2.2" },
                Capabilities = new(),
                ServerInstructions =
                    @"- When copying a file inside of `Assets` folder, use the `Unity_CopyAsset` tool instead of generic file tools. 

                    - Do not use generic codebase search or file search tools on any files in the `Assets` folder other than for *.cs files.

                    - Do not use generic file tools (edit_file, apply, copy, move, etc) when working with anything in the `Assets` folder.

                    - When editing an existing scene or prefab, open it first.

                    - After creating or changing objects in a scene or prefab, focus on the objects that were created or changed.

                    - After making a change to a scene or prefab that you want to keep, save it.

                    - After editing a prefab, exit isolation mode before continuing to work on the scene.
             
                    - Take a screenshot after every change you make to a loaded Unity scene or prefab that affects visuals."
            };

            _services = new ServiceCollection()
                .AddSingleton(SynchronizationContext.Current)
                .BuildServiceProvider();

            var toolOptions = new McpServerToolCreateOptions() { Services = _services };

            CollectTools(_options, toolOptions);
            CollectPrompts(_options);

            _cancellationTokenSource = new();

            Task.Run(RunServer);
        }

        private static async Task RunServer()
        {
            using var loggerFactory = new UnityLoggerFactory(
                new LogLevel[] { LogLevel.Error, LogLevel.Critical, LogLevel.Warning }
            );

            if (_mcpPort is int port)
            {
                // TCP mode: listen for one client (e.g. node-runner with -connectPort). No need to be launched by node-runner.
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                Debug.Log($"[MCP] Listening on 127.0.0.1:{port}. Connect with node-runner -connectPort {port}");
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(_cancellationTokenSource.Token);
                }
                finally
                {
                    listener.Stop();
                }

                NetworkStream stream = client.GetStream();
                await using var transport = new StreamServerTransport(stream, stream, "Nurture Unity MCP", loggerFactory);
                await using IMcpServer server = McpServerFactory.Create(
                    transport,
                    _options,
                    loggerFactory,
                    _services
                );
                await server.RunAsync(_cancellationTokenSource.Token);
            }
            else
            {
                // Stdio mode: used when node-runner spawns Unity (current behavior).
                await using var stdioTransport = new StdioServerTransport(_options, loggerFactory);
                await using IMcpServer server = McpServerFactory.Create(
                    stdioTransport,
                    _options,
                    loggerFactory,
                    _services
                );
                await server.RunAsync(_cancellationTokenSource.Token);
            }
        }

        private static void CollectTools(
            McpServerOptions options,
            McpServerToolCreateOptions toolOptions
        )
        {
            var toolAssembly = Assembly.GetCallingAssembly();
            var toolTypes =
                from t in toolAssembly.GetTypes()
                where t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null
                select t;

            ToolsCapability tools = new() { ToolCollection = new() };

            foreach (var toolType in toolTypes)
            {
                foreach (
                    var toolMethod in toolType.GetMethods(
                        BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.Static
                            | BindingFlags.Instance
                    )
                )
                {
                    if (toolMethod.GetCustomAttribute<McpServerToolAttribute>() is not null)
                    {
                        var tool = McpServerTool.Create(toolMethod, options: toolOptions);
                        tools.ToolCollection.Add(tool);
                    }
                }
            }

            if (tools.ToolCollection.Count > 0)
            {
                options.Capabilities.Tools = tools;
            }
        }

        private static void CollectPrompts(McpServerOptions options)
        {
            var promptAssembly = Assembly.GetCallingAssembly();
            var promptTypes =
                from t in promptAssembly.GetTypes()
                where t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null
                select t;

            PromptsCapability prompts = new() { PromptCollection = new() };

            foreach (var promptType in promptTypes)
            {
                foreach (
                    var promptMethod in promptType.GetMethods(
                        BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.Static
                            | BindingFlags.Instance
                    )
                )
                {
                    if (promptMethod.GetCustomAttribute<McpServerPromptAttribute>() is not null)
                    {
                        var prompt = promptMethod.IsStatic
                            ? McpServerPrompt.Create(promptMethod)
                            : McpServerPrompt.Create(promptMethod, promptType);

                        prompts.PromptCollection.Add(prompt);
                    }
                }
            }

            if (prompts.PromptCollection.Count > 0)
            {
                options.Capabilities.Prompts = prompts;
            }
        }

        private static void Stop()
        {
            Debug.Log("[MCP] Stopping server");
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource = null;
        }
    }
}

#endif
