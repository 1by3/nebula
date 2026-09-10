using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nebula.Cli.Cloud;

using Nebula.Cli.Core;

/// <summary>A thin client for the Hetzner Cloud API (https://docs.hetzner.cloud).</summary>
public sealed class HcloudClient
{
    private const string Api = "https://api.hetzner.cloud/v1";
    private readonly HttpClient _http;

    public HcloudClient(string token)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public JsonNode Get(string path) => Send(HttpMethod.Get, path, null);
    public JsonNode Post(string path, object body) => Send(HttpMethod.Post, path, body);
    public JsonNode Delete(string path) => Send(HttpMethod.Delete, path, null);

    private JsonNode Send(HttpMethod method, string path, object? body)
    {
        Ui.Verbose($"hcloud {method} {path}");
        using var req = new HttpRequestMessage(method, Api + path);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        try { resp = _http.SendAsync(req).GetAwaiter().GetResult(); }
        catch (Exception e) { throw new CliError($"Hetzner API {method} {path}: {e.Message}"); }
        string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
        {
            string detail = text;
            try { detail = JsonNode.Parse(text)?["error"]?["message"]?.ToString() ?? text; } catch { }
            if ((int)resp.StatusCode == 401) throw new CliError("Hetzner rejected the API token", "run `nebula config hetzner` with a Read & Write token from the project's Security > API tokens page");
            throw new CliError($"Hetzner API {method} {path} failed ({(int)resp.StatusCode}): {detail}");
        }
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
    }

    /// <summary>Does the token work? Returns null when it does, else the failure message.</summary>
    public string? Check()
    {
        try { Get("/servers?per_page=1"); return null; }
        catch (CliError e) { return e.Message; }
    }

    public JsonNode? ByName(string collection, string name)
    {
        var r = Get($"/{collection}?name={Uri.EscapeDataString(name)}");
        return r[collection]?.AsArray().FirstOrDefault(x => x?["name"]?.ToString() == name);
    }

    public List<JsonNode> ServersByLabel(string selector)
    {
        var r = Get($"/servers?label_selector={Uri.EscapeDataString(selector)}&per_page=50");
        return r["servers"]?.AsArray().Where(x => x != null).Select(x => x!).ToList() ?? new List<JsonNode>();
    }

    public List<(string name, string city, string zone)> Locations()
    {
        var r = Get("/locations");
        return r["locations"]?.AsArray().Where(x => x != null)
            .Select(x => (x!["name"]!.ToString(), x["city"]?.ToString() ?? "", x["network_zone"]?.ToString() ?? "")).ToList()
            ?? new List<(string, string, string)>();
    }

    public void WaitAction(JsonNode? action, int timeoutSeconds = 180)
    {
        if (action == null) return;
        string id = action["id"]!.ToString();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var a = Get($"/actions/{id}")["action"]!;
            string status = a["status"]?.ToString() ?? "";
            if (status == "success") return;
            if (status == "error") throw new CliError($"Hetzner action {a["command"]} failed: {a["error"]?["message"]}");
            Thread.Sleep(2000);
        }
        throw new CliError($"Hetzner action {action["command"]} did not finish in {timeoutSeconds}s");
    }
}
