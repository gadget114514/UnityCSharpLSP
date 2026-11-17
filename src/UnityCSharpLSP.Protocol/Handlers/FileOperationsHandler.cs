using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using UnityCSharpLSP.Protocol.Models;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace UnityCSharpLSP.Protocol.Handlers;

/// <summary>
/// Implements LSP 3.16 file operation events. "Will" requests compute the edits that
/// keep C# source consistent with the pending file operation; "did" notifications
/// update the owning Roslyn workspace.
/// </summary>
public sealed class FileOperationsHandler
{
    private readonly WorkspaceManagerProvider _workspaceManagerProvider;

    public FileOperationsHandler(WorkspaceManagerProvider workspaceManagerProvider)
    {
        _workspaceManagerProvider = workspaceManagerProvider;
    }

    [JsonRpcMethod(CustomLspMethods.WorkspaceWillCreateFilesName, UseSingleObjectParameterDeserialization = true)]
    public Task<WorkspaceEdit?> WillCreateFiles(CreateFilesParams @params, CancellationToken ct = default)
    {
        var result = new WorkspaceEdit { Changes = new Dictionary<string, TextEdit[]>() };
        if (@params?.Files is null)
        {
            return Task.FromResult<WorkspaceEdit?>(result);
        }

        foreach (var file in @params.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = ToPath(file?.Uri);
            if (path is null || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ws = _workspaceManagerProvider(path);
            if (ws is null)
            {
                continue;
            }

            var className = ToIdentifier(Path.GetFileNameWithoutExtension(path));
            var namespaceName = ToNamespace(ws.ProjectInfo.ProjectRootPath, path);
            var skeleton = $"namespace {namespaceName}\n{{\n    public class {className}\n    {{\n    }}\n}}\n";
            var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;

            AddEdits(result, uri, new TextEdit
            {
                Range = new LspRange
                {
                    Start = new Position(0, 0),
                    End = new Position(0, 0),
                },
                NewText = skeleton,
            });
        }

        return Task.FromResult<WorkspaceEdit?>(result);
    }

    [JsonRpcMethod(CustomLspMethods.WorkspaceDidCreateFilesName, UseSingleObjectParameterDeserialization = true)]
    public void DidCreateFiles(CreateFilesParams @params)
    {
        if (@params?.Files is null) return;
        foreach (var file in @params.Files)
        {
            var path = ToPath(file?.Uri);
            if (path is null) continue;
            _workspaceManagerProvider(path)?.AddOrRefreshDiskFile(path);
        }
    }

    [JsonRpcMethod(CustomLspMethods.WorkspaceWillRenameFilesName, UseSingleObjectParameterDeserialization = true)]
    public async Task<WorkspaceEdit?> WillRenameFiles(RenameFilesParams @params, CancellationToken ct = default)
    {
        var result = new WorkspaceEdit { Changes = new Dictionary<string, TextEdit[]>() };
        if (@params?.Files is null)
        {
            return result;
        }

        foreach (var file in @params.Files)
        {
            ct.ThrowIfCancellationRequested();
            var oldPath = ToPath(file?.OldUri);
            var newPath = ToPath(file?.NewUri);
            if (oldPath is null || newPath is null ||
                !oldPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                !newPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var oldName = Path.GetFileNameWithoutExtension(oldPath);
            var newName = Path.GetFileNameWithoutExtension(newPath);
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                continue;
            }

            var ws = _workspaceManagerProvider(oldPath);
            if (ws is null)
            {
                continue;
            }

            var document = ws.GetDocument(oldPath);
            if (document is null)
            {
                continue;
            }

            var symbol = await FindTypeSymbolAsync(document, oldName, ct).ConfigureAwait(false);
            if (symbol is null)
            {
                continue;
            }

            Solution renamedSolution;
            try
            {
                renamedSolution = await Renamer.RenameSymbolAsync(
                    ws.CurrentSolution,
                    symbol,
                    new SymbolRenameOptions(),
                    newName,
                    ct).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var edits = await RenameHandler.CreateWorkspaceEditAsync(ws.CurrentSolution, renamedSolution, ct)
                .ConfigureAwait(false);
            Merge(result, edits);
        }

        return result;
    }

    [JsonRpcMethod(CustomLspMethods.WorkspaceDidRenameFilesName, UseSingleObjectParameterDeserialization = true)]
    public void DidRenameFiles(RenameFilesParams @params)
    {
        if (@params?.Files is null) return;
        foreach (var file in @params.Files)
        {
            var oldPath = ToPath(file?.OldUri);
            var newPath = ToPath(file?.NewUri);
            if (oldPath is not null)
            {
                _workspaceManagerProvider(oldPath)?.RemoveDocument(oldPath);
            }

            if (newPath is not null)
            {
                _workspaceManagerProvider(newPath)?.AddOrRefreshDiskFile(newPath);
            }
        }
    }

    [JsonRpcMethod(CustomLspMethods.WorkspaceWillDeleteFilesName, UseSingleObjectParameterDeserialization = true)]
    public Task<WorkspaceEdit?> WillDeleteFiles(DeleteFilesParams @params, CancellationToken ct = default)
    {
        var operations = new List<SumType<TextDocumentEdit, CreateFile, RenameFile, DeleteFile>>();
        if (@params?.Files is not null)
        {
            foreach (var file in @params.Files)
            {
                ct.ThrowIfCancellationRequested();
                var path = ToPath(file?.Uri);
                if (path is null)
                {
                    continue;
                }

                operations.Add(new DeleteFile
                {
                    Uri = new Uri(new Uri(Path.GetFullPath(path)).AbsoluteUri),
                    Options = new DeleteFileOptions { IgnoreIfNotExists = true },
                });
            }
        }

        return Task.FromResult<WorkspaceEdit?>(new WorkspaceEdit
        {
            Changes = new Dictionary<string, TextEdit[]>(),
            DocumentChanges = new SumType<TextDocumentEdit[], SumType<TextDocumentEdit, CreateFile, RenameFile, DeleteFile>[]>(
                operations.ToArray()),
        });
    }

    [JsonRpcMethod(CustomLspMethods.WorkspaceDidDeleteFilesName, UseSingleObjectParameterDeserialization = true)]
    public void DidDeleteFiles(DeleteFilesParams @params)
    {
        if (@params?.Files is null) return;
        foreach (var file in @params.Files)
        {
            var path = ToPath(file?.Uri);
            if (path is null) continue;
            _workspaceManagerProvider(path)?.RemoveDocument(path);
        }
    }

    private static async Task<ISymbol?> FindTypeSymbolAsync(
        Document document,
        string typeName,
        CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return null;
        }

        var declaration = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault(node => string.Equals(node.Identifier.ValueText, typeName, StringComparison.Ordinal));

        return declaration is null ? null : model.GetDeclaredSymbol(declaration, ct);
    }

    private static void AddEdits(WorkspaceEdit edit, string uri, params TextEdit[] edits)
    {
        if (edit.Changes is null)
        {
            edit.Changes = new Dictionary<string, TextEdit[]>();
        }

        edit.Changes[uri] = edit.Changes.TryGetValue(uri, out var existing)
            ? existing.Concat(edits).ToArray()
            : edits;
    }

    private static void Merge(WorkspaceEdit target, WorkspaceEdit source)
    {
        if (source.Changes is null)
        {
            return;
        }

        foreach (var pair in source.Changes)
        {
            AddEdits(target, pair.Key, pair.Value);
        }
    }

    private static string ToIdentifier(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray();
        if (chars.Length == 0 || char.IsDigit(chars[0]))
        {
            return "_" + new string(chars);
        }

        return new string(chars);
    }

    private static string ToNamespace(string projectRoot, string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? projectRoot;
        var relative = Path.GetRelativePath(projectRoot, directory);
        if (string.IsNullOrEmpty(relative) || relative == ".")
        {
            return "Game";
        }

        var parts = relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Select(ToIdentifier)
            .Where(part => part.Length > 0)
            .ToArray();

        return parts.Length == 0 ? "Game" : string.Join(".", parts);
    }

    private static string? ToPath(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.LocalPath : uri;
    }
}
