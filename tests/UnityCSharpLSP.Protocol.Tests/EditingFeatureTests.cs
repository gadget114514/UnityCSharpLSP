using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using Xunit;
using LspDiagnostic = Microsoft.VisualStudio.LanguageServer.Protocol.Diagnostic;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;
using LspFormattingOptions = Microsoft.VisualStudio.LanguageServer.Protocol.FormattingOptions;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class EditingFeatureTests : IClassFixture<EditingFeatureTests.EditingSession>
{
    private readonly EditingSession _session;

    public EditingFeatureTests(EditingSession session)
    {
        _session = session;
    }

    [Fact]
    public async Task Formatting_AndRangeFormatting_ReturnMinimalEdits()
    {
        const string source = "namespace Game.Data { public class Fmt { public int Value{get;set;} } }";
        var (uri, _, _) = await _session.AddSourceAsync("FormattingTest.cs", source);

        var edits = await _session.RequestAsync<TextEdit[]>(
            Methods.TextDocumentFormattingName,
            new DocumentFormattingParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Options = new LspFormattingOptions { TabSize = 4, InsertSpaces = true },
            });

        Assert.NotNull(edits);
        Assert.NotEmpty(edits);
        Assert.Contains("public int Value { get; set; }", ApplyTextEdits(source, edits));

        const string rangeSource = "namespace Game.Data\n{\n    class RangeFmt\n    {\n        public int A=1;\n        public int B=2;\n    }\n}\n";
        var (rangeUri, _, _) = await _session.AddSourceAsync("RangeFormattingTest.cs", rangeSource);
        var rangeEdits = await _session.RequestAsync<TextEdit[]>(
            Methods.TextDocumentRangeFormattingName,
            new DocumentRangeFormattingParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = rangeUri },
                Range = new LspRange { Start = new Position(4, 0), End = new Position(4, 22) },
                Options = new LspFormattingOptions { TabSize = 4, InsertSpaces = true },
            });

        Assert.NotNull(rangeEdits);
        Assert.NotEmpty(rangeEdits);
        var rangeResult = ApplyTextEdits(rangeSource, rangeEdits);
        Assert.Contains("public int A = 1;", rangeResult);
        Assert.Contains("public int B=2;", rangeResult);

        const string onTypeSource = "namespace Game.Data\n{\n    class OnTypeFmt\n    {\n        public int C=3;\n    }\n}\n";
        var (onTypeUri, _, _) = await _session.AddSourceAsync("OnTypeFormattingTest.cs", onTypeSource);
        var onTypeEdits = await _session.RequestAsync<TextEdit[]>(
            Methods.TextDocumentOnTypeFormattingName,
            new DocumentOnTypeFormattingParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = onTypeUri },
                Position = new Position(4, "        public int C=3;".Length),
                Character = ";",
                Options = new LspFormattingOptions { TabSize = 4, InsertSpaces = true },
            });

        Assert.NotNull(onTypeEdits);
        Assert.NotEmpty(onTypeEdits);
        Assert.Contains("public int C = 3;", ApplyTextEdits(onTypeSource, onTypeEdits));
    }

    [Fact]
    public async Task CodeAction_OffersAddUsingAndPragmaSuppress_AndResolveIsNoOp()
    {
        const string source = """
            namespace Game.Data.Editing
            {
                public class ActionHelper { }
            }

            namespace Game.Runtime
            {
                public class ActionConsumer
                {
                    ActionHelper field;
                }
            }
            """;
        var (uri, _, _) = await _session.AddSourceAsync("CodeActionTest.cs", source);
        var diagnostic = new LspDiagnostic
        {
            Code = "CS0246",
            Message = "The type or namespace name 'ActionHelper' could not be found (are you missing a using directive or an assembly reference?)",
            Severity = DiagnosticSeverity.Error,
            Range = new LspRange { Start = new Position(8, 8), End = new Position(8, 19) },
        };

        var actions = await _session.RequestAsync<CodeAction[]>(
            Methods.TextDocumentCodeActionName,
            new CodeActionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Range = diagnostic.Range,
                Context = new CodeActionContext { Diagnostics = [diagnostic] },
            });

        Assert.NotNull(actions);
        var addUsing = Assert.Single(actions, action => action.Title == "Add using Game.Data.Editing");
        Assert.Equal(CodeActionKind.QuickFix, addUsing.Kind);
        var edit = Assert.Single(addUsing.Edit!.Changes!);
        Assert.Contains("using Game.Data.Editing;", edit.Value[0].NewText);

        var resolved = await _session.RequestAsync<CodeAction>(Methods.CodeActionResolveName, addUsing);
        Assert.NotNull(resolved);
        Assert.Equal(addUsing.Title, resolved.Title);

        var suppress = Assert.Single(actions, action => action.Title.Contains("Suppress CS0246", StringComparison.Ordinal));
        Assert.Contains(suppress.Edit!.Changes![uri.AbsoluteUri], e => e.NewText.Contains("#pragma warning disable CS0246"));
        Assert.Contains(suppress.Edit.Changes[uri.AbsoluteUri], e => e.NewText.Contains("#pragma warning restore CS0246"));
    }

    [Fact]
    public async Task SemanticTokens_FullAndRange_ReturnEncodedTokens()
    {
        const string source = "namespace Game.Data { public class TokenSample { public int Count; } }";
        var (uri, _, _) = await _session.AddSourceAsync("SemanticTokensTest.cs", source);

        var tokens = await _session.RequestAsync<SemanticTokens>(
            Methods.TextDocumentSemanticTokensFullName,
            new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } });

        Assert.NotNull(tokens);
        Assert.NotEmpty(tokens.Data);
        Assert.Equal(0, tokens.Data.Length % 5);
        var decoded = Decode(tokens.Data);
        Assert.Contains(decoded, token => token.Type == 0);
        Assert.Contains(decoded, token => token.Type == 2 && token.Length == "TokenSample".Length);
        Assert.Contains(decoded, token => token.Type == 15);

        var rangeTokens = await _session.RequestAsync<SemanticTokens>(
            Methods.TextDocumentSemanticTokensRangeName,
            new SemanticTokensRangeParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Range = new LspRange { Start = new Position(0, 0), End = new Position(0, 30) },
            });

        Assert.NotNull(rangeTokens);
        Assert.All(Decode(rangeTokens.Data), token => Assert.Equal(0, token.Line));
    }

    [Fact]
    public async Task InlayHint_ReturnsTypeAndParameterHints()
    {
        const string source = """
            namespace Game.Data
            {
                public class HintSample
                {
                    public string Format(string label, int value) => label + value;
                    public void Run()
                    {
                        var count = 42;
                        Format("first", 1);
                    }
                }
            }
            """;
        var (uri, _, _) = await _session.AddSourceAsync("InlayHintTest.cs", source);

        var hints = await _session.RequestAsync<InlayHint[]>(
            TextDocumentMethods.InlayHintName,
            new InlayHintParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Range = new LspRange { Start = new Position(0, 0), End = new Position(11, 0) },
            });

        Assert.NotNull(hints);
        Assert.Contains(hints, hint => hint.Label == ": int" && hint.Kind == InlayHintKind.Type && hint.PaddingLeft == true);
        Assert.Contains(hints, hint => hint.Label == "label:" && hint.Kind == InlayHintKind.Parameter && hint.PaddingRight == true);
        Assert.Contains(hints, hint => hint.Label == "value:" && hint.Kind == InlayHintKind.Parameter);
    }

    [Fact]
    public async Task CodeLens_AndResolve_ReturnReferenceCounts()
    {
        const string source = """
            namespace Game.Data
            {
                public class LensSample
                {
                    public void Ping() { }
                    public void Run() { Ping(); Ping(); }
                }
            }
            """;
        var (uri, _, _) = await _session.AddSourceAsync("CodeLensTest.cs", source);

        var lenses = await _session.RequestAsync<CodeLens[]>(
            Methods.TextDocumentCodeLensName,
            new CodeLensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } });

        Assert.NotNull(lenses);
        var pingLens = Assert.Single(lenses, lens => lens.Range.Start.Line == 4);
        Assert.NotNull(pingLens.Command);
        Assert.Equal("2 references", pingLens.Command!.Title);

        var resolved = await _session.RequestAsync<CodeLens>(Methods.CodeLensResolveName, pingLens);
        Assert.NotNull(resolved);
        Assert.Equal("2 references", resolved.Command!.Title);
    }

    [Fact]
    public async Task DocumentLink_ReturnsLinksForPathsInCommentsAndStrings()
    {
        const string source = """
            namespace Game.Data
            {
                // See ./notes.txt for details: "./notes.txt"
                public class LinkSample { public string Path => "./notes.txt"; }
            }
            """;
        var (uri, _, _) = await _session.AddSourceAsync("DocumentLinkTest.cs", source);
        await _session.WriteSiblingFileAsync("notes.txt", "notes");

        var links = await _session.RequestAsync<DocumentLink[]>(
            Methods.TextDocumentDocumentLinkName,
            new DocumentLinkParams { TextDocument = new TextDocumentIdentifier { Uri = uri } });

        Assert.NotNull(links);
        Assert.True(links.Length >= 2, $"Expected at least two links, got {links.Length}.");
        Assert.All(links, link => Assert.Equal(
            Path.GetFullPath(Path.Combine(_session.TestDirectory, "notes.txt")),
            link.Target!.LocalPath));
    }

    [Fact]
    public async Task LinkedEditingRange_LinksPreprocessorSymbolsAndRegionLabels()
    {
        const string source = """
            namespace Game.Data
            {
                class LinkSample
                {
            #region TargetRegion
                void A() { }
            #endregion TargetRegion
            #if FEATURE_FLAG
                void B() { }
            #elif FEATURE_FLAG
                void C() { }
            #endif
                }
            }
            """;
        var (uri, _, _) = await _session.AddSourceAsync("LinkedEditingTest.cs", source);

        var regionRanges = await _session.RequestAsync<LinkedEditingRanges>(
            Methods.TextDocumentLinkedEditingRangeName,
            new LinkedEditingRangeParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(4, "#region ".Length),
            });

        Assert.NotNull(regionRanges);
        Assert.Equal(2, regionRanges.Ranges.Length);
        Assert.Contains(regionRanges.Ranges, range => range.Start.Line == 4);
        Assert.Contains(regionRanges.Ranges, range => range.Start.Line == 6);

        var directiveRanges = await _session.RequestAsync<LinkedEditingRanges>(
            Methods.TextDocumentLinkedEditingRangeName,
            new LinkedEditingRangeParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(7, "#if ".Length),
            });

        Assert.NotNull(directiveRanges);
        Assert.Equal(2, directiveRanges.Ranges.Length);
        Assert.Contains(directiveRanges.Ranges, range => range.Start.Line == 7);
        Assert.Contains(directiveRanges.Ranges, range => range.Start.Line == 9);
    }

    [Fact]
    public async Task InlineValue_ReturnsConstantInitializerValues()
    {
        const string source = """
            namespace Game.Data
            {
                class InlineSample
                {
                    const int MaxCount = 10;
                    void Run()
                    {
                        const string Name = "hi";
                        int local = 1 + 2;
                    }
                }
            }
            """;
        var (uri, _, _) = await _session.AddSourceAsync("InlineValueTest.cs", source);

        var values = await _session.RequestAsync<InlineValueText[]>(
            TextDocumentMethods.InlineValueName,
            new InlineValueParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Range = new LspRange { Start = new Position(3, 0), End = new Position(9, 0) },
            });

        Assert.NotNull(values);
        Assert.Contains(values, value => value.Text == "= 10");
        Assert.Contains(values, value => value.Text == "= \"hi\"");
        Assert.Contains(values, value => value.Text == "= 3");
    }

    [Fact]
    public async Task PullDiagnostics_ReturnsFullReportThenUnchangedThenUpdated()
    {
        const string source = """
            namespace Game.Data
            {
                class PullSample
                {
                    MissingType field;
                }
            }
            """;
        var (uri, _, _) = await _session.AddSourceAsync("PullDiagnosticsTest.cs", source);
        var textDocument = new TextDocumentIdentifier { Uri = uri };

        var first = await _session.RequestAsync<DocumentDiagnosticReport>(
            DiagnosticMethods.Name,
            new DocumentDiagnosticParams { TextDocument = textDocument });

        Assert.NotNull(first);
        Assert.Equal("full", first.Kind);
        Assert.False(string.IsNullOrEmpty(first.ResultId));
        Assert.Contains(first.Diagnostics!, d => GetCode(d) == "CS0246");

        var unchanged = await _session.RequestAsync<DocumentDiagnosticReport>(
            DiagnosticMethods.Name,
            new DocumentDiagnosticParams
            {
                TextDocument = textDocument,
                PreviousResultId = first.ResultId,
            });

        Assert.NotNull(unchanged);
        Assert.Equal("unchanged", unchanged.Kind);
        Assert.Equal(first.ResultId, unchanged.ResultId);

        const string fixedSource = """
            namespace Game.Data
            {
                class PullSample
                {
                    int field;
                }
            }
            """;
        await _session.Client.NotifyWithParameterObjectAsync(
            Methods.TextDocumentDidChangeName,
            new DidChangeTextDocumentParams
            {
                TextDocument = new VersionedTextDocumentIdentifier { Uri = uri, Version = 2 },
                ContentChanges = [new TextDocumentContentChangeEvent { Text = fixedSource }],
            });

        var updated = await _session.RequestAsync<DocumentDiagnosticReport>(
            DiagnosticMethods.Name,
            new DocumentDiagnosticParams
            {
                TextDocument = textDocument,
                PreviousResultId = first.ResultId,
            });

        Assert.NotNull(updated);
        Assert.Equal("full", updated.Kind);
        Assert.NotEqual(first.ResultId, updated.ResultId);
        Assert.DoesNotContain(updated.Diagnostics!, d => GetCode(d) == "CS0246");
    }

    [Fact]
    public async Task Initialize_AdvertisesAllEditingCapabilities()
    {
        var result = await _session.RequestAsync<InitializeResult>(
            Methods.InitializeName,
            new InitializeParams { Capabilities = new ClientCapabilities() });

        Assert.NotNull(result);
        Assert.Equal(true, result.Capabilities.DocumentFormattingProvider);
        Assert.Equal(true, result.Capabilities.DocumentRangeFormattingProvider);
        Assert.NotNull(result.Capabilities.DocumentOnTypeFormattingProvider);
        Assert.Equal("}", result.Capabilities.DocumentOnTypeFormattingProvider!.FirstTriggerCharacter);
        Assert.NotNull(result.Capabilities.CodeActionProvider);
        Assert.NotNull(result.Capabilities.SemanticTokensOptions);
        Assert.NotNull(result.Capabilities.CodeLensProvider);
        Assert.NotNull(result.Capabilities.DocumentLinkProvider);
        Assert.Equal(true, result.Capabilities.LinkedEditingRangeProvider);
    }

    private static string? GetCode(LspDiagnostic diagnostic)
    {
        var code = diagnostic.Code;
        return code?.Second ?? code?.First.ToString();
    }

    private static string ApplyTextEdits(string text, IEnumerable<TextEdit> edits)
    {
        var lines = text.Split('\n');
        var lineStarts = new int[lines.Length];
        var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            lineStarts[i] = offset;
            offset += lines[i].Length + 1;
        }

        int ToOffset(Position position) => lineStarts[position.Line] + position.Character;

        var result = text;
        foreach (var edit in edits.OrderByDescending(edit => ToOffset(edit.Range.Start)))
        {
            var start = ToOffset(edit.Range.Start);
            var end = ToOffset(edit.Range.End);
            result = string.Concat(result.AsSpan(0, start), edit.NewText, result.AsSpan(end));
        }

        return result;
    }

    private static (int Line, int Start, int Length, int Type, int Modifiers)[] Decode(int[] data)
    {
        var result = new List<(int, int, int, int, int)>();
        var line = 0;
        var character = 0;
        for (var i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            result.Add((line, character, data[i + 2], data[i + 3], data[i + 4]));
        }

        return result.ToArray();
    }

    public sealed class EditingSession : IAsyncLifetime
    {
        private LspJsonRpcSession? _server;
        private JsonRpc? _client;

        public JsonRpc Client => _client ?? throw new InvalidOperationException("Session is not initialized.");
        public string DummyPath { get; private set; } = string.Empty;
        public string TestDirectory { get; private set; } = string.Empty;

        public Task<T?> RequestAsync<T>(string method, object parameters)
        {
            return Client.InvokeWithParameterObjectAsync<T>(method, parameters);
        }

        public async Task<(Uri Uri, string Path, string Source)> AddSourceAsync(string fileName, string source)
        {
            var path = Path.Combine(TestDirectory, fileName);
            await WriteSiblingFileAsync(fileName, source);
            var documentId = _server!.WorkspaceManager!.AddOrRefreshDiskFile(path);
            Assert.NotNull(documentId);
            return (new Uri(path), path, source);
        }

        public Task WriteSiblingFileAsync(string fileName, string content)
        {
            var path = Path.Combine(TestDirectory, fileName);
            File.WriteAllText(path, content);
            return Task.CompletedTask;
        }

        public async Task InitializeAsync()
        {
            DummyPath = GetDummyProjectPath();
            TestDirectory = Path.Combine(Path.GetTempPath(), "UnityCSharpLSP.EditingTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TestDirectory);

            var (clientStream, serverStream) = FullDuplexStream.CreatePair();
            _server = new LspJsonRpcSession(serverStream, serverStream);
            _server.Start();

            var clientHandler = new HeaderDelimitedMessageHandler(
                clientStream,
                clientStream,
                new JsonMessageFormatter());
            _client = new JsonRpc(clientHandler);
            _client.StartListening();

            await _client.InvokeWithParameterObjectAsync<InitializeResult>(
                Methods.InitializeName,
                new InitializeParams
                {
                    RootUri = new Uri(DummyPath),
                    Capabilities = new ClientCapabilities(),
                });
        }

        public async Task DisposeAsync()
        {
            try
            {
                try
                {
                    if (_client is not null && _server is not null)
                    {
                        await _client.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, new object());
                        await _client.NotifyAsync(Methods.ExitName);
                        await _server.WaitForExitAsync();
                    }
                }
                finally
                {
                    _client?.Dispose();
                    _server?.Dispose();
                }
            }
            finally
            {
                for (var i = 0; i < 5 && Directory.Exists(TestDirectory); i++)
                {
                    try
                    {
                        Directory.Delete(TestDirectory, recursive: true);
                        break;
                    }
                    catch (IOException)
                    {
                        await Task.Delay(50);
                    }
                }
            }
        }

        private static string GetDummyProjectPath()
        {
            var directory = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(directory))
            {
                var dummyPath = Path.Combine(directory, "DummyProject");
                if (Directory.Exists(dummyPath))
                {
                    return dummyPath;
                }

                directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
            }

            throw new DirectoryNotFoundException("Could not find DummyProject folder.");
        }
    }
}
