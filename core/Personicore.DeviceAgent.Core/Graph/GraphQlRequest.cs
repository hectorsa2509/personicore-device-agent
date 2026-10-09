using System.Text.Json;
using System.Text.Json.Serialization;

namespace Personicore.DeviceAgent.Graph;

/// <summary>
/// A GraphQL request already assembled, before it is sent. It is a type of its own so
/// offline mode can dump exactly the body that would be sent: if the dump were built
/// separately it would stop reflecting what the agent does the moment either branch
/// changed.
/// </summary>
/// <param name="OperationName">Operation name, for the logs and for the dump.</param>
/// <param name="Query">Documento GraphQL.</param>
/// <param name="Variables">Variables, exactly as they are serialized.</param>
public sealed record GraphQlRequest(string OperationName, string Query, object Variables)
{
    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The exact HTTP body, as it is serialized when sending.</summary>
    public string ToPrettyJson() =>
        JsonSerializer.Serialize(new { query = Query, variables = Variables }, PrettyOptions);

    /// <summary>
    /// The variables on their own, indented. They are what is actually reviewed in the
    /// dump: the GraphQL document is fixed, the variables are this computer's data.
    /// </summary>
    public string VariablesJson() => JsonSerializer.Serialize(Variables, PrettyOptions);
}
