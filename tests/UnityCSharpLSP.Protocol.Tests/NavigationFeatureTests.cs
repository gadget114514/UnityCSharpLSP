using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using Xunit;
using LspLocation = Microsoft.VisualStudio.LanguageServer.Protocol.Location;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;
using ModelSelectionRange = UnityCSharpLSP.Protocol.Models.SelectionRange;

namespace UnityCSharpLSP.Protocol.Tests;

public sealed class NavigationFeatureTests : IClassFixture<NavigationFeatureTests.ProtocolSession>
{
    private readonly ProtocolSession _session;

    public NavigationFeatureTests(ProtocolSession session)
    {
        _session = session;
    }

    [Fact]
    public async Task CompletionResolve_ReturnsDetailAndXmlDocumentation()
    {
        const string memberAccess = "instance.Format(\"completion\", 1)";
        var items = await _session.RequestAsync<CompletionItem[]>(
            Methods.TextDocumentCompletionName,
            new CompletionParams
            {
                TextDocument = _session.TextDocument,
                Position = _session.PositionOf(memberAccess, offset: memberAccess.IndexOf('.') + 1),
            });

        Assert.NotNull(items);
        var item = Assert.Single(items, candidate => candidate.Label == "Format");
        Assert.NotNull(item.Data);

        var resolved = await _session.RequestAsync<CompletionItem>(
            Methods.TextDocumentCompletionResolveName,
            item);

        Assert.NotNull(resolved);
        Assert.False(string.IsNullOrWhiteSpace(resolved.Detail));
        Assert.Contains("NavigationBase.Format", resolved.Detail);
        var documentation = resolved.Documentation?.Value switch
        {
            MarkupContent markup => markup,
            JObject json => json.ToObject<MarkupContent>(),
            _ => null,
        };
        Assert.NotNull(documentation);
        Assert.Equal(MarkupKind.Markdown, documentation.Kind);
        Assert.Contains("Formats a value with its label.", documentation.Value);
    }

    [Fact]
    public async Task TypeDefinitionAndDeclaration_NavigateToGeneratedSymbols()
    {
        const string fieldReference = "_field.Counter = typed.Kind";
        var fieldPosition = _session.PositionOf(fieldReference);

        var typeDefinitions = await _session.RequestAsync<LspLocation[]>(
            Methods.TextDocumentTypeDefinitionName,
            new TextDocumentPositionParams
            {
                TextDocument = _session.TextDocument,
                Position = fieldPosition,
            });

        Assert.NotNull(typeDefinitions);
        var typeDefinition = Assert.Single(typeDefinitions);
        AssertGeneratedLocation(
            typeDefinition,
            _session.PositionOf("public class NavigationBase", offset: "public class ".Length));

        var declarations = await _session.RequestAsync<LspLocation[]>(
            TextDocumentMethods.DeclarationName,
            new TextDocumentPositionParams
            {
                TextDocument = _session.TextDocument,
                Position = fieldPosition,
            });

        Assert.NotNull(declarations);
        var declaration = Assert.Single(declarations);
        AssertGeneratedLocation(
            declaration,
            _session.PositionOf("private NavigationBase _field", offset: "private NavigationBase ".Length));
    }

    [Fact]
    public async Task DocumentHighlight_ReturnsDeclarationAndRepeatedReferences()
    {
        var expectedPositions = _session.AllPositions("Counter");
        Assert.Equal(3, expectedPositions.Length);

        var highlights = await _session.RequestAsync<DocumentHighlight[]>(
            Methods.TextDocumentDocumentHighlightName,
            new DocumentHighlightParams
            {
                TextDocument = _session.TextDocument,
                Position = expectedPositions[0],
            });

        Assert.NotNull(highlights);
        Assert.Equal(expectedPositions.Length, highlights.Length);
        Assert.All(highlights, highlight =>
        {
            Assert.Equal(DocumentHighlightKind.Text, highlight.Kind);
            Assert.Equal(_session.Uri.LocalPath, highlight.Range.Start.Line >= 0 ? _session.Uri.LocalPath : string.Empty);
        });

        var expectedRanges = expectedPositions
            .Select(position => (
                position.Line,
                position.Character,
                position.Line,
                position.Character + "Counter".Length))
            .OrderBy(range => range)
            .ToArray();
        var actualRanges = highlights
            .Select(highlight => RangeKey(highlight.Range))
            .OrderBy(range => range)
            .ToArray();
        Assert.Equal(expectedRanges, actualRanges);
    }

    [Fact]
    public async Task FoldingRange_ReturnsSyntaxCommentDirectiveAndUsingFolds()
    {
        var ranges = await _session.RequestAsync<FoldingRange[]>(
            Methods.TextDocumentFoldingRangeName,
            new FoldingRangeParams { TextDocument = _session.TextDocument });

        Assert.NotNull(ranges);
        Assert.Contains(ranges, range =>
            range.Kind == FoldingRangeKind.Imports &&
            range.StartLine == _session.PositionOf("using System;").Line &&
            range.EndLine == _session.PositionOf("using System.Linq;").Line);
        Assert.Contains(ranges, range =>
            range.Kind == FoldingRangeKind.Comment &&
            range.StartLine == _session.PositionOf("/*").Line &&
            range.EndLine == _session.PositionOf("*/").Line);
        Assert.Contains(ranges, range =>
            range.Kind == FoldingRangeKind.Comment &&
            range.StartLine == _session.PositionOf("// First consecutive comment.").Line &&
            range.EndLine == _session.PositionOf("// Second consecutive comment.").Line);
        Assert.Contains(ranges, range =>
            range.Kind == FoldingRangeKind.Region &&
            range.StartLine == _session.PositionOf("#region Invocation Helpers").Line &&
            range.EndLine == _session.PositionOf("#endregion").Line);
        Assert.Contains(ranges, range =>
            range.Kind == FoldingRangeKind.Region &&
            range.StartLine == _session.PositionOf("#if DEBUG").Line &&
            range.EndLine == _session.PositionOf("#endif").Line);

        var methodPosition = _session.PositionOf("public string Format(string label, int value)");
        Assert.Contains(ranges, range =>
            range.Kind is null &&
            range.StartLine == methodPosition.Line &&
            range.EndLine > methodPosition.Line);
    }

    [Fact]
    public async Task SelectionRange_ReturnsNestedRangesForEveryRequestedPosition()
    {
        const string fieldReference = "_field.Counter = typed.Kind";
        const string returnStatement = "return label + \": \" + value;";
        var positions = new[]
        {
            _session.PositionOf(fieldReference, offset: fieldReference.IndexOf("Counter", StringComparison.Ordinal)),
            _session.PositionOf(returnStatement, offset: returnStatement.IndexOf("value", StringComparison.Ordinal)),
        };

        var selections = await _session.RequestAsync<ModelSelectionRange[]>(
            TextDocumentMethods.SelectionRangeName,
            new SelectionRangeParams
            {
                TextDocument = _session.TextDocument,
                Positions = positions,
            });

        Assert.NotNull(selections);
        Assert.Equal(positions.Length, selections.Length);
        Assert.All(selections, selection =>
        {
            Assert.NotNull(selection.Parent);
            var child = selection;
            var parentCount = 0;
            while (child.Parent is not null)
            {
                var parent = child.Parent;
                AssertRangeContains(parent.Range, child.Range);
                child = parent;
                parentCount++;
                Assert.True(parentCount < 32, "Selection range parent chain is unexpectedly deep.");
            }

            Assert.True(parentCount >= 2, "Expected syntax ancestry ending at the compilation unit.");
            Assert.Equal(0, child.Range.Start.Line);
            Assert.Equal(0, child.Range.Start.Character);
        });
    }

    [Theory]
    [InlineData(
        "_field.Format(\"first\", current)",
        "string label",
        "int value",
        "Formats a value with its label.",
        "The numeric value.")]
    [InlineData(
        "new NavigationEntity(\"first\", 1)",
        "string name",
        "int kind",
        "Creates an entity.",
        "The entity kind.")]
    [InlineData(
        "[Navigation(\"navigation\", 7)]",
        "string category",
        "int priority",
        "Configures navigation metadata.",
        "The navigation priority.")]
    public async Task SignatureHelp_InvocationObjectCreationAndAttribute_ReturnDocumentedSignatures(
        string invocation,
        string firstParameter,
        string secondParameter,
        string summary,
        string secondParameterDocumentation)
    {
        var commaOffset = invocation.IndexOf(',') + 1;
        var help = await _session.RequestAsync<SignatureHelp>(
            Methods.TextDocumentSignatureHelpName,
            new SignatureHelpParams
            {
                TextDocument = _session.TextDocument,
                Position = _session.PositionOf(invocation, offset: commaOffset),
            });

        Assert.NotNull(help);
        Assert.NotEmpty(help.Signatures);
        Assert.Equal(1, help.ActiveParameter);
        var signature = help.Signatures[help.ActiveSignature ?? 0];
        Assert.Equal(summary, signature.Documentation);
        Assert.Equal(2, signature.Parameters.Length);
        Assert.Equal(firstParameter, signature.Parameters[0].Label);
        Assert.Equal(secondParameter, signature.Parameters[1].Label);
        Assert.Equal(secondParameterDocumentation, signature.Parameters[1].Documentation);
    }

    [Fact]
    public async Task PrepareRenameAndRename_ReturnTokenRangeAndMinimalWorkspaceEdits()
    {
        const string fieldReference = "_field.Counter = typed.Kind";
        const string newName = "RenamedCounter";
        var referencePosition = _session.PositionOf(
            fieldReference,
            offset: fieldReference.IndexOf("Counter", StringComparison.Ordinal));

        var prepared = await _session.RequestAsync<PrepareRenameResult>(
            RenameMethods.PrepareName,
            new TextDocumentPositionParams
            {
                TextDocument = _session.TextDocument,
                Position = referencePosition,
            });

        Assert.NotNull(prepared);
        Assert.Equal("Counter", prepared.Placeholder);
        Assert.Equal(
            (referencePosition.Line, referencePosition.Character,
                referencePosition.Line, referencePosition.Character + "Counter".Length),
            RangeKey(prepared.Range));

        var workspaceEdit = await _session.RequestAsync<WorkspaceEdit>(
            Methods.TextDocumentRenameName,
            new RenameParams
            {
                TextDocument = _session.TextDocument,
                Position = referencePosition,
                NewName = newName,
            });

        Assert.NotNull(workspaceEdit);
        Assert.NotNull(workspaceEdit.Changes);
        var fileEdit = Assert.Single(workspaceEdit.Changes);
        Assert.Equal(_session.Uri.AbsoluteUri, fileEdit.Key);

        var applied = ApplyTextEdits(GeneratedSource, fileEdit.Value);
        Assert.Equal(GeneratedSource.Replace("Counter", newName, StringComparison.Ordinal), applied);

        var counterPositions = _session.AllPositions("Counter");
        Assert.Equal(counterPositions.Length, fileEdit.Value.Length);
        Assert.All(fileEdit.Value, edit => Assert.Contains(
            counterPositions,
            position => position.Line == edit.Range.Start.Line &&
                        edit.Range.Start.Character >= position.Character &&
                        edit.Range.Start.Character <= position.Character + "Counter".Length));
    }

    [Fact]
    public async Task TypeHierarchy_PrepareSupertypesAndSubtypes_TraverseGeneratedHierarchy()
    {
        var prepared = await _session.RequestAsync<TypeHierarchyItem[]>(
            TypeHierarchyMethods.PrepareName,
            new TextDocumentPositionParams
            {
                TextDocument = _session.TextDocument,
                Position = _session.PositionOf(
                    "public class NavigationEntity",
                    offset: "public class ".Length),
            });

        Assert.NotNull(prepared);
        var entity = Assert.Single(prepared);
        Assert.Equal("NavigationEntity", entity.Name);
        Assert.Equal(SymbolKind.Class, entity.Kind);
        Assert.False(string.IsNullOrWhiteSpace(entity.Data?.Id));

        var supertypes = await _session.RequestAsync<TypeHierarchyItem[]>(
            TypeHierarchyMethods.SupertypesName,
            new TypeHierarchySupertypesParams { Item = entity });

        Assert.NotNull(supertypes);
        Assert.Contains(supertypes, item => item.Name == "INavigationEntity" && item.Kind == SymbolKind.Interface);
        var baseType = Assert.Single(supertypes, item => item.Name == "NavigationBase");
        AssertGeneratedUri(baseType.Uri);

        var subtypes = await _session.RequestAsync<TypeHierarchyItem[]>(
            TypeHierarchyMethods.SubtypesName,
            new TypeHierarchySubtypesParams { Item = baseType });

        Assert.NotNull(subtypes);
        var derived = Assert.Single(subtypes, item => item.Name == "NavigationEntity");
        Assert.Equal(SymbolKind.Class, derived.Kind);
        AssertGeneratedUri(derived.Uri);
    }

    [Fact]
    public async Task Moniker_ReturnsStableCSharpIdentifier()
    {
        var monikers = await _session.RequestAsync<Moniker[]>(
            MonikerMethods.TextDocumentMonikerName,
            new TextDocumentPositionParams
            {
                TextDocument = _session.TextDocument,
                Position = _session.PositionOf(
                    "public class NavigationBase",
                    offset: "public class ".Length),
            });

        Assert.NotNull(monikers);
        var moniker = Assert.Single(monikers);
        Assert.Equal("csharp", moniker.Scheme);
        Assert.Equal("T:Game.Data.Navigation.NavigationBase", moniker.Identifier);
        Assert.Equal("default", moniker.Unique);
    }

    [Fact]
    public async Task DocumentSymbol_WithEmptyClientCapabilities_ReturnsFlatSymbolInformation()
    {
        // ProtocolSession deliberately initializes with new ClientCapabilities(), so hierarchy is not advertised.
        var symbols = await _session.RequestAsync<SymbolInformation[]>(
            Methods.TextDocumentDocumentSymbolName,
            new DocumentSymbolParams { TextDocument = _session.TextDocument });

        Assert.NotNull(symbols);
        var baseType = Assert.Single(symbols, symbol => symbol.Name == "NavigationBase");
        Assert.Equal(SymbolKind.Class, baseType.Kind);
        AssertGeneratedUri(baseType.Location.Uri);
        var method = Assert.Single(symbols, symbol => symbol.Name == "Format");
        Assert.Equal(SymbolKind.Method, method.Kind);
        Assert.Equal("NavigationBase", method.ContainerName);
        AssertGeneratedUri(method.Location.Uri);
    }

    private void AssertGeneratedLocation(LspLocation location, Position expectedStart)
    {
        AssertGeneratedUri(location.Uri);
        Assert.Equal(expectedStart.Line, location.Range.Start.Line);
        Assert.Equal(expectedStart.Character, location.Range.Start.Character);
    }

    private void AssertGeneratedUri(Uri uri)
    {
        Assert.Equal(_session.Uri.LocalPath, uri.LocalPath, ignoreCase: true);
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

    private static (int StartLine, int StartCharacter, int EndLine, int EndCharacter) RangeKey(LspRange range)
    {
        return (
            range.Start.Line,
            range.Start.Character,
            range.End.Line,
            range.End.Character);
    }

    private static void AssertRangeContains(LspRange outer, LspRange inner)
    {
        var outerStart = (outer.Start.Line, outer.Start.Character);
        var innerStart = (inner.Start.Line, inner.Start.Character);
        var innerEnd = (inner.End.Line, inner.End.Character);
        var outerEnd = (outer.End.Line, outer.End.Character);
        Assert.True(outerStart.CompareTo(innerStart) <= 0, "Parent selection starts after its child.");
        Assert.True(innerEnd.CompareTo(outerEnd) <= 0, "Parent selection ends before its child.");
    }

    private const string GeneratedSource = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace Game.Data.Navigation
        {
            /// <summary>Shared data used by navigation.</summary>
            public class NavigationBase
            {
                /// <summary>A counter updated by consumers.</summary>
                public int Counter;

                /// <summary>Formats a value with its label.</summary>
                /// <param name="label">The label text.</param>
                /// <param name="value">The numeric value.</param>
                public string Format(string label, int value)
                {
                    /*
                     * Multi-line comment for folding.
                     */
                    // First consecutive comment.
                    // Second consecutive comment.
                    return label + ": " + value;
                }
            }

            public interface INavigationEntity
            {
            }

            public class NavigationEntity : NavigationBase, INavigationEntity
            {
                /// <summary>Creates an entity.</summary>
                /// <param name="name">The entity name.</param>
                /// <param name="kind">The entity kind.</param>
                public NavigationEntity(string name, int kind)
                {
                    Name = name;
                    Kind = kind;
                }

                public string Name { get; }

                public int Kind { get; }
            }

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class NavigationAttribute : Attribute
            {
                /// <summary>Configures navigation metadata.</summary>
                /// <param name="category">The navigation category.</param>
                /// <param name="priority">The navigation priority.</param>
                public NavigationAttribute(string category, int priority)
                {
                    Category = category;
                    Priority = priority;
                }

                public string Category { get; }

                public int Priority { get; }
            }

            [Navigation("navigation", 7)]
            public sealed class NavigationConsumer
            {
                private NavigationBase _field = new NavigationBase();

        #region Invocation Helpers
                public string Run()
                {
                    var typed = new NavigationEntity("first", 1);
                    _field.Counter = typed.Kind;
                    var current = _field.Counter;
                    var text = _field.Format("first", current);
                    text = _field.Format("second", current);
        #if DEBUG
                    text = text.ToUpperInvariant();
        #endif
                    return text;
                }
        #endregion

                public string CompleteMembers()
                {
                    var instance = new NavigationBase();
                    return instance.Format("completion", 1);
                }
            }
        }
        """;

    public sealed class ProtocolSession : IAsyncLifetime
    {
        private LspJsonRpcSession? _server;
        private JsonRpc? _client;
        private string? _filePath;

        public Uri Uri { get; private set; } = null!;

        public TextDocumentIdentifier TextDocument => new() { Uri = Uri };

        private JsonRpc Client => _client ?? throw new InvalidOperationException("Session is not initialized.");

        public Task<T?> RequestAsync<T>(string method, object parameters)
        {
            return Client.InvokeWithParameterObjectAsync<T>(method, parameters);
        }

        public Position PositionOf(string snippet, int occurrence = 0, int offset = 0)
        {
            if (occurrence < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(occurrence));
            }

            var index = -1;
            for (var current = 0; current <= occurrence; current++)
            {
                index = GeneratedSource.IndexOf(snippet, index + 1, StringComparison.Ordinal);
                if (index < 0)
                {
                    throw new InvalidOperationException(
                        $"Could not find occurrence {occurrence} of generated source snippet '{snippet}'.");
                }
            }

            if (offset < 0 || offset > snippet.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            index += offset;
            var line = 0;
            var lineStart = 0;
            for (var current = 0; current < index; current++)
            {
                if (GeneratedSource[current] == '\n')
                {
                    line++;
                    lineStart = current + 1;
                }
            }

            return new Position(line, index - lineStart);
        }

        public Position[] AllPositions(string value)
        {
            var positions = new List<Position>();
            var occurrence = 0;
            var index = GeneratedSource.IndexOf(value, StringComparison.Ordinal);
            while (index >= 0)
            {
                positions.Add(PositionOf(value, occurrence));
                occurrence++;
                index = GeneratedSource.IndexOf(value, index + value.Length, StringComparison.Ordinal);
            }

            return positions.ToArray();
        }

        public async Task InitializeAsync()
        {
            var dummyPath = GetDummyProjectPath();
            var filePath = Path.Combine(
                dummyPath,
                "Assets",
                "Scripts",
                "Data",
                $"NavigationFeatures_{Guid.NewGuid():N}.cs");
            File.WriteAllText(filePath, GeneratedSource);
            _filePath = filePath;
            Uri = new Uri(filePath);

            try
            {
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
                        RootUri = new Uri(dummyPath),
                        Capabilities = new ClientCapabilities(),
                    });
                Assert.NotNull(_server.WorkspaceManager?.AddOrRefreshDiskFile(filePath));
            }
            catch
            {
                _client?.Dispose();
                _server?.Dispose();
                File.Delete(filePath);
                throw;
            }
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
                if (_filePath is not null)
                {
                    File.Delete(_filePath);
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
