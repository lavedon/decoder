using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

bool plain = false;
string mode = "auto";
string? inputArg = null;
string? filePath = null;
string? outputPath = null;
StringBuilder outputCapture = new();

// Parse arguments
for (int i = 0; i < args.Length; i++)
{
    var arg = args[i].TrimStart('-').ToLowerInvariant();

    if (arg is "help" or "h" or "?")
    {
        PrintUsage();
        return 0;
    }

    if (arg == "plain")
    {
        plain = true;
        continue;
    }

    if (arg is "url" or "base64" or "auto")
    {
        mode = arg;
        continue;
    }

    if (arg is "file" or "f")
    {
        if (i + 1 >= args.Length)
        {
            PrintError("--file requires a path. Example: --file myCapture.txt");
            return 1;
        }
        filePath = args[++i];
        continue;
    }

    if (arg is "output" or "o")
    {
        if (i + 1 >= args.Length)
        {
            PrintError("--output requires a path. Example: --output decoded.txt");
            return 1;
        }
        outputPath = args[++i];
        continue;
    }

    if (!args[i].StartsWith('-'))
    {
        inputArg = args[i];
        continue;
    }

    PrintError($"Unknown argument: {args[i]}");
    PrintUsage();
    return 1;
}

// Determine input source
string? input = inputArg;

if (filePath is not null)
{
    if (!File.Exists(filePath))
    {
        PrintError($"File not found: {filePath}");
        return 1;
    }
    input = File.ReadAllText(filePath).TrimEnd('\r', '\n');
}
else if (input is null && Console.IsInputRedirected)
{
    input = Console.In.ReadToEnd().TrimEnd('\r', '\n');
}

// No input → interactive REPL
if (input is null)
{
    return RunInteractive();
}

if (string.IsNullOrWhiteSpace(input))
{
    PrintError("Input is empty.");
    return 1;
}

Decode(input, mode);
FlushOutput();
return 0;

// ── Core ─────────────────────────────────────────────────────────────

void Decode(string input, string mode)
{
    switch (mode)
    {
        case "url":
            DecodeUrl(input);
            break;
        case "base64":
            DecodeBase64(input);
            break;
        default:
            DecodeAuto(input);
            break;
    }
}

void DecodeUrl(string input)
{
    var decoded = TryUrlDecode(input);
    if (decoded is null)
    {
        PrintWarning("Input does not appear to be URL-encoded (output unchanged).");
        return;
    }

    DisplayDecoded("URL Decoded", decoded);
    var pairs = TryParseFormBody(decoded);
    if (pairs.Count > 0)
        DisplayFormTable(pairs);
}

void DecodeBase64(string input)
{
    var decoded = TryBase64Decode(input);
    if (decoded is null)
    {
        PrintError("Input is not valid Base64.");
        return;
    }

    DisplayDecoded("Base64 Decoded", decoded);
    var pairs = TryParseFormBody(decoded);
    if (pairs.Count > 0)
        DisplayFormTable(pairs);
}

void DecodeAuto(string input)
{
    bool anyDecoded = false;

    if (LooksLikeUrlEncoded(input))
    {
        var decoded = TryUrlDecode(input);
        if (decoded is not null)
        {
            anyDecoded = true;
            DisplayDecoded("URL Decoded", decoded);
            var pairs = TryParseFormBody(decoded);
            if (pairs.Count > 0)
                DisplayFormTable(pairs);
        }
    }

    if (LooksLikeBase64(input))
    {
        var decoded = TryBase64Decode(input);
        if (decoded is not null)
        {
            anyDecoded = true;
            DisplayDecoded("Base64 Decoded", decoded);
            var pairs = TryParseFormBody(decoded);
            if (pairs.Count > 0)
                DisplayFormTable(pairs);
        }
    }

    if (!anyDecoded)
    {
        // Force-try both even without heuristic match
        var urlDecoded = TryUrlDecode(input);
        var b64Decoded = TryBase64Decode(input);

        if (urlDecoded is not null)
        {
            DisplayDecoded("URL Decoded", urlDecoded);
            var pairs = TryParseFormBody(urlDecoded);
            if (pairs.Count > 0)
                DisplayFormTable(pairs);
        }

        if (b64Decoded is not null)
        {
            DisplayDecoded("Base64 Decoded", b64Decoded);
            var pairs = TryParseFormBody(b64Decoded);
            if (pairs.Count > 0)
                DisplayFormTable(pairs);
        }

        if (urlDecoded is null && b64Decoded is null)
            PrintWarning("Could not detect encoding. Input does not appear to be URL-encoded or Base64.");
    }
}

// ── Decode helpers ───────────────────────────────────────────────────

string? TryUrlDecode(string input)
{
    try
    {
        // Replace + with space first (form encoding), then percent-decode
        var decoded = Uri.UnescapeDataString(input.Replace('+', ' '));
        return decoded == input ? null : decoded;
    }
    catch
    {
        return null;
    }
}

string? TryBase64Decode(string input)
{
    try
    {
        // Normalize URL-safe Base64
        var normalized = input.Replace('-', '+').Replace('_', '/');

        // Pad if needed
        var remainder = normalized.Length % 4;
        if (remainder == 2)
            normalized += "==";
        else if (remainder == 3)
            normalized += "=";

        var bytes = Convert.FromBase64String(normalized);
        var decoded = Encoding.UTF8.GetString(bytes);

        // Check for binary content (too many control characters)
        int controlCount = 0;
        foreach (var c in decoded)
        {
            if (c < 0x20 && c is not '\r' and not '\n' and not '\t')
                controlCount++;
        }

        if (decoded.Length > 0 && (double)controlCount / decoded.Length > 0.1)
        {
            PrintWarning($"Base64 decoded to binary data ({bytes.Length} bytes).");
            return null;
        }

        return decoded;
    }
    catch
    {
        return null;
    }
}

List<KeyValuePair<string, string>> TryParseFormBody(string input)
{
    var pairs = new List<KeyValuePair<string, string>>();

    // Must contain at least one = to be form data
    if (!input.Contains('='))
        return pairs;

    var segments = input.Split('&');
    foreach (var segment in segments)
    {
        var eqIndex = segment.IndexOf('=');
        if (eqIndex < 0)
            continue;

        var key = segment[..eqIndex];
        var value = segment[(eqIndex + 1)..];

        try
        {
            key = Uri.UnescapeDataString(key.Replace('+', ' '));
            value = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch
        {
            // Use raw values if decode fails
        }

        pairs.Add(new(key, value));
    }

    return pairs;
}

// ── Detection heuristics ─────────────────────────────────────────────

bool LooksLikeUrlEncoded(string input)
{
    return Patterns.PercentEncoded().IsMatch(input);
}

bool LooksLikeBase64(string input)
{
    return input.Length >= 4 && Patterns.Base64Chars().IsMatch(input);
}

// ── Display ──────────────────────────────────────────────────────────

void DisplayDecoded(string label, string decoded)
{
    if (plain)
    {
        Console.WriteLine($"[{label}]");
        Console.WriteLine(decoded);
        Console.WriteLine();
    }
    else
    {
        var panel = new Panel(Markup.Escape(decoded))
            .Header($"[bold blue]{label}[/]")
            .Border(BoxBorder.Rounded)
            .Padding(1, 0);

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    CaptureOutput($"[{label}]");
    CaptureOutput(decoded);
    CaptureOutput("");
}

void DisplayFormTable(List<KeyValuePair<string, string>> pairs)
{
    if (plain)
    {
        Console.WriteLine("[Form Body]");
        foreach (var kv in pairs)
            Console.WriteLine($"  {kv.Key}\t{kv.Value}");
        Console.WriteLine();
    }
    else
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold blue]Form Body[/]")
            .AddColumn(new TableColumn("[bold]Key[/]"))
            .AddColumn(new TableColumn("[bold]Value[/]"));

        foreach (var kv in pairs)
            table.AddRow($"[green]{Markup.Escape(kv.Key)}[/]", Markup.Escape(kv.Value));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    CaptureOutput("[Form Body]");
    foreach (var kv in pairs)
        CaptureOutput($"  {kv.Key}\t{kv.Value}");
    CaptureOutput("");
}

// ── Interactive ──────────────────────────────────────────────────────

int RunInteractive()
{
    AnsiConsole.MarkupLine("[bold blue]decoder[/] — [dim]interactive mode[/]");
    AnsiConsole.WriteLine();

    while (true)
    {
        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold blue]What would you like to do?[/]")
                .AddChoices("Paste input", "Load from file", "Export last result", "Exit"));

        if (action == "Exit")
            return 0;

        if (action == "Export last result")
        {
            if (outputCapture.Length == 0)
            {
                PrintWarning("Nothing to export yet. Decode something first.");
                AnsiConsole.WriteLine();
                continue;
            }

            var exportPath = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]Export path:[/]"));

            if (!string.IsNullOrWhiteSpace(exportPath))
            {
                File.WriteAllText(exportPath, outputCapture.ToString());
                PrintSuccess($"Exported to {exportPath}");
            }
            AnsiConsole.WriteLine();
            continue;
        }

        string? input = null;

        if (action == "Load from file")
        {
            var path = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]File path:[/]"));

            if (!File.Exists(path))
            {
                PrintError($"File not found: {path}");
                AnsiConsole.WriteLine();
                continue;
            }
            input = File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        else
        {
            input = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold blue]Paste input:[/]")
                    .AllowEmpty());
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            AnsiConsole.WriteLine();
            continue;
        }

        var modeChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Decode mode:[/]")
                .AddChoices("Auto", "URL Decode", "Base64 Decode"));

        var selectedMode = modeChoice switch
        {
            "URL Decode" => "url",
            "Base64 Decode" => "base64",
            _ => "auto"
        };

        outputCapture.Clear();
        AnsiConsole.WriteLine();
        Decode(input, selectedMode);
    }
}

// ── Helpers ──────────────────────────────────────────────────────────

void PrintUsage()
{
    if (plain)
    {
        Console.WriteLine("decoder - Decode URL-encoded strings, Base64, and HTTP form bodies");
        Console.WriteLine();
        Console.WriteLine("Usage: decode [options] [input]");
        Console.WriteLine();
        Console.WriteLine("Modes:");
        Console.WriteLine("  --url          URL/percent-decode only");
        Console.WriteLine("  --base64       Base64 decode only");
        Console.WriteLine("  --auto         Auto-detect (default)");
        Console.WriteLine();
        Console.WriteLine("Input:");
        Console.WriteLine("  --file, -f     Read input from a file");
        Console.WriteLine();
        Console.WriteLine("Output:");
        Console.WriteLine("  --output, -o   Export decoded result to a file");
        Console.WriteLine("  --plain        Plain output (no colors)");
        Console.WriteLine();
        Console.WriteLine("General:");
        Console.WriteLine("  --help, -h     Show this help");
        Console.WriteLine();
        Console.WriteLine("No arguments launches interactive mode.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  decode                              Interactive REPL");
        Console.WriteLine("  decode \"Hello%20World\"              URL decode");
        Console.WriteLine("  decode \"SGVsbG8gV29ybGQ=\"           Base64 decode");
        Console.WriteLine("  decode -f myCapture.txt             Decode from file");
        Console.WriteLine("  decode -f myCapture.log -o out.txt  File in, file out");
        Console.WriteLine("  echo \"dGVzdA==\" | decode            Piped input");
    }
    else
    {
        var panel = new Panel(
            new Rows(
                new Markup("[bold]Modes:[/]"),
                new Markup("  [green]--url[/]          URL/percent-decode only"),
                new Markup("  [green]--base64[/]       Base64 decode only"),
                new Markup("  [green]--auto[/]         Auto-detect (default)"),
                new Markup(""),
                new Markup("[bold]Input:[/]"),
                new Markup("  [green]--file[/], [green]-f[/]     Read input from a file"),
                new Markup(""),
                new Markup("[bold]Output:[/]"),
                new Markup("  [green]--output[/], [green]-o[/]   Export decoded result to a file"),
                new Markup("  [green]--plain[/]        Plain output (no colors)"),
                new Markup(""),
                new Markup("[bold]General:[/]"),
                new Markup("  [green]--help[/], [green]-h[/]     Show this help"),
                new Markup(""),
                new Markup("[dim]No arguments launches interactive mode.[/]"),
                new Markup(""),
                new Markup("[bold]Examples:[/]"),
                new Markup("  [dim]decode \"Hello%20World\"[/]"),
                new Markup("  [dim]decode -f myCapture.txt[/]"),
                new Markup("  [dim]decode -f myCapture.log -o out.txt[/]"),
                new Markup("  [dim]echo \"dGVzdA==\" | decode[/]")
            ))
            .Header("[bold blue]decoder[/] — Decode URL-encoded strings, Base64, and HTTP form bodies")
            .Border(BoxBorder.Rounded)
            .Padding(1, 0);

        AnsiConsole.Write(panel);
    }
}

void CaptureOutput(string line)
{
    outputCapture.AppendLine(line);
}

void FlushOutput()
{
    if (outputPath is not null && outputCapture.Length > 0)
    {
        File.WriteAllText(outputPath, outputCapture.ToString());
        PrintSuccess($"Exported to {outputPath}");
    }
}

void PrintError(string message)
{
    if (plain)
        Console.Error.WriteLine($"ERROR: {message}");
    else
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
}

void PrintWarning(string message)
{
    if (plain)
        Console.Error.WriteLine($"WARNING: {message}");
    else
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");
}

void PrintSuccess(string message)
{
    if (plain)
        Console.WriteLine(message);
    else
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]");
}

// ── AOT-compatible regex ─────────────────────────────────────────────

static partial class Patterns
{
    [GeneratedRegex(@"^[A-Za-z0-9+/=_-]+$")]
    public static partial Regex Base64Chars();

    [GeneratedRegex(@"%[0-9A-Fa-f]{2}")]
    public static partial Regex PercentEncoded();
}
