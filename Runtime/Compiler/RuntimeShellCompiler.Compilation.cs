using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Editor.Shell;

public sealed partial class RuntimeShellCompiler
{
    /// <summary>
    /// Performs a full compilation cycle: collect files → parse → compile → load → discover builders →
    /// register a <see cref="ShellSourceIds.Dynamic"/> contribution on the <see cref="ShellRegistry"/>.
    /// When <c>.razor</c> files are present, delegates to <c>dotnet build</c> for full Blazor support;
    /// otherwise uses the fast in-memory Roslyn compilation path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entire operation runs under <c>_compileLock</c> to prevent concurrent compilations.
    /// On success, the previous <c>AssemblyLoadContext</c> is unloaded and the new
    /// builders/components are pushed to the registry as a single <see cref="ShellSource"/>
    /// keyed by <see cref="ShellSourceIds.Dynamic"/>.
    /// </para>
    /// <para>If no script files are found, the dynamic source is removed so the registry falls back
    /// to the static (source-generated) source.</para>
    /// </remarks>
    /// <returns>A <see cref="ShellCompilationResult"/> describing success, errors, and warnings.</returns>
    private ShellCompilationResult CompileAndLoad()
    {
        lock (_compileLock)
        {
            var result = new ShellCompilationResult();

            var csFiles = new List<string>();
            var razorFiles = new List<string>();
            var cssFiles = new List<string>();

            foreach (var dir in _scriptDirectories)
            {
                if (!Directory.Exists(dir)) continue;
                csFiles.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
                razorFiles.AddRange(Directory.GetFiles(dir, "*.razor", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase)));
                cssFiles.AddRange(Directory.GetFiles(dir, "*.css", SearchOption.AllDirectories));
            }

            if (csFiles.Count == 0 && razorFiles.Count == 0)
            {
                result.Success = true;
                result.Message = "No script files found.";
                _registry.Update(new ShellDescriptor());
                return result;
            }

            result.Files = csFiles.Concat(razorFiles)
                .Select(Path.GetFileName)
                .ToArray()!;

            Assembly assembly;

            if (razorFiles.Count > 0)
            {
                // -- Razor path: use dotnet build for full Blazor support --
                var dllPath = CompileWithDotnetBuild(csFiles, razorFiles, cssFiles, result);
                if (dllPath != null)
                {
                    UnloadCurrent();
                    var gen = _generation;
                    var loadContext = new ScriptLoadContext($"Scripts_Gen{gen}");
                    using var dllStream = File.OpenRead(dllPath);
                    assembly = loadContext.LoadFromStream(dllStream);
                    _currentContext = loadContext;
                }
                else if (csFiles.Count > 0)
                {
                    // Razor build failed - fall back to Roslyn for .cs files only
                    // so C#-only shells still work even when .razor compilation has issues.
                    var razorErrors = result.Errors.ToList();
                    result.Errors.Clear();
                    result.Warnings.Add($"Razor build failed ({razorErrors.Count} error(s)) - falling back to C#-only compilation.");
                    foreach (var err in razorErrors)
                        result.Warnings.Add($"  Razor: {err.FileName}({err.Line},{err.Column}): {err.Message}");

                    var roslynAssembly = CompileWithRoslyn(csFiles, result);
                    if (roslynAssembly is null)
                        return result; // Both paths failed
                    assembly = roslynAssembly;
                }
                else
                {
                    return result; // Only .razor files and build failed
                }
            }
            else
            {
                // -- Pure C# path: fast in-memory Roslyn compilation --
                var roslynAssembly = CompileWithRoslyn(csFiles, result);
                if (roslynAssembly is null)
                    return result; // Errors already populated
                assembly = roslynAssembly;
            }

            // Discover shell builders + Blazor panel components in the freshly loaded assembly.
            var (builders, panelComponents) = DiscoverShell(assembly, result);

            var cssSnippets = new List<string>(cssFiles.Count);
            foreach (var css in cssFiles)
            {
                try
                {
                    var content = File.ReadAllText(css);
                    if (!string.IsNullOrWhiteSpace(content))
                        cssSnippets.Add(content);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Failed to read CSS {Path.GetFileName(css)}: {ex.Message}");
                }
            }

            // Push as the dynamic source. Precedence 100 > the static source's 0, so hot-reloaded
            // panels override compiled-in panels with the same Id.
            _registry.RegisterSource(ShellSourceIds.Dynamic, new ShellSource
            {
                Builders = builders,
                PanelComponents = panelComponents,
                CustomCss = cssSnippets,
                Precedence = 100,
            });

            var totalFiles = csFiles.Count + razorFiles.Count;
            result.Success = true;
            result.Message = razorFiles.Count > 0
                ? $"Compiled {totalFiles} file(s) with Razor support (gen {_generation})."
                : $"Compiled {totalFiles} file(s) successfully (gen {_generation}).";
            return result;
        }
    }

    /// <summary>
    /// Compiles <c>.cs</c> files using the in-memory Roslyn pipeline.
    /// </summary>
    /// <returns>The loaded assembly, or <see langword="null"/> on failure.</returns>
    private Assembly? CompileWithRoslyn(List<string> csFiles, ShellCompilationResult result)
    {
        var syntaxTrees = new List<SyntaxTree>();
        foreach (var file in csFiles)
        {
            try
            {
                var source = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(source, path: file,
                    options: new CSharpParseOptions(LanguageVersion.Latest));
                syntaxTrees.Add(tree);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ShellCompilationError
                {
                    FileName = Path.GetFileName(file),
                    Message = $"Failed to read: {ex.Message}"
                });
            }
        }

        if (result.Errors.Count > 0)
        {
            result.Success = false;
            result.Message = "Parse errors.";
            return null;
        }

        var gen = Interlocked.Increment(ref _generation);
        var compilation = CSharpCompilation.Create(
            $"EditorScripts_Gen{gen}",
            syntaxTrees,
            _references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithAllowUnsafe(true));

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        if (!emitResult.Success)
        {
            foreach (var diag in emitResult.Diagnostics)
            {
                if (diag.Severity == DiagnosticSeverity.Error)
                {
                    var lineSpan = diag.Location.GetMappedLineSpan();
                    result.Errors.Add(new ShellCompilationError
                    {
                        FileName = Path.GetFileName(lineSpan.Path ?? ""),
                        Message = diag.GetMessage(),
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1
                    });
                }
            }

            result.Success = false;
            result.Message = $"Compilation failed with {result.Errors.Count} error(s).";
            return null;
        }

        // Load into an isolated AssemblyLoadContext so the previous generation can be unloaded.
        ms.Position = 0;
        UnloadCurrent();

        var loadContext = new ScriptLoadContext($"Scripts_Gen{gen}");
        var assembly = loadContext.LoadFromStream(ms);
        _currentContext = loadContext;

        return assembly;
    }

    /// <summary>
    /// Discovers types annotated with <see cref="EditorShellAttribute"/> implementing
    /// <see cref="IEditorShellBuilder"/>, instantiates them, and discovers Blazor components
    /// annotated with <see cref="EditorPanelAttribute"/>. Returned to the caller as raw lists
    /// so the <see cref="ShellRegistry"/> can merge them with the static source.
    /// </summary>
    /// <param name="assembly">The compiled script assembly to scan.</param>
    /// <param name="result">Compilation result to append warnings to on instantiation failures.</param>
    /// <returns>The discovered builders (sorted by <see cref="IEditorShellBuilder.Order"/>) and panel components.</returns>
    private static (IReadOnlyList<IEditorShellBuilder> Builders,
                    IReadOnlyList<(EditorPanelAttribute Attr, Type Type)> PanelComponents)
        DiscoverShell(Assembly assembly, ShellCompilationResult result)
    {
        var builders = new List<IEditorShellBuilder>();
        var panelComponents = new List<(EditorPanelAttribute Attr, Type Type)>();

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.GetCustomAttribute<EditorShellAttribute>() != null &&
                typeof(IEditorShellBuilder).IsAssignableFrom(type))
            {
                try
                {
                    if (Activator.CreateInstance(type) is IEditorShellBuilder builder)
                        builders.Add(builder);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Failed to instantiate {type.Name}: {ex.Message}");
                }
            }

            var panelAttr = type.GetCustomAttribute<EditorPanelAttribute>();
            if (panelAttr != null)
                panelComponents.Add((panelAttr, type));
        }

        builders.Sort((a, b) => a.Order.CompareTo(b.Order));
        return (builders, panelComponents);
    }
}
