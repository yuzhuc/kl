using System.Diagnostics;
using Xunit;

namespace Kl.Tui.Tests;

public sealed class LocalServerTests
{
    [Fact]
    public void ReadsEndpointFromServerInfoFile()
    {
        var root = TempHome();
        try
        {
            File.WriteAllText(Path.Combine(root, "server.json"), """
                {
                  "server_name": "kl-core",
                  "server_version": "0.1.0",
                  "protocol_version": 1,
                  "transport": "named-pipe",
                  "endpoint": "kl-core-test",
                  "pid": %PID%,
                  "started_at_unix_ms": 0
                }
                """.Replace("%PID%", Environment.ProcessId.ToString()));

            var endpoint = KlCoreClient.ResolveEndpoint(new KlClientOptions { HomePath = root });

            Assert.Equal("kl-core-test", endpoint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExplicitEndpointWinsOverServerInfoFile()
    {
        var root = TempHome();
        try
        {
            File.WriteAllText(Path.Combine(root, "server.json"), """
                {"endpoint":"from-file","pid":%PID%}
                """.Replace("%PID%", Environment.ProcessId.ToString()));

            var endpoint = KlCoreClient.ResolveEndpoint(new KlClientOptions
            {
                HomePath = root,
                Endpoint = "explicit"
            });

            Assert.Equal("explicit", endpoint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingServerInfoFileKeepsStdioMode()
    {
        var root = TempHome();
        try
        {
            Assert.Null(KlCoreClient.ResolveEndpoint(new KlClientOptions { HomePath = root }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeadServerProcessFallsBackToStdio()
    {
        var root = TempHome();
        try
        {
            File.WriteAllText(Path.Combine(root, "server.json"), """
                {"endpoint":"kl-core-test","pid":999999}
                """);

            Assert.Null(KlCoreClient.ResolveEndpoint(new KlClientOptions { HomePath = root }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParsesServeCommandAndSocketName()
    {
        var options = CommandLine.Parse([
            "serve", "--socket", "kl-test", "--home", "C:\\kl\\home"
        ]);

        Assert.Equal("serve", options.Command);
        Assert.Equal("kl-test", options.SocketName);
        Assert.Equal("C:\\kl\\home", options.HomePath);
    }

    [Fact]
    public void ServeWithoutSocketNameUsesCoreDefault()
    {
        var options = CommandLine.Parse(["serve"]);

        Assert.Equal("serve", options.Command);
        Assert.Null(options.SocketName);
    }

    [Fact]
    public async Task StdioSessionStillStartsWithoutServerInfoFile()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "kl-stdio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var config = Path.Combine(root, "kl.toml");
        await File.WriteAllTextAsync(
            config,
            """
            [agent]
            provider = "scripted"
            model = "scripted"
            scripted_path = "script.json"
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "script.json"),
            """[{"text":"ok"}]""");
        var options = new TuiOptions
        {
            ConfigPath = config,
            HomePath = Path.Combine(root, "home"),
            Workspace = workspace
        };

        try
        {
            await using var session = await KlSessionClient.StartAsync(
                options.ToClientOptions(),
                options.ToSessionOptions(),
                new SilentPresenter());
            var outcome = await session.RunTurnAsync("hello");

            Assert.Equal("completed", outcome.Status);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RealServerModeRunsAScriptedTurn()
    {
        var corePath = Environment.GetEnvironmentVariable("KL_TEST_CORE_PATH");
        if (string.IsNullOrWhiteSpace(corePath) || !File.Exists(corePath))
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "kl-server-e2e-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(workspace);
        var config = Path.Combine(root, "kl.toml");
        await File.WriteAllTextAsync(
            config,
            """
            [agent]
            provider = "scripted"
            model = "scripted"
            scripted_path = "script.json"
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "script.json"),
            """[{"text":"ok"}]""");
        var endpoint = "kl-cs-" + Guid.NewGuid().ToString("N");
        using var process = Process.Start(new ProcessStartInfo(corePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "--serve",
                "--home", home,
                "--config", config,
                "--socket", endpoint
            }
        }) ?? throw new InvalidOperationException("failed to start test core");

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(Path.Combine(home, "server.json")))
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException("test core exited before listening");
                }
                Assert.True(DateTime.UtcNow < deadline, "server.json was not created");
                await Task.Delay(50);
            }

            var options = new TuiOptions
            {
                CorePath = corePath,
                ConfigPath = config,
                HomePath = home,
                Workspace = workspace
            };
            using var turnTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var session = await KlSessionClient.StartAsync(
                options.ToClientOptions(),
                options.ToSessionOptions(),
                new SilentPresenter(),
                turnTimeout.Token);
            var outcome = await session.RunTurnAsync("hello", turnTimeout.Token);
            Assert.Equal("completed", outcome.Status);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
    private static string TempHome()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "kl-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class SilentPresenter : ISessionPresenter
    {
        public ValueTask OnEventAsync(
            SessionEvent sessionEvent,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> RequestApprovalAsync(
            ApprovalRequestEvent request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }
}
