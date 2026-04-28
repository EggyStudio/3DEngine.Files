using System.Reflection;
using Engine.Files.Compiler;

namespace Editor.Shell;

/// <summary>
/// Runtime Roslyn + Razor compiler specialized for editor shell scripts. Delegates all
/// file-watching, debounce, isolated <see cref="System.Runtime.Loader.AssemblyLoadContext"/>,
/// Roslyn / Razor pipelines to <see cref="RuntimeAssemblyCompiler{TResult}"/>; only adds the
/// shell discovery + <see cref="ShellRegistry"/> wire-up.
/// </summary>
/// <remarks>
/// On every successful compile, discovers <c>[EditorShell]</c> implementations of
/// <see cref="IEditorShellBuilder"/> and Blazor components annotated with
/// <see cref="EditorPanelAttribute"/>, gathers any <c>.css</c> snippets, and pushes them as
/// <see cref="ShellSourceIds.Dynamic"/> into the registry with precedence 100 so they
/// override compiled-in (static, source-generator-emitted) panels of the same id.
/// </remarks>
public sealed class RuntimeShellCompiler : RuntimeAssemblyCompiler<ShellCompilationResult>
{
    private readonly ShellRegistry _registry;

    /// <inheritdoc />
    protected override bool EnableRazor => true;

    /// <inheritdoc />
    protected override string[] WatchedExtensions => ["*.cs", "*.razor", "*.css"];

    /// <inheritdoc />
    protected override string AssemblyNamePrefix => "EditorScripts";

    /// <inheritdoc />
    protected override string LoadContextPrefix => "Scripts";

    /// <inheritdoc />
    protected override string RazorAssemblyName => "EditorShells";

    /// <inheritdoc />
    protected override string RazorRootNamespace => "EditorScripts";

    /// <summary>Creates a new shell compiler targeting <paramref name="registry"/>.</summary>
    public RuntimeShellCompiler(ShellRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        // Ensure user shell scripts can reference the builder API.
        AddReference(typeof(ShellRegistry).Assembly);
    }

    /// <summary>Fluent <see cref="RuntimeAssemblyCompiler{TResult}.WatchDirectory"/> typed for chaining.</summary>
    public new RuntimeShellCompiler WatchDirectory(string path)
    {
        base.WatchDirectory(path);
        return this;
    }

    /// <summary>Fluent <see cref="RuntimeAssemblyCompiler{TResult}.AddReference(Assembly)"/> typed for chaining.</summary>
    public new RuntimeShellCompiler AddReference(Assembly assembly)
    {
        base.AddReference(assembly);
        return this;
    }

    /// <inheritdoc />
    protected override void OnNoSourceFiles(ShellCompilationResult result)
    {
        // Empty scripts directory => drop any prior dynamic contribution so the static source wins.
        _registry.RemoveSource(ShellSourceIds.Dynamic);
        result.Success = true;
        result.Message = "No script files found.";
    }

    /// <inheritdoc />
    protected override void OnAssemblyLoaded(Assembly assembly, IReadOnlyList<string> cssFiles,
        ShellCompilationResult result)
    {
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

        // Precedence 100 > the static source's 0 so dynamic panels win id collisions.
        _registry.RegisterSource(ShellSourceIds.Dynamic, new ShellSource
        {
            Builders = builders,
            PanelComponents = panelComponents,
            CustomCss = cssSnippets,
            Precedence = 100,
        });
    }

    /// <inheritdoc />
    protected override void OnDispose()
    {
        if (_razorProjectDir != null && Directory.Exists(_razorProjectDir))
        {
            try
            {
                Directory.Delete(_razorProjectDir, recursive: true);
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }

    /// <summary>
    /// Reflects over <paramref name="assembly"/> for <c>[EditorShell]</c> types implementing
    /// <see cref="IEditorShellBuilder"/> and Blazor components annotated with
    /// <see cref="EditorPanelAttribute"/>.
    /// </summary>
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