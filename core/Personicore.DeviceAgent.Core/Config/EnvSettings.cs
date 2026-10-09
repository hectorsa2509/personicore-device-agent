using System.Text;

namespace Personicore.DeviceAgent.Config;

/// <summary>
/// Settings for sending to the Graph, read from a `.env` file.
///
/// The rule that governs the whole agent: **with no endpoint, nothing is sent** and the
/// dump goes to the desktop. A missing `.env`, an empty one and one with a blank
/// endpoint are all the same case, and it is the default — so the agent can be handed
/// out and run with no risk of it writing to production by accident.
/// </summary>
public sealed class EnvSettings
{
    /// <summary>Where the POST goes. Empty ⇒ the endpoint served by the router (get_config.sh).</summary>
    public string GraphQlEndpoint { get; init; } = string.Empty;

    /// <summary>Fixed credential. Empty ⇒ the JWT served by the meter node is used.</summary>
    public string GraphQlToken { get; init; } = string.Empty;

    /// <summary>Operation name, only for the logs and the dump.</summary>
    public string OperationName { get; init; } = "routerIpsAssigment";

    /// <summary>
    /// The mutation document. Empty ⇒ the built-in `routerIpsAssigment` one is used. A
    /// document of one's own has to accept the same variables ($routerId, $addIps,
    /// $removeIps, $updateIps): those are what the agent assembles, and only those.
    /// </summary>
    public string MutationDocument { get; init; } = string.Empty;

    /// <summary>Where the settings came from, so it can be diagnosed.</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>true if the .env overrides the router's endpoint.</summary>
    public bool HasEndpoint => !string.IsNullOrWhiteSpace(GraphQlEndpoint);

    /// <summary>
    /// PERSONICORE_DRY_RUN=true: read the household but never send; the mutations are
    /// written to the desktop file instead. Off by default: the agent saves for real,
    /// like the router's network.html.
    /// </summary>
    public bool DryRun { get; init; }

    private const string Prefix = "PERSONICORE_";

    /// <summary>
    /// The credential compiled into the executable, as a source. It is not a path: there
    /// is no file on the panelist's disk that corresponds to it.
    /// </summary>
    internal const string PackagedSource = "(packaged)";

    private const string PackagedResource = "personicore-packaged.env";

    /// <summary>
    /// Where the `.env` is looked for, in order. The first one that exists wins. Next to
    /// the executable comes first on purpose: that is what allows the agent to be handed
    /// out as a folder (exe + .env) without touching anything on the computer.
    /// </summary>
    public static IEnumerable<string> CandidatePaths()
    {
        var explicitPath = Environment.GetEnvironmentVariable($"{Prefix}ENV_FILE");
        if (!string.IsNullOrWhiteSpace(explicitPath)) yield return explicitPath;

        var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(exeDirectory))
        {
            yield return Path.Combine(exeDirectory, ".env");

            // With extensions hidden (the norm on Windows), creating ".env" from
            // Explorer or Notepad actually leaves ".env.txt".
            yield return Path.Combine(exeDirectory, ".env.txt");
        }

        // Compiled in when the package was built. It sits exactly where a packaged .env
        // next to the executable used to, so a .env left there still overrides it — which
        // is what keeps a developer's test build and the macOS bundle working — but the
        // panelist is handed one file with nothing beside it to lose, to leave behind
        // when the .exe is copied somewhere else, or to edit by mistake.
        yield return PackagedSource;

        yield return Path.Combine(AgentPaths.UserData, ".env");

        yield return Path.Combine(AgentPaths.MachineData, ".env");
    }

    public static EnvSettings Load()
    {
        var (values, source) = ReadFirstAvailable();

        // A real environment variable beats the file: that is what allows testing
        // another endpoint from a console without editing anything.
        string Read(string key, string fallback = "")
        {
            var fromProcess = Environment.GetEnvironmentVariable(Prefix + key);
            if (!string.IsNullOrWhiteSpace(fromProcess)) return fromProcess.Trim();

            return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : fallback;
        }

        var settings = new EnvSettings
        {
            GraphQlEndpoint = Read("GRAPHQL_ENDPOINT"),
            GraphQlToken = Read("GRAPHQL_TOKEN"),
            OperationName = Read("GRAPHQL_OPERATION", "routerIpsAssigment"),
            MutationDocument = ReadMutation(Read, Path.GetDirectoryName(source)),
            DryRun = Read("DRY_RUN").ToLowerInvariant() is "true" or "1" or "yes" or "on",
            SourcePath = source,
        };

        var endpoint = settings.HasEndpoint ? settings.GraphQlEndpoint : "the router's (get_config.sh)";
        Log.Info($"GraphQL endpoint: {endpoint} · source={Describe(source)}"
                 + (settings.DryRun ? " · PERSONICORE_DRY_RUN is on" : string.Empty));

        return settings;
    }

    /// <summary>
    /// The document can be given inline or, better, by pointing at a `.graphql` file: a
    /// multi-line mutation inside a `.env` is awkward to maintain. A relative path is
    /// taken against the `.env`, not the working directory: when started from the Run key
    /// or a scheduled task, that is System32.
    /// </summary>
    private static string ReadMutation(Func<string, string, string> read, string? envDirectory)
    {
        var file = read("GRAPHQL_MUTATION_FILE", string.Empty);
        if (!string.IsNullOrWhiteSpace(file))
        {
            file = Environment.ExpandEnvironmentVariables(file.Trim());
            if (!Path.IsPathRooted(file))
            {
                var baseDirectory = !string.IsNullOrEmpty(envDirectory)
                    ? envDirectory
                    : Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty;
                file = Path.Combine(baseDirectory, file);
            }

            try
            {
                return File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                Log.Error($"Could not read the mutation from {file}; using the built-in one", ex);
                return string.Empty;
            }
        }

        return read("GRAPHQL_MUTATION", string.Empty);
    }

    /// <summary>
    /// The credential this package was built with, or null when it was built without one
    /// — a legitimate package: the agent then uses the jwt and endpoint the meter node
    /// serves from get_config.sh, exactly as the router's own web page does.
    /// </summary>
    private static Dictionary<string, string>? ReadPackaged()
    {
        using var stream = typeof(EnvSettings).Assembly.GetManifestResourceStream(PackagedResource);
        if (stream is null) return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);

        // An empty resource is the same as no resource: a build that embedded nothing
        // must not shadow the .env in the user's folder.
        var values = Parse(ReadAllLines(reader));
        return values.Count == 0 ? null : values;
    }

    private static IEnumerable<string> ReadAllLines(TextReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static string Describe(string source) => source switch
    {
        "" => "no .env file",
        PackagedSource => "baked into the executable",
        _ => source,
    };

    private static (Dictionary<string, string> Values, string Source) ReadFirstAvailable()
    {
        foreach (var path in CandidatePaths())
        {
            try
            {
                if (path == PackagedSource)
                {
                    var packaged = ReadPackaged();
                    if (packaged is not null) return (packaged, PackagedSource);
                    continue;
                }

                if (!File.Exists(path)) continue;
                return (Parse(File.ReadAllLines(path, Encoding.UTF8)), path);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read {path}: {ex.Message}");
            }
        }

        return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), string.Empty);
    }

    /// <summary>
    /// `.env` parser: `KEY=value`, with `#` for comments, optional `export` and optional
    /// quotes. Inside double quotes `\n` and `\t` are interpreted, which is what is needed
    /// when someone pastes a mutation on a single line.
    /// </summary>
    internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                line = line[7..].TrimStart();

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            if (key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) key = key[Prefix.Length..];

            values[key] = Unquote(line[(separator + 1)..].Trim());
        }

        return values;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1];

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"");

        // Unquoted, a `#` mid-line is a trailing comment.
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return comment >= 0 ? value[..comment].TrimEnd() : value;
    }
}
