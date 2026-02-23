using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Infrastructure;

public sealed class PlaywrightFixture : IAsyncLifetime
{
    private static readonly string PidFilePath =
        Path.Combine(Path.GetTempPath(), "AlgoTradeForge_E2ETests", "frontend.pid");

    private KestrelWebApplicationFactory? _factory;
    private Process? _frontendProcess;
    private IPlaywright? _playwright;
    private string? _envLocalPath;

    private int _backendPort;
    private int _frontendPort;

    public IBrowser Browser { get; private set; } = null!;
    public string FrontendBaseUrl => $"http://localhost:{_frontendPort}";

    /// <summary>
    /// Finds an available TCP port by binding to port 0 and reading the assigned port.
    /// </summary>
    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task InitializeAsync()
    {
        // 0. Kill any stale frontend from a previous run that wasn't cleaned up
        KillStaleFrontend();

        // 1. Allocate free ports to avoid collisions with other test runs or dev servers
        _backendPort = GetFreePort();
        _frontendPort = GetFreePort();

        // 2. Start backend on Kestrel
        _factory = new KestrelWebApplicationFactory(_backendPort, _frontendPort);
        _factory.EnsureStarted();
        await WaitForHealthAsync(_factory.BaseUrl + "/swagger/index.html", "Backend",
            TimeSpan.FromSeconds(30));

        // 3. Start Next.js frontend dev server
        StartFrontend();
        await WaitForHealthAsync(FrontendBaseUrl, "Frontend", TimeSpan.FromSeconds(90));

        // 4. Install and launch Playwright browser
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"Playwright browser install failed with exit code {exitCode}");

        _playwright = await Playwright.CreateAsync();

        var headed = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADED") == "1";
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !headed,
            SlowMo = headed ? 250 : 0,
        });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        _playwright?.Dispose();

        KillFrontendProcess();
        CleanupEnvLocal();

        _factory?.Dispose();
    }

    private void StartFrontend()
    {
        var frontendDir = FindFrontendDirectory();

        // Write .env.local so Next.js Turbopack picks up the API URL at compile time.
        // Use FileShare.ReadWrite to avoid IOException when a lingering Next.js watcher
        // still holds the file open (common Windows file-locking scenario).
        _envLocalPath = Path.Combine(frontendDir, ".env.local");
        var envContent = System.Text.Encoding.UTF8.GetBytes($"NEXT_PUBLIC_API_URL={_factory!.BaseUrl}\n");
        using (var fs = new FileStream(_envLocalPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Write(envContent);
        }

        // Use cmd.exe /c on Windows for reliable .cmd script resolution
        if (OperatingSystem.IsWindows())
        {
            _frontendProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c npm run dev -- --port {_frontendPort}",
                    WorkingDirectory = frontendDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
        }
        else
        {
            _frontendProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "npm",
                    Arguments = $"run dev -- --port {_frontendPort}",
                    WorkingDirectory = frontendDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
        }

        // Also set in process env for belt-and-suspenders
        _frontendProcess.StartInfo.Environment["NEXT_PUBLIC_API_URL"] = _factory.BaseUrl;

        if (!_frontendProcess.Start())
            throw new InvalidOperationException("Failed to start frontend process");

        WritePidFile(_frontendProcess.Id);

        // Drain stdout/stderr to avoid blocking
        _frontendProcess.BeginOutputReadLine();
        _frontendProcess.BeginErrorReadLine();
    }

    private void CleanupEnvLocal()
    {
        if (_envLocalPath is not null && File.Exists(_envLocalPath))
        {
            try { File.Delete(_envLocalPath); }
            catch { /* best-effort */ }
        }
    }

    private void KillFrontendProcess()
    {
        if (_frontendProcess is null || _frontendProcess.HasExited)
        {
            DeletePidFile();
            _frontendProcess?.Dispose();
            return;
        }

        try
        {
            KillProcessTree(_frontendProcess.Id);
            _frontendProcess.WaitForExit(10_000);
        }
        catch { /* best-effort */ }
        finally
        {
            DeletePidFile();
            _frontendProcess.Dispose();
        }
    }

    /// <summary>
    /// Kills a stale frontend process from a previous run that wasn't cleaned up
    /// (e.g., the test runner crashed or was force-killed).
    /// </summary>
    private static void KillStaleFrontend()
    {
        if (!File.Exists(PidFilePath))
            return;

        try
        {
            var pidText = File.ReadAllText(PidFilePath).Trim();
            if (int.TryParse(pidText, out var pid))
            {
                try
                {
                    using var stale = Process.GetProcessById(pid);
                    KillProcessTree(pid);
                    stale.WaitForExit(10_000);
                }
                catch (ArgumentException) { /* process already exited */ }
            }
        }
        catch { /* best-effort */ }
        finally
        {
            DeletePidFile();
        }
    }

    private static void KillProcessTree(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/T /F /PID {pid}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            killer?.WaitForExit(10_000);
        }
        else
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { /* already exited */ }
        }
    }

    private static void WritePidFile(int pid)
    {
        var dir = Path.GetDirectoryName(PidFilePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);
        File.WriteAllText(PidFilePath, pid.ToString());
    }

    private static void DeletePidFile()
    {
        try { if (File.Exists(PidFilePath)) File.Delete(PidFilePath); }
        catch { /* best-effort */ }
    }

    private static string FindFrontendDirectory()
    {
        // Walk up from the test output directory to find the repo root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "frontend");
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "package.json")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find 'frontend' directory from " + AppContext.BaseDirectory);
    }

    private static async Task WaitForHealthAsync(string url, string serviceName, TimeSpan timeout)
    {
        using var http = new HttpClient(new HttpClientHandler
        {
            // Don't follow redirects — a 3xx from Next.js means it's alive
            AllowAutoRedirect = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var response = await http.GetAsync(url);
                var code = (int)response.StatusCode;
                // Any non-5xx response means the server is alive
                if (code < 500)
                    return;
            }
            catch
            {
                // Not ready yet
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"{serviceName} did not become healthy at {url} within {timeout.TotalSeconds}s");
    }
}
