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
        var loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddReference(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var fileName = Path.GetFileName(path);
            if (!loadedNames.Add(fileName)) return;

            var meta = GetOrCreateMetadataReference(path);
            if (meta is not null) refs.Add(meta);
        }

        // 1. Resolve BCL / CoreLib directory (works in single-file published apps, test runs, and CLI runs)
        var coreLibDir = ResolveCoreLibDirectory();
        if (!string.IsNullOrEmpty(coreLibDir) && Directory.Exists(coreLibDir))
        {
            AddBclReferences(coreLibDir, AddReference);
        }

        // 2. Ensure netstandard.dll (2.1.0.0) is referenced (crucial for Unity assemblies compiled against netstandard 2.1)
        if (!loadedNames.Contains("netstandard.dll"))
        {
            AddFallbackNetStandardReferences(AddReference);
        }

        // 3. Unity Managed DLLs (UnityEngine.dll, UnityEngine.CoreModule.dll, etc.)
        AddUnityManagedReferences(AddReference);

        return refs;
    }

    private static string? ResolveCoreLibDirectory()
    {
        // 1. Try from TRUSTED_PLATFORM_ASSEMBLIES (works in single-file published apps)
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (string.Equals(Path.GetFileName(path), "System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase))
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        return dir;
                    }
                }
            }
        }

        // 2. Try from typeof(object).Assembly.Location (non-single-file run)
        try
        {
            var loc = typeof(object).Assembly.Location;
            if (!string.IsNullOrEmpty(loc))
            {
                var dir = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    return dir;
                }
            }
        }
        catch
        {
        }

        // 3. Try standard dotnet shared directory
        var sharedRoots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf))
            {
                sharedRoots.Add(Path.Combine(pf, "dotnet", "shared", "Microsoft.NETCore.App"));
            }
        }
        else
        {
            sharedRoots.Add("/usr/share/dotnet/shared/Microsoft.NETCore.App");
            sharedRoots.Add("/usr/local/share/dotnet/shared/Microsoft.NETCore.App");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                sharedRoots.Add(Path.Combine(home, ".dotnet", "shared", "Microsoft.NETCore.App"));
            }
        }

        foreach (var sharedRoot in sharedRoots)
        {
            if (Directory.Exists(sharedRoot))
            {
                try
                {
                    var latest = Directory.EnumerateDirectories(sharedRoot)
                        .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (!string.IsNullOrEmpty(latest) && Directory.Exists(latest))
                    {
                        return latest;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }

    private static void AddBclReferences(string coreLibDir, Action<string> addRef)
    {
        var coreAssemblies = new[]
        {
            "System.Private.CoreLib.dll",
            "System.Runtime.dll",
            "netstandard.dll",
            "mscorlib.dll",
            "System.dll",
            "System.Core.dll",
            "System.Collections.dll",
            "System.Collections.Concurrent.dll",
            "System.Collections.NonGeneric.dll",
            "System.Console.dll",
            "System.IO.dll",
            "System.Linq.dll",
            "System.Memory.dll",
            "System.Buffers.dll",
            "System.Numerics.Vectors.dll",
            "System.ObjectModel.dll",
            "System.Threading.dll",
            "System.Threading.Tasks.dll",
            "System.ComponentModel.Primitives.dll",
            "System.ComponentModel.TypeConverter.dll",
        };

        foreach (var asmName in coreAssemblies)
        {
            var p = Path.Combine(coreLibDir, asmName);
            if (File.Exists(p))
            {
                addRef(p);
            }
        }
    }

    private void AddFallbackNetStandardReferences(Action<string> addRef)
    {
        // 1. Check Unity Editor NetStandard directory
        var netStandardCandidates = new List<string>();
        if (!string.IsNullOrEmpty(ProjectInfo.EditorPath))
        {
            netStandardCandidates.Add(Path.Combine(ProjectInfo.EditorPath, "Data", "NetStandard", "ref", "2.1.0", "netstandard.dll"));
            netStandardCandidates.Add(Path.Combine(ProjectInfo.EditorPath, "Contents", "NetStandard", "ref", "2.1.0", "netstandard.dll"));
            netStandardCandidates.Add(Path.Combine(ProjectInfo.EditorPath, "NetStandard", "ref", "2.1.0", "netstandard.dll"));
        }
        if (!string.IsNullOrEmpty(ProjectInfo.ManagedDllDirectory))
        {
            var parent = Path.GetDirectoryName(ProjectInfo.ManagedDllDirectory);
            if (!string.IsNullOrEmpty(parent))
            {
                netStandardCandidates.Add(Path.Combine(parent, "NetStandard", "ref", "2.1.0", "netstandard.dll"));
            }
        }

        foreach (var candidate in netStandardCandidates)
        {
            if (File.Exists(candidate))
            {
                addRef(candidate);
                return;
            }
        }

        // 2. Check .NET SDK targeting pack
        var packBases = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf)) packBases.Add(Path.Combine(pf, "dotnet", "packs", "NETStandard.Library.Ref"));
        }
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot)) packBases.Add(Path.Combine(dotnetRoot, "packs", "NETStandard.Library.Ref"));
        packBases.Add("/usr/share/dotnet/packs/NETStandard.Library.Ref");
        packBases.Add("/usr/local/share/dotnet/packs/NETStandard.Library.Ref");

        foreach (var packBase in packBases)
        {
            if (!Directory.Exists(packBase)) continue;
            try
            {
                var vDir = Directory.EnumerateDirectories(packBase)
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (vDir != null)
                {
                    var p = Path.Combine(vDir, "ref", "netstandard2.1", "netstandard.dll");
                    if (File.Exists(p))
                    {
                        addRef(p);
                        return;
                    }
                }
            }
            catch
            {
            }
        }
    }

    private void AddUnityManagedReferences(Action<string> addRef)
    {
        if (string.IsNullOrEmpty(ProjectInfo.ManagedDllDirectory) ||
            !Directory.Exists(ProjectInfo.ManagedDllDirectory))
        {
            return;
        }

        var managedDir = ProjectInfo.ManagedDllDirectory;
        var unitySubDir = Path.Combine(managedDir, "UnityEngine");

        // In modern Unity (2018+), UnityEngine modules reside in Managed/UnityEngine
        if (Directory.Exists(unitySubDir))
        {
            foreach (var dll in Directory.EnumerateFiles(unitySubDir, "UnityEngine*.dll"))
            {
                addRef(dll);
            }
        }
        else
        {
            foreach (var dll in Directory.EnumerateFiles(managedDir, "UnityEngine*.dll"))
            {
                addRef(dll);
            }
        }
    }

    private void AddEditorReferences(string managedDir, List<MetadataReference> references)
    {
        var loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unitySubDir = Path.Combine(managedDir, "UnityEngine");

        // 1. In modern Unity (2018+), modular editor DLLs reside in Managed/UnityEngine
        if (Directory.Exists(unitySubDir))
        {
            foreach (var dll in Directory.EnumerateFiles(unitySubDir, "UnityEditor*.dll"))
            {
                var fileName = Path.GetFileName(dll);
                if (loadedNames.Add(fileName))
                {
                    var meta = GetOrCreateMetadataReference(dll);
                    if (meta is not null && !references.Contains(meta))
                    {
                        references.Add(meta);
                    }
                }
            }
        }

        // 2. Add any additional root editor assemblies (e.g. UnityEditor.Graphs.dll),
        // but skip monolithic UnityEditor.dll if modular UnityEditor has already been loaded!
        foreach (var dll in Directory.EnumerateFiles(managedDir, "UnityEditor*.dll"))
        {
            var fileName = Path.GetFileName(dll);
            if (loadedNames.Add(fileName))
            {
                var meta = GetOrCreateMetadataReference(dll);
                if (meta is not null && !references.Contains(meta))
                {
                    references.Add(meta);
                }
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
    /// Removes a document completely from the Roslyn solution (e.g. when deleted on disk).
    /// </summary>
    public bool RemoveDocument(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        lock (_solutionLock)
        {
            if (!_documentIdsByPath.TryRemove(full, out var docId))
            {
                return false;
            }

            var newSol = CurrentSolution.RemoveDocument(docId);
            return _workspace.TryApplyChanges(newSol);
        }
    }

    /// <summary>
    /// Adds or refreshes a file from disk into the Roslyn solution.
    /// </summary>
    public DocumentId? AddOrRefreshDiskFile(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        if (!File.Exists(full))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(full);
            return OpenOrUpdateDocument(full, text);
        }
        catch
        {
            return null;
        }
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
