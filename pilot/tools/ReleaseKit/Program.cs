using ReleaseKit;

// WinDictate restricted pilot release tool. Usage:
//
//   dotnet run --project pilot/tools/ReleaseKit -c Release -- build  --out <folder> [--model <file>] [--allow-dirty] [--skip-tests] [--deny-file <file>]
//   dotnet run --project pilot/tools/ReleaseKit -c Release -- verify --path <submission folder or zip folder>
//   dotnet run --project pilot/tools/ReleaseKit -c Release -- sanitise --path <folder> [--deny-file <file>]
//
// "build" is what pilot/build-release.ps1 runs. Exit code 0 means every step,
// check and scan passed; anything else means the package must not be used.

try
{
    var options = Options.Parse(args);
    return options.Command switch
    {
        "build" => await BuildCommand.RunAsync(options),
        "verify" => Hashing.VerifyCommand(options.Required("path")),
        "sanitise" or "sanitize" => Sanitiser.Command(options.Required("path"), options.Get("deny-file")),
        _ => Usage()
    };
}
catch (ReleaseException ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("RELEASE FAILED: " + ex.Message);
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: ReleaseKit build --out <folder> [--model <file>] [--allow-dirty] [--skip-tests] [--deny-file <file>]");
    Console.Error.WriteLine("       ReleaseKit verify --path <folder>");
    Console.Error.WriteLine("       ReleaseKit sanitise --path <folder> [--deny-file <file>]");
    return 2;
}

namespace ReleaseKit
{
    /// <summary>A step failed: the release must stop.</summary>
    public sealed class ReleaseException(string message) : Exception(message);

    /// <summary>Command-line options: a command, then --name value pairs and --flags.</summary>
    public sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

        public string Command { get; private init; } = "";

        public static Options Parse(string[] args)
        {
            var options = new Options { Command = args.Length > 0 ? args[0].ToLowerInvariant() : "" };
            for (int i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ReleaseException($"Unexpected argument: {args[i]}");
                string name = args[i][2..];
                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                options._values[name] = hasValue ? args[++i] : null;
            }
            return options;
        }

        public bool Flag(string name) => _values.ContainsKey(name);

        public string? Get(string name) => _values.TryGetValue(name, out string? value) ? value : null;

        public string Required(string name) =>
            Get(name) ?? throw new ReleaseException($"--{name} is required for '{Command}'.");
    }
}
