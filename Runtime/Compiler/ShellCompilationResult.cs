using Engine.Files.Compiler;

namespace Editor.Shell;

/// <summary>Result of a shell script compilation cycle (extends <see cref="RuntimeCompilationResult"/>).</summary>
/// <remarks>Currently no shell-specific fields; the typed alias keeps the existing public API stable.</remarks>
public sealed class ShellCompilationResult : RuntimeCompilationResult;

/// <summary>A shell-script compilation error (extends <see cref="RuntimeCompilationError"/>).</summary>
public sealed class ShellCompilationError : RuntimeCompilationError;