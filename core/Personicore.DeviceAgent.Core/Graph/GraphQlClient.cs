using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Graph;

public sealed class GraphQlError
{
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

public sealed class GraphQlResponse<T>
{
    [JsonPropertyName("data")] public T? Data { get; set; }
    [JsonPropertyName("errors")] public List<GraphQlError>? Errors { get; set; }

    [JsonIgnore]
    public bool HasErrors => Errors is { Count: > 0 };

    [JsonIgnore]
    public string ErrorText => Errors is null ? string.Empty : string.Join(" | ", Errors.Select(e => e.Message));
}

/// <summary>A GraphQL backend failure, already flattened for the log and the UI.</summary>
public class GraphQlException(string message) : Exception(message);

/// <summary>
/// The backend answered fine, but there is nobody to assign this computer to yet: the
/// router is not set up in the panel, or its household has no panelists. This is not a
/// fault of the agent or the network, and the panelist cannot fix it from their computer
/// — which is why it is kept apart from an error and told in different words.
/// </summary>
public sealed class HouseholdNotAssignedException(string message) : GraphQlException(message);

/// <summary>
/// The backend answered, and the answer will not change however many times it is asked.
///
/// The distinction that matters to the loop: an ordinary failure is worth retrying in a
/// minute, because networks come back and nodes reboot. These are not. Retrying one on the
/// failure cadence is a stack trace a minute, for days, burying everything else in the log
/// the one time somebody comes to read it.
/// </summary>
public class BackendNotReadyException(string message) : GraphQlException(message);

/// <summary>
/// The backend refused the credential rather than the data: the resolver wants a
/// panelist's JWT and the agent does not have one.
///
/// The fix is in nobody's reach on this computer. The agent has no credential of its own
/// and asks the panelist for none — it uses the one baked into the package, or the
/// household's from <c>get_config.sh</c>, which belongs to the *router*. A resolver that
/// derives the panelist from the token's user rejects either one every time, on every
/// cycle, for ever. Telling it apart from a bad payload is what stops support chasing the
/// device data when the problem is who signed the request.
/// </summary>
public sealed class PanelistCredentialException(string message) : BackendNotReadyException(message);

/// <summary>
/// The panel no longer has the Device this computer's stored id points at.
///
/// Deliberately <b>not</b> a <see cref="BackendNotReadyException"/>: waiting changes nothing,
/// and unlike every other refusal here there is something the agent can do about it. The
/// stored id is stale — support deleted or deactivated the row — so it has to be let go of and
/// the device created again. Re-creating cannot duplicate anything: the backend has just said,
/// authoritatively, that nothing exists under that id in this household.
/// </summary>
public sealed class DeviceGoneException(string message) : GraphQlException(message);

/// <summary>
/// The address and the owners reached the panel; only the device's own row could not be
/// made.
///
/// Its own class because it is not the same event as "nothing worked", and the panelist
/// must not be told it is. In the deployed contract <c>routerIpsAssigment</c> is what
/// associates a computer with its panelists, and when that call succeeded the panel does
/// know this computer, its owners and its address. What is outstanding is the separate
/// Device record — support's problem, not theirs, and nothing they can act on.
/// </summary>
public sealed class DeviceRecordPendingException(string message) : BackendNotReadyException(message);

/// <summary>
/// The panel has this computer registered against a different meter node from the one it is
/// on now. Moving house is the ordinary cause; a node record recreated in the panel is the
/// other.
/// </summary>
/// <remarks>
/// A <see cref="BackendNotReadyException"/> because the loop's answer is the same one —
/// wait, do not retry in a minute — and its own class because the sentence the panelist
/// reads is not. "Personicore is not ready for this computer yet" sends whoever reads it
/// looking at a deployment, when what is wrong is which router this computer's record hangs
/// off; an address report cannot move a Network Device between routers, so no retry fixes it.
///
/// Raised only on the backend's own marker. The agent also <i>knows</i> locally when the node
/// in front of it is not the one it registered on, and deliberately does not announce this
/// then: a replaced node record looks exactly the same from here, and a headline must not
/// describe a cause it is guessing at.
/// </remarks>
public sealed class DeviceOnAnotherNodeException(string message) : BackendNotReadyException(message);

/// <summary>
/// The operation the agent was built against is not in the schema this endpoint serves.
///
/// Its own class because it is the expected state of an agent that is ahead of its
/// backend, which is where this one has spent most of its life. It is not a bug and not a
/// misconfiguration: the package is right, the deployment has not caught up, and the only
/// thing to do is ask again later — much later than a minute.
/// </summary>
public sealed class SchemaNotReadyException(string message) : BackendNotReadyException(message);

/// <summary>
/// Minimal GraphQL transport. The credential is the JWT served by the node itself
/// (get_config.sh) and travels in Authorization with no Bearer prefix, exactly as in
/// update_ips_from_graphql.sh and the router's web page.
/// </summary>
public sealed class GraphQlClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public GraphQlClient(string endpoint, string jwt, TimeSpan timeout)
    {
        _endpoint = endpoint;
        // Direct, never through the system proxy: with the Personicore proxy switched on,
        // the node would intercept TLS and the agent would lose its own backend.
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = timeout };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", jwt);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>The endpoint this client points at; shown in diagnostics and in the dump.</summary>
    public string Endpoint => _endpoint;

    public async Task<T> ExecuteAsync<T>(GraphQlRequest request, CancellationToken ct)
    {
        var operationName = request.OperationName;
        var payload = new { query = request.Query, variables = request.Variables };

        Log.Debug($"GraphQL → {operationName}");

        using var response = await _http.PostAsJsonAsync(_endpoint, payload, JsonOptions, ct)
            .ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        GraphQlResponse<T>? body = null;
        try
        {
            body = JsonSerializer.Deserialize<GraphQlResponse<T>>(text, JsonOptions);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            // Not a GraphQL body (proxy page, gateway error): report the HTTP status below.
        }

        // Apollo answers validation errors with HTTP 400 *and* a GraphQL errors body: the
        // messages are what tells "unknown field" apart from a real failure, so they win.
        if (!response.IsSuccessStatusCode)
        {
            var detail = body?.HasErrors == true ? body.ErrorText : response.ReasonPhrase;
            throw new GraphQlException($"{operationName}: HTTP {(int)response.StatusCode} {detail}");
        }

        if (body is null)
            throw new GraphQlException($"{operationName}: empty response from the backend.");

        if (body.HasErrors)
            throw new GraphQlException($"{operationName}: {body.ErrorText}");

        if (body.Data is null)
            throw new GraphQlException($"{operationName}: the response has no data.");

        return body.Data;
    }

    public void Dispose() => _http.Dispose();
}
