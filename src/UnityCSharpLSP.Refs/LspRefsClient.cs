using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace UnityCSharpLSP.Refs;

internal sealed class LspRefsClient : IDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly StringBuilder _stderr = new();
    private readonly object _stderrLock = new();
    private int _nextId;

    public LspRefsClient(string serverPath, string workspace)
    {
        var startInfo = new ProcessStartInfo(serverPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workspace,
        };
        startInfo.ArgumentList.Add("--stdio");

        _process = Process.Start(startInfo)
                   ?? throw new IOException($"Failed to start LSP server: {serverPath}");
        _stdin = _process.StandardInput.BaseStream;
        _stdout = _process.StandardOutput.BaseStream;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderrLock) _stderr.AppendLine(e.Data);
        };
        _process.BeginErrorReadLine();
    }

    public string GetStderr()
    {
        lock (_stderrLock) return _stderr.ToString();
    }

    public async Task InitializeAsync(string workspace, CancellationToken ct)
    {
        string uri = ToUri(workspace);
        await RequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            rootUri = uri,
            capabilities = new { },
            workspaceFolders = new[]
            {
                new { uri, name = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace)) },
            },
        }, ct).ConfigureAwait(false);

        Notify("initialized", new { });
    }

    public void DidOpen(string filePath)
    {
        Notify("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri = ToUri(filePath),
                languageId = "csharp",
                version = 1,
                text = File.ReadAllText(filePath),
            },
        });
    }

    public void DidClose(string filePath)
    {
        Notify("textDocument/didClose", new
        {
            textDocument = new { uri = ToUri(filePath) },
        });
    }

    public async Task<List<(string Path, int Line)>> ReferencesAsync(
        string filePath,
        int line,
        int character,
        CancellationToken ct)
    {
        JsonElement result = await RequestAsync("textDocument/references", new
        {
            textDocument = new { uri = ToUri(filePath) },
            position = new { line, character },
            context = new { includeDeclaration = true },
        }, ct).ConfigureAwait(false);

        var locations = new List<(string, int)>();
        if (result.ValueKind != JsonValueKind.Array) return locations;

        foreach (JsonElement location in result.EnumerateArray())
        {
            if (!location.TryGetProperty("uri", out JsonElement uriElement)) continue;

            int lineNumber = 0;
            if (location.TryGetProperty("range", out JsonElement range) &&
                range.TryGetProperty("start", out JsonElement start) &&
                start.TryGetProperty("line", out JsonElement lineElement) &&
                lineElement.ValueKind == JsonValueKind.Number)
            {
                lineNumber = lineElement.GetInt32();
            }

            locations.Add((ToLocalPath(uriElement.GetString()), lineNumber));
        }

        return locations;
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        try
        {
            await RequestAsync("shutdown", null, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best effort: the server may already be gone.
        }

        try
        {
            Notify("exit", null);
        }
        catch
        {
            // Best effort: the server may already be gone.
        }
    }

    private async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken ct)
    {
        int id = ++_nextId;
        WriteMessage(new { jsonrpc = "2.0", id, method, @params = parameters });

        while (true)
        {
            using JsonDocument document = await ReadMessageAsync(ct).ConfigureAwait(false);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("method", out _) && root.TryGetProperty("id", out JsonElement requestId))
            {
                WriteMessage(new { jsonrpc = "2.0", id = requestId, result = (object?)null });
                continue;
            }

            if (!root.TryGetProperty("id", out JsonElement responseId) ||
                responseId.ValueKind != JsonValueKind.Number ||
                responseId.GetInt32() != id)
            {
                continue;
            }

            if (root.TryGetProperty("error", out JsonElement error))
            {
                throw new IOException($"LSP {method} failed: {error.GetRawText()}");
            }

            return root.TryGetProperty("result", out JsonElement result)
                ? result.Clone()
                : default;
        }
    }

    private void Notify(string method, object? parameters)
        => WriteMessage(new { jsonrpc = "2.0", method, @params = parameters });

    private void WriteMessage<T>(T message)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        _stdin.Write(header);
        _stdin.Write(body);
        _stdin.Flush();
    }

    private async Task<JsonDocument> ReadMessageAsync(CancellationToken ct)
    {
        int contentLength = -1;
        while (true)
        {
            string? line = await ReadLineAsync(ct).ConfigureAwait(false)
                           ?? throw new IOException("LSP server closed stdout.");
            if (line.Length == 0) break;

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(line["Content-Length:".Length..].Trim());
            }
        }

        if (contentLength <= 0)
        {
            throw new IOException("LSP response is missing a valid Content-Length header.");
        }

        var body = new byte[contentLength];
        int offset = 0;
        while (offset < body.Length)
        {
            int read = await _stdout.ReadAsync(body.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("LSP server closed stdout mid-message.");
            offset += read;
        }

        return JsonDocument.Parse(body);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int read = await _stdout.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            if (one[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
            bytes.Add(one[0]);
        }
    }

    public static string ToUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private static string ToLocalPath(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return string.Empty;
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile
            ? parsed.LocalPath
            : uri;
    }

    public void Dispose()
    {
        try { _stdin.Dispose(); } catch { }
        try { _stdout.Dispose(); } catch { }

        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }

        try { _process.Dispose(); } catch { }
    }
}
