using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Personicore.DeviceAgent.Agent;

namespace Personicore.DeviceAgent.Mac.Platform;

internal static class ChromeConnectionCheck
{
    public const string TestUrl = "https://example.com/";

    public static string? ActiveProxy(string settings)
    {
        string Field(string name) => Regex.Match(settings, @"(?m)^\s*" + name + @"\s*:\s*([^\r\n]+)").Groups[1].Value.Trim();
        if (Field("HTTPSEnable") != "1" || Field("HTTPSPort") != "3129") return null;
        if (!IPAddress.TryParse(Field("HTTPSProxy"), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return null;
        return $"{ip}:3129";
    }

    public static async Task<string> RunAsync(string proxy, CancellationToken ct)
    {
        var host = proxy.Split(':')[0];
        try
        {
            using var socket = new TcpClient();
            using var tcpTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tcpTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(host, 3129, tcpTimeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return AgentText.ChromeTcpFailed;
        }

        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"http://{proxy}"), UseProxy = true,
                AllowAutoRedirect = false, UseCookies = false,
            };
            // No certificate callback, browser cookies, auth headers or bypass of TLS.
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            using var request = new HttpRequestMessage(HttpMethod.Get, TestUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode ? AgentText.ChromeConnected : AgentText.ChromeHttpFailed;
        }
        catch (HttpRequestException ex)
        {
            return ex.HttpRequestError switch
            {
                HttpRequestError.SecureConnectionError => AgentText.ChromeTlsFailed,
                HttpRequestError.ProxyTunnelError => AgentText.ChromeTunnelFailed,
                _ => AgentText.ChromeHttpFailed,
            };
        }
        catch (OperationCanceledException) { return AgentText.ChromeHttpFailed; }
    }
}
