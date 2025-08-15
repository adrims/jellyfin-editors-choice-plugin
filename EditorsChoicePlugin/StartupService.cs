using EditorsChoicePlugin.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using MediaBrowser.Common.Configuration;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.Net.Mime;
using MediaBrowser.Common.Net;
using System.Text;
using System.Net;

namespace EditorsChoicePlugin;

public class StartupService : IScheduledTask
{
    public string Name => "EditorsChoice Startup";
    public string Key => "Jellyfin.Plugin.EditorsChoice.Startup";
    public string Description => "Startup Service for Editors choice";
    public string Category => "Startup Services";

    private readonly IServerApplicationHost _serverApplicationHost;
    private readonly ILogger<Plugin> _logger;
    private readonly IUserManager _userManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IConfigurationManager _configurationManager;
    private readonly PluginConfiguration _config;
    private readonly HttpClient _http = new();
    private string _basePath = "";

    public StartupService(
        IServerApplicationHost serverApplicationHost,
        ILogger<Plugin> logger,
        IUserManager userManager,
        IApplicationPaths applicationPaths,
        IServerApplicationHost applicationHost,
        IConfigurationManager configurationManager)
    {
        _serverApplicationHost = serverApplicationHost;
        _logger = logger;
        _userManager = userManager;
        _applicationPaths = applicationPaths;
        _applicationHost = applicationHost;
        _configurationManager = configurationManager;
        _config = Plugin.Instance!.Configuration;
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("EditorsChoice Startup. Registering file transformations.");

        // Normalize Mode from legacy booleans
        if (string.IsNullOrWhiteSpace(_config.Mode))
            _config.Mode = _config.ShowRandomMedia ? "RANDOM" : "FAVOURITES";

        // Resolve BasePath from network config (if reverse-proxied)
        try
        {
            var netCfg = Plugin.Instance!.ServerConfigurationManager.GetNetworkConfiguration();
            if (!string.IsNullOrWhiteSpace(netCfg.BaseUrl))
            {
                _basePath = "/" + netCfg.BaseUrl.TrimStart('/').Trim();
                _logger.LogInformation("EditorsChoice BasePath = {BasePath}", _basePath);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "EditorsChoice: unable to read BaseUrl; using '/'");
        }

        if (_config.DoScriptInject)
        {
            TryInjectClientScript();
        }
        else if (_config.FileTransformation)
        {
            // Don't block the scheduler; run async with retries
            _ = RegisterTransformation(cancellationToken);
        }

        return Task.CompletedTask;
    }

    private void TryInjectClientScript()
    {
        if (string.IsNullOrWhiteSpace(_applicationPaths.WebPath)) return;

        var indexFile = Path.Combine(_applicationPaths.WebPath, "index.html");
        if (!File.Exists(indexFile)) return;

        var indexContents = File.ReadAllText(indexFile);
        var scriptReplace = "<script plugin=\"EditorsChoice\".*?></script>(<style plugin=\"EditorsChoice\">.*?</style>)?";
        var scriptElement = $"<script injection=\"true\" plugin=\"EditorsChoice\" defer=\"defer\" src=\"{_basePath}/EditorsChoice/script\"></script>";

        if (indexContents.Contains(scriptElement))
        {
            _logger.LogInformation("EditorsChoice: client script already injected in {Index}", indexFile);
            return;
        }

        _logger.LogInformation("EditorsChoice: injecting client script into {Index}", indexFile);
        indexContents = System.Text.RegularExpressions.Regex.Replace(indexContents, scriptReplace, "");
        var bodyClosing = indexContents.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyClosing == -1)
        {
            _logger.LogWarning("EditorsChoice: could not find </body> in {Index}", indexFile);
            return;
        }

        indexContents = indexContents.Insert(bodyClosing, scriptElement);
        try
        {
            File.WriteAllText(indexFile, indexContents);
            _logger.LogInformation("EditorsChoice: script injected into {Index}", indexFile);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "EditorsChoice: error writing {Index}", indexFile);
        }
    }

    public async Task RegisterTransformation(CancellationToken ct)
    {
        try
        {
            // Prefer loopback; fall back to configured public URL if provided
            var baseUrl = string.IsNullOrWhiteSpace(_config.Url)
                ? $"http://127.0.0.1:{_applicationHost.HttpPort}"
                : _config.Url!.TrimEnd('/');

            var baseUri = new Uri(baseUrl);

            // Wait for the web server/routes to be ready
            var ready = await WaitUntilServerReady(baseUri, ct);
            if (!ready)
            {
                _logger.LogError("EditorsChoice: server never became ready; aborting registration.");
                return;
            }

            var data = new JsonObject
            {
                ["id"] = "b3d45a0e-3dac-4413-97df-32a13316571e",
                ["fileNamePattern"] = "index.html",
                ["transformationEndpoint"] = $"{_basePath}/editorschoice/transform"
            };

            var url = new Uri(baseUri, $"{_basePath}/FileTransformation/RegisterTransformation");
            var payload = new StringContent(data.ToString(), Encoding.UTF8, MediaTypeNames.Application.Json);

            // Retry with exponential backoff on 503/404/timeout
            var delay = TimeSpan.FromSeconds(1);
            for (var attempt = 1; attempt <= 8 && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    var resp = await _http.PostAsync(url, payload, ct);
                    _logger.LogInformation("EditorsChoice: POST {Url}", url);
                    if (resp.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("EditorsChoice: transformation registered (attempt {Attempt})", attempt);
                        return;
                    }

                    if (resp.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.NotFound)
                    {
                        _logger.LogInformation(
                            "EditorsChoice: endpoint not ready ({Status}) attempt {Attempt}; retrying in {Delay}s",
                            (int)resp.StatusCode, attempt, delay.TotalSeconds);
                    }
                    else
                    {
                        _logger.LogWarning("EditorsChoice: registration failed with {Status}; aborting.",
                            (int)resp.StatusCode);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "EditorsChoice: error on attempt {Attempt}; retrying in {Delay}s",
                        attempt, delay.TotalSeconds);
                }

                await Task.Delay(delay, ct);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 15000));
            }

            _logger.LogError("EditorsChoice: could not register transformation after retries.");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "EditorsChoice: unexpected error registering transformation.");
        }
    }

    private async Task<bool> WaitUntilServerReady(Uri baseUri, CancellationToken ct)
    {
        // Small initial buffer
        await Task.Delay(800, ct);

        // Probe a lightweight endpoint that exists early
        // Try /System/Info/Public first; fallback to /
        var probes = new[]
        {
            new Uri(baseUri, $"{_basePath}/System/Info/Public"),
            new Uri(baseUri, $"{_basePath}/")
        };

        var delay = TimeSpan.FromMilliseconds(500);
        for (var i = 0; i < 10 && !ct.IsCancellationRequested; i++)
        {
            foreach (var probe in probes)
            {
                try
                {
                    using var resp = await _http.GetAsync(probe, ct);
                    if ((int)resp.StatusCode < 500) // 2xx/3xx/404 mean HTTP pipeline is up
                    {
                        _logger.LogInformation("EditorsChoice: server ready (probe {Probe}, status {Status})", probe, (int)resp.StatusCode);
                        return true;
                    }
                }
                catch { /* ignore until next retry */ }
            }
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 3000));
        }
        return false;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup };
    }
}
