using System.Diagnostics;
using Microsoft.Playwright;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Infrastructure;

public sealed class PlaywrightFixture : IAsyncLifetime
{
    private const string BackendUrl = "http://localhost:5180";
    private const string FrontendUrl = "http://localhost:3180";
    private const int FrontendPort = 3180;

    private KestrelWebApplicationFactory? _factory;
    private Process? _frontendProcess;
    private IPlaywright? _playwright;
    private string? _envLocalPath;

    public IBrowser Browser { get; private set; } = null!;
    public string FrontendBaseUrl => FrontendUrl;

    public async Task InitializeAsync()
    {
        // 1. Start backend on Kestrel
        _factory = new KestrelWebApplicationFactory();
        _factory.EnsureStarted();
        await WaitForHealthAsync(BackendUrl + "/swagger/index.html", "Backend",
            TimeSpan.FromSeconds(30));

        // 2. Start Next.js frontend dev server
        StartFrontend();
        await WaitForHealthAsync(FrontendUrl, "Frontend", TimeSpan.FromSeconds(90));

        // 3. Install and launch Playwright browser
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

        // Write .env.local so Next.js Turbopack picks up the API URL at compile time
        _envLocalPath = Path.Combine(frontendDir, ".env.local");
        File.WriteAllText(_envLocalPath, $"NEXT_PUBLIC_API_URL={BackendUrl}\n");

        // Use cmd.exe /c on Windows for reliable .cmd script resolution
        if (OperatingSystem.IsWindows())
        {
            _frontendProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c npm run dev -- --port {FrontendPort}",
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
                    Arguments = $"run dev -- --port {FrontendPort}",
                    WorkingDirectory = frontendDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
        }

        // Also set in process env for belt-and-suspenders
        _frontendProcess.StartInfo.Environment["NEXT_PUBLIC_API_URL"] = BackendUrl;

        if (!_frontendProcess.Start())
            throw new InvalidOperationException("Failed to start frontend process");

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
            return;

        try
        {
            // Kill the process tree (npm spawns child processes)
            if (OperatingSystem.IsWindows())
            {
                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/T /F /PID {_frontendProcess.Id}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                killer?.WaitForExit(5000);
            }
            else
            {
                _frontendProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            /* best-effort cleanup */
        }
        finally
        {
            _frontendProcess.Dispose();
        }
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
