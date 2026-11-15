using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using UnityCSharpLSP.Workspace.Defines;
using UnityCSharpLSP.Workspace.Graph;
using UnityCSharpLSP.Workspace.Models;

namespace UnityCSharpLSP.Workspace;

/// <summary>
/// Manages the Roslyn <see cref="AdhocWorkspace"/> and the immutable <see cref="Solution"/>
/// corresponding to a Unity project.
/// </summary>
public sealed class UnityWorkspaceManager : IDisposable
{
    private readonly AdhocWorkspace _workspace;
    private readonly object _solutionLock = new();

    private readonly ConcurrentDictionary<string, ProjectId> _projectIdsByName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DocumentId> _documentIdsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MetadataReference> _metadataReferenceCache = new(StringComparer.OrdinalIgnoreCase);

    public UnityProjectInfo ProjectInfo { get; }
    public AsmdefGraph AssemblyGraph { get; }
    public Microsoft.CodeAnalysis.Workspace RoslynWorkspace => _workspace;

    public Solution CurrentSolution => _workspace.CurrentSolution;

    public UnityWorkspaceManager(UnityProjectInfo projectInfo, AsmdefGraph assemblyGraph)
    {
        ProjectInfo = projectInfo;
        AssemblyGraph = assemblyGraph;
        _workspace = new AdhocWorkspace();

        InitializeSolution();
    }

    /// <summary>
    /// Factory method that scans the Unity project root, builds the assembly graph,
    /// and initializes the Roslyn workspace.
    /// </summary>
    public static UnityWorkspaceManager Create(string projectRoot, string? explicitEditorPath = null)
    {
        var projectInfo = UnityProjectLoader.Load(projectRoot, explicitEditorPath);

        // Build assembly graph starting from Assets folder (or ProjectRoot if Assets doesn't exist)
        var assetsDir = Path.Combine(projectInfo.ProjectRootPath, "Assets");
        var searchRoot = Directory.Exists(assetsDir) ? assetsDir : projectInfo.ProjectRootPath;
        var graph = AsmdefGraphBuilder.Build(searchRoot);

        return new UnityWorkspaceManager(projectInfo, graph);
    }

    private void InitializeSolution()
    {
        lock (_solutionLock)
        {
            var solutionId = SolutionId.CreateNewId("UnitySolution");
            var solutionInfo = SolutionInfo.Create(solutionId, VersionStamp.Create());
            _workspace.AddSolution(solutionInfo);

            // Collect common metadata references (BCL + UnityEngine Managed DLLs)
            var commonReferences = CollectCommonReferences();

            // 1. Create a ProjectInfo for each AssemblyNode in the graph
            var projectInfos = new List<ProjectInfo>();

            foreach (var node in AssemblyGraph.Assemblies.Values)
            {
                var projectId = ProjectId.CreateNewId(node.Name);
                _projectIdsByName[node.Name] = projectId;

                var isEditor = node.Name.EndsWith("-Editor", StringComparison.OrdinalIgnoreCase) ||
                               (node.Asmdef is not null && DefineSymbolManager.IsEditorAssembly(node.Asmdef));

                var symbols = DefineSymbolManager.GetSymbols(
                    ProjectInfo.UnityVersion,
                    isEditor,
                    ProjectInfo.Packages,
                    node.Asmdef);

                var parseOptions = new CSharpParseOptions(
                    languageVersion: LanguageVersion.CSharp10,
                    preprocessorSymbols: symbols);

                var compilationOptions = new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable,
                    allowUnsafe: node.Asmdef?.AllowUnsafeCode ?? false);

                // Collect document infos
                var docInfos = new List<DocumentInfo>();
                foreach (var csFile in node.SourceFiles)
                {
                    var docId = DocumentId.CreateNewId(projectId, debugName: Path.GetFileName(csFile));
                    _documentIdsByPath[Path.GetFullPath(csFile)] = docId;

                    var textLoader = new FileTextLoader(csFile);
                    docInfos.Add(DocumentInfo.Create(
                        docId,
                        Path.GetFileName(csFile),
                        filePath: csFile,
                        loader: textLoader));
                }

                var references = new List<MetadataReference>(commonReferences);

                // If node is an Editor assembly, include UnityEditor DLLs if found
                if (isEditor && !string.IsNullOrEmpty(ProjectInfo.ManagedDllDirectory))
                {
                    AddEditorReferences(ProjectInfo.ManagedDllDirectory, references);
                }

                var projectInfo = Microsoft.CodeAnalysis.ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    name: node.Name,
                    assemblyName: node.Name,
                    language: LanguageNames.CSharp,
                    compilationOptions: compilationOptions,
                    parseOptions: parseOptions,
                    documents: docInfos,
                    metadataReferences: references);

                projectInfos.Add(projectInfo);
            }

            // 2. Add projects to workspace
            foreach (var proj in projectInfos)
            {
                _workspace.AddProject(proj);
            }

            // 3. Link project references based on AssemblyNode.References
            var currentSolution = _workspace.CurrentSolution;
            foreach (var node in AssemblyGraph.Assemblies.Values)
            {
                if (!_projectIdsByName.TryGetValue(node.Name, out var fromProjId))
                {
                    continue;
                }

                foreach (var refName in node.References)
                {
                    if (_projectIdsByName.TryGetValue(refName, out var toProjId))
                    {
                        currentSolution = currentSolution.AddProjectReference(
                            fromProjId,
                            new ProjectReference(toProjId));
                    }
                }
            }

            _workspace.TryApplyChanges(currentSolution);
        }
    }

    private List<MetadataReference> CollectCommonReferences()
    {
        var refs = new List<MetadataReference>();

        // 1. Basic CLR / BCL references from current runtime
        var bclAssemblies = new[]
        {
            typeof(object).Assembly,                           // System.Private.CoreLib
            typeof(Console).Assembly,                          // System.Console
            typeof(Enumerable).Assembly,                       // System.Linq
            typeof(List<>).Assembly,                           // System.Collections
            typeof(Task).Assembly,                             // System.Threading.Tasks
            typeof(System.ComponentModel.Component).Assembly,  // System.ComponentModel.Primitives
        };

        var bclPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in bclAssemblies)
        {
            if (!string.IsNullOrEmpty(asm.Location) && File.Exists(asm.Location))
            {
                bclPaths.Add(asm.Location);
            }
        }

        // Also add System.Runtime.dll from the same folder as CoreLib
        var coreLibDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrEmpty(coreLibDir) && Directory.Exists(coreLibDir))
        {
            var systemRuntime = Path.Combine(coreLibDir, "System.Runtime.dll");
            if (File.Exists(systemRuntime)) bclPaths.Add(systemRuntime);

            var netstandard = Path.Combine(coreLibDir, "netstandard.dll");
            if (File.Exists(netstandard)) bclPaths.Add(netstandard);
        }

        foreach (var path in bclPaths)
        {
            var meta = GetOrCreateMetadataReference(path);
            if (meta is not null) refs.Add(meta);
        }

        // 2. Unity Managed DLLs (UnityEngine.dll, UnityEngine.CoreModule.dll, etc.)
        if (!string.IsNullOrEmpty(ProjectInfo.ManagedDllDirectory) &&
            Directory.Exists(ProjectInfo.ManagedDllDirectory))
        {
            var unityDlls = Directory.EnumerateFiles(
                ProjectInfo.ManagedDllDirectory,
                "UnityEngine*.dll",
                SearchOption.AllDirectories);

            foreach (var dll in unityDlls)
            {
                var meta = GetOrCreateMetadataReference(dll);
                if (meta is not null) refs.Add(meta);
            }
        }

        return refs;
    }

    private void AddEditorReferences(string managedDir, List<MetadataReference> references)
    {
        var editorDlls = Directory.EnumerateFiles(
            managedDir,
            "UnityEditor*.dll",
            SearchOption.AllDirectories);

        foreach (var dll in editorDlls)
        {
            var meta = GetOrCreateMetadataReference(dll);
            if (meta is not null && !references.Contains(meta))
            {
                references.Add(meta);
            }
        }
    }

    private MetadataReference? GetOrCreateMetadataReference(string path)
    {
        return _metadataReferenceCache.GetOrAdd(path, p =>
        {
            try
            {
                // Shadow-copy bytes to avoid locking files during Unity compilation
                var bytes = File.ReadAllBytes(p);
                return MetadataReference.CreateFromImage(bytes, filePath: p);
            }
            catch
            {
                return null!;
            }
        });
    }

    /// <summary>
    /// Gets the <see cref="DocumentId"/> associated with a file path.
    /// </summary>
    public DocumentId? GetDocumentId(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        return _documentIdsByPath.TryGetValue(full, out var docId) ? docId : null;
    }

    /// <summary>
    /// Gets the <see cref="Document"/> associated with a file path.
    /// </summary>
    public Document? GetDocument(string filePath)
    {
        var docId = GetDocumentId(filePath);
        return docId is not null ? CurrentSolution.GetDocument(docId) : null;
    }

    /// <summary>
    /// Opens or tracks an in-memory document with specified content.
    /// </summary>
    public DocumentId OpenOrUpdateDocument(string filePath, string text)
    {
        var full = Path.GetFullPath(filePath);
        var sourceText = SourceText.From(text);

        lock (_solutionLock)
        {
            if (_documentIdsByPath.TryGetValue(full, out var existingId))
            {
                var newSolution = CurrentSolution.WithDocumentText(existingId, sourceText);
                _workspace.TryApplyChanges(newSolution);
                return existingId;
            }

            // Find or fallback owning assembly
            var owner = AssemblyGraph.TryGetOwningAssembly(full);
            var assemblyName = owner?.Name ?? AsmdefGraphBuilder.AssemblyCSharpName;

            if (!_projectIdsByName.TryGetValue(assemblyName, out var projId))
            {
                // If the assembly project doesn't exist yet, attach to the first available project
                projId = _projectIdsByName.Values.FirstOrDefault()
                         ?? throw new InvalidOperationException("No projects in workspace.");
            }

            var docId = DocumentId.CreateNewId(projId, Path.GetFileName(full));
            var newSol = CurrentSolution.AddDocument(
                docId,
                Path.GetFileName(full),
                sourceText,
                filePath: full);

            _workspace.TryApplyChanges(newSol);
            _documentIdsByPath[full] = docId;
            return docId;
        }
    }

    /// <summary>
    /// Updates the text of an open document using full replacement.
    /// </summary>
    public bool UpdateDocumentText(string filePath, string newText)
    {
        var docId = GetDocumentId(filePath);
        if (docId is null)
        {
            return false;
        }

        var sourceText = SourceText.From(newText);
        lock (_solutionLock)
        {
            var newSol = CurrentSolution.WithDocumentText(docId, sourceText);
            return _workspace.TryApplyChanges(newSol);
        }
    }

    /// <summary>
    /// Closes a document, reverting its text to disk content if file exists.
    /// </summary>
    public bool CloseDocument(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        if (!_documentIdsByPath.TryGetValue(full, out var docId))
        {
            return false;
        }

        if (File.Exists(full))
        {
            try
            {
                var diskText = File.ReadAllText(full);
                return UpdateDocumentText(full, diskText);
            }
            catch
            {
                // Ignore disk read errors on close
            }
        }

        return true;
    }

    /// <summary>
    /// Gets all compiler diagnostics for a document.
    /// </summary>
    public async Task<IEnumerable<Diagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct = default)
    {
        var doc = GetDocument(filePath);
        if (doc is null)
        {
            return Array.Empty<Diagnostic>();
        }

        var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model is null)
        {
            return Array.Empty<Diagnostic>();
        }

        return model.GetDiagnostics(cancellationToken: ct);
    }

    /// <summary>
    /// Resolves the Roslyn <see cref="ISymbol"/> at the given line and character offset (0-indexed).
    /// </summary>
    public async Task<(ISymbol? Symbol, SemanticModel? SemanticModel)> GetSymbolAtPositionAsync(
        string filePath,
        int line,
        int character,
        CancellationToken ct = default)
    {
        var doc = GetDocument(filePath);
        if (doc is null)
        {
            return (null, null);
        }

        var sourceText = await doc.GetTextAsync(ct).ConfigureAwait(false);
        if (line < 0 || line >= sourceText.Lines.Count)
        {
            return (null, null);
        }

        var textLine = sourceText.Lines[line];
        var position = Math.Min(textLine.Start + character, textLine.End);

        var syntaxTree = await doc.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
        if (syntaxTree is null)
        {
            return (null, null);
        }

        var root = await syntaxTree.GetRootAsync(ct).ConfigureAwait(false);
        var token = root.FindToken(position);
        var node = token.Parent;
        if (node is null)
        {
            return (null, null);
        }

        var semanticModel = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (semanticModel is null)
        {
            return (null, null);
        }

        var symbolInfo = semanticModel.GetSymbolInfo(node, ct);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

        if (symbol is null)
        {
            symbol = semanticModel.GetDeclaredSymbol(node, ct);
        }

        return (symbol, semanticModel);
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private sealed class FileTextLoader : TextLoader
    {
        private readonly string _filePath;

        public FileTextLoader(string filePath)
        {
            _filePath = filePath;
        }

        public override Task<TextAndVersion> LoadTextAndVersionAsync(
            LoadTextOptions options,
            CancellationToken cancellationToken)
        {
            var text = File.Exists(_filePath) ? File.ReadAllText(_filePath) : string.Empty;
            var sourceText = SourceText.From(text);
            return Task.FromResult(TextAndVersion.Create(sourceText, VersionStamp.Create(), _filePath));
        }
    }
}
