using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Engine.Files.Compiler;

public abstract partial class RuntimeAssemblyCompiler<TResult>
{
    /// <summary>Adds a directory to watch for source files. Created if it does not exist.</summary>
    public RuntimeAssemblyCompiler<TResult> WatchDirectory(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
        _scriptDirectories.Add(Path.GetFullPath(path));
        return this;
    }

    /// <summary>Adds an assembly's metadata to the compilation reference set.</summary>
    /// <remarks>Skipped silently if <see cref="Assembly.Location"/> is empty (single-file deployment).</remarks>
    public RuntimeAssemblyCompiler<TResult> AddReference(Assembly assembly)
    {
        var location = assembly.Location;
        if (!string.IsNullOrEmpty(location) && File.Exists(location))
        {
            _references.Add(MetadataReference.CreateFromFile(location));
            _userAssemblyPaths.Add(location);
        }
        return this;
    }

    /// <summary>Adds a Roslyn <see cref="MetadataReference"/> directly.</summary>
    public RuntimeAssemblyCompiler<TResult> AddReference(MetadataReference reference)
    {
        _references.Add(reference);
        return this;
    }
}

