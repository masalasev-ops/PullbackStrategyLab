using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Research;

namespace PullbackStrategyLab.Worker.Seats;

/// <summary>
/// The pinned seat: the Claude Code CLI in print mode, drawing on the operator's plan.
///
/// <b>It is a subprocess because there is no .NET Agent SDK, and that was found at 6.5 rather than
/// assumed.</b> The SDK is a library for Python and TypeScript only; the documented way to drive
/// the same loop from another language is to run the CLI with <c>-p</c> and a structured output
/// format. So the decision's phrase "uses the Agent SDK" is satisfied here by the CLI the SDK
/// itself wraps, and what changes is not the transport but what an assertion about it can reach.
/// see: The subscription seat is the Claude Code CLI in print mode, and the empty tool set is read back rather than asserted
///
/// <b>The empty tool set is a fact the run reports, not a property of the arguments alone.</b>
/// <c>--allowedTools</c> auto-approves and does not restrict, so passing it nothing would have left
/// the full tool set in the model's context and merely unapproved. <c>--disallowedTools "*"</c>
/// removes every tool from that context. Both halves are here: the argument is built once and
/// asserted, and the session's own reported tool list is read back and the answer is refused if it
/// is not empty. The second half is what turns the 1.5 obligation from a test into a guard, because
/// a tool arriving from the operator's own configuration is something no test over this file could
/// have seen.
/// see: The AI writes only to the proposal store
///
/// <b>It runs in an empty directory, and that is load-bearing.</b> Without <c>--bare</c> a print
/// session loads CLAUDE.md, hooks, MCP servers and plugins discovered from the working directory,
/// and this repository is full of all four. <c>--bare</c> is the documented fix and cannot be used:
/// it never reads the subscription login, which is the one thing this transport exists for. So the
/// seat runs somewhere with none of them, and <c>--strict-mcp-config</c> closes the MCP half.
/// </summary>
public sealed class SubscriptionTransport : IResearchTransport
{
    /// <summary>The argument that empties the tool set. Named once so the code and the test read one string.</summary>
    public const string EmptyToolSet = "*";

    /// <summary>The directory the seat asks from: empty, and never the repository or the data store.</summary>
    public const string AskDirectoryName = "researcher-seat";

    private readonly ResearcherOptions _options;
    private readonly string _dataRoot;

    public SubscriptionTransport(IOptions<PullbackStrategyLabOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Researcher;
        _dataRoot = options.Value.DataRoot;
    }

    public string Transport => SeatTransport.Subscription;

    public string ConfiguredModel => _options.Model;

    /// <summary>
    /// The arguments one ask is made with, built where a test can read them.
    ///
    /// A method rather than a literal inside <see cref="Ask"/>, because the empty tool set is the
    /// whole of the guard on a hard rule and an assertion that had to launch a process to see it
    /// would not run in the suite.
    /// </summary>
    public IReadOnlyList<string> Arguments() =>
    [
        // Print mode: one turn, no terminal.
        "-p", ProposalPrompt.Instruction,

        // Every tool removed from the model's context. Not --allowedTools, which approves rather
        // than restricts, and would have left the full set present and merely unapproved.
        "--disallowedTools", EmptyToolSet,

        // No MCP server except those named by --mcp-config, and none is named.
        "--strict-mcp-config",

        // Nobody is awake to answer a prompt, so anything that would ask is denied rather than
        // waited on. A weekly unattended job that blocked on a permission request would hang until
        // the timeout and record an unavailable week for a reason nobody could see.
        "--permission-prompts", "none",

        "--model", _options.Model,

        // The stream carries the session's own tool list in its first event, which the text and
        // json formats do not. That event is why this format is chosen.
        "--output-format", "stream-json",
        "--verbose",
    ];

    public SeatAnswer Ask(string pack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pack);

        string? executable = ResolveOnPath(_options.CommandName);

        if (executable is null)
        {
            return SeatAnswer.Unavailable(
                Transport, ConfiguredModel,
                $"no \"{_options.CommandName}\" was found on the path, so the subscription seat could not be asked");
        }

        try
        {
            return Run(executable, pack, cancellationToken);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return SeatAnswer.Unavailable(
                Transport, ConfiguredModel,
                $"the subscription seat could not be run: {failure.Message}");
        }
    }

    private SeatAnswer Run(string executable, string pack, CancellationToken cancellationToken)
    {
        string askDirectory = Path.Combine(_dataRoot, AskDirectoryName);
        Directory.CreateDirectory(askDirectory);

        (string fileName, IReadOnlyList<string> prefix) = Launch(executable);

        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = askDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in prefix.Concat(Arguments()))
        {
            start.ArgumentList.Add(argument);
        }

        // Banned on every transport, and cleared on the child rather than only asserted of the
        // parent: on this path its presence silently defeats plan authentication and bills API
        // rates, which is a failure that costs money and looks like success.
        start.Environment.Remove(ResearcherOptions.BannedEnvironmentVariable);

        using var process = new Process { StartInfo = start };
        process.Start();

        // The pack goes on standard input rather than in an argument. It is tens of thousands of
        // tokens and every platform has a command-line length limit well under that.
        process.StandardInput.Write(pack);
        process.StandardInput.Close();

        string output = process.StandardOutput.ReadToEnd();
        string errors = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(TimeSpan.FromSeconds(_options.TimeoutSeconds)))
        {
            process.Kill(entireProcessTree: true);
            return SeatAnswer.Unavailable(
                Transport, ConfiguredModel,
                $"the subscription seat did not answer within {_options.TimeoutSeconds} second(s)");
        }

        cancellationToken.ThrowIfCancellationRequested();

        return ReadStream(output, errors, process.ExitCode, ConfiguredModel);
    }

    /// <summary>
    /// What the stream said: the tool list it reported, the model that served it, and the result.
    ///
    /// Pure and public, so the tool-set guard is proved against streams written by hand. A guard
    /// that could only be exercised by a real subscription call would be a guard the suite never
    /// runs, which is the shape this corpus refuses on a rule it has broken four times.
    /// </summary>
    public static SeatAnswer ReadStream(string output, string errors, int exitCode, string configuredModel)
    {
        ArgumentNullException.ThrowIfNull(output);

        string? servedModel = null;
        IReadOnlyList<string>? tools = null;
        string? result = null;
        bool isError = false;

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonElement message;

            try
            {
                message = JsonDocument.Parse(line).RootElement;
            }
            catch (JsonException)
            {
                // A line the stream format does not promise. Skipped rather than fatal: the run is
                // judged on the events it did carry, and a missing init event fails below on its
                // own terms rather than on a parse error somewhere else in the stream.
                continue;
            }

            string type = message.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";

            if (type == "system" && message.TryGetProperty("subtype", out JsonElement subtype)
                && subtype.GetString() == "init")
            {
                servedModel = message.TryGetProperty("model", out JsonElement m) ? m.GetString() : null;
                tools = message.TryGetProperty("tools", out JsonElement ts) && ts.ValueKind == JsonValueKind.Array
                    ? [.. ts.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
                    : null;
            }

            if (type == "result")
            {
                result = message.TryGetProperty("result", out JsonElement r) ? r.GetString() : null;
                isError = message.TryGetProperty("is_error", out JsonElement e) && e.ValueKind == JsonValueKind.True;
            }
        }

        // Refused before the answer is read, on the two shapes that mean the seat was not the seat
        // this lab configured. A session that reported no tool list at all is refused with the
        // same force as one that reported a tool: an unreadable guard is not a passed one.
        if (tools is null)
        {
            return SeatAnswer.Unavailable(
                SeatTransport.Subscription, configuredModel,
                "the session reported no tool list, so the empty tool set could not be read back and "
                + "the answer is refused");
        }

        if (tools.Count > 0)
        {
            return SeatAnswer.Unavailable(
                SeatTransport.Subscription, configuredModel,
                $"the session reported {tools.Count} tool(s) ({string.Join(", ", tools)}) where the seat "
                + "requires none, so the answer is refused");
        }

        if (exitCode != 0 || isError || string.IsNullOrWhiteSpace(result))
        {
            string because = !string.IsNullOrWhiteSpace(result) ? result!
                : !string.IsNullOrWhiteSpace(errors) ? errors.Trim()
                : $"the seat exited {exitCode} with nothing on either stream";

            return SeatAnswer.Unavailable(SeatTransport.Subscription, configuredModel, because);
        }

        return SeatAnswer.Answer(
            SeatTransport.Subscription, configuredModel, servedModel, result!,
            [new SeatPin(SeatPin.ToolsReported, "none")]);
    }

    /// <summary>
    /// What to launch and what to put in front of the arguments.
    ///
    /// <b>The one place a platform's own launcher is used, and it is reached only for a shim that
    /// platform created.</b> A batch shim is not an executable image, so starting one directly
    /// fails; a package manager on Windows installs the CLI as exactly that. The command processor
    /// is read from the environment rather than named, and every other case launches the file
    /// itself, so nothing here assumes a platform that the other branch does not.
    /// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Prefix) Launch(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        string extension = Path.GetExtension(executable);

        bool isShim =
            string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);

        return isShim
            ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", ["/c", executable])
            : (executable, []);
    }

    /// <summary>
    /// The command on the path, or nothing.
    ///
    /// <b>Searched rather than trusted to the launcher, on the shape 6.10 worked out for bash.</b>
    /// A name with no extension is not executable on Windows and a name resolved by the wrong rule
    /// is the fault that makes a gate pass by never running. What is found is returned so the run
    /// can say which file it used.
    /// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
    /// </summary>
    public static string? ResolveOnPath(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (Path.IsPathRooted(command))
        {
            return File.Exists(command) ? command : null;
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? ["", ".exe", ".cmd", ".bat"]
            : [""];

        foreach (string directory in
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(directory.Trim(), command + extension);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
