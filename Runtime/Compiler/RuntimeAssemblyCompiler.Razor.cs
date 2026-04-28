using System.Diagnostics;
using System.Text;

namespace Engine.Files.Compiler;

public abstract partial class RuntimeAssemblyCompiler<TResult>
{
    /// <summary>Temporary directory used for the Razor SDK csproj across compile cycles.</summary>
    protected string? _razorProjectDir;

    /// <summary>Assembly name for the temporary Razor build project. Override to customize.</summary>
    protected virtual string RazorAssemblyName => AssemblyNamePrefix;

    /// <summary>Root namespace for the temporary Razor build project. Override to customize.</summary>
    protected virtual string RazorRootNamespace => AssemblyNamePrefix;

    /// <summary>
    /// Compiles a mixed set of <c>.cs</c> and <c>.razor</c> files via a generated
    /// <c>Microsoft.NET.Sdk.Razor</c> project + <c>dotnet build</c>.
    /// </summary>
    /// <returns>Path to the compiled DLL or <see langword="null"/> on failure.</returns>
    protected string? CompileWithDotnetBuild(
        List<string> csFiles,
        List<string> razorFiles,
        List<string> cssFiles,
        TResult result)
    {
        if (!EnableRazor)
            throw new InvalidOperationException("CompileWithDotnetBuild called but EnableRazor is false.");

        var gen = Interlocked.Increment(ref _generation);
        var projectDir = GetOrCreateRazorProjectDir();

        try
        {
            var srcDir = Path.Combine(projectDir, "src");
            if (Directory.Exists(srcDir))
                Directory.Delete(srcDir, recursive: true);
            Directory.CreateDirectory(srcDir);

            // Preserve relative paths so files in different subdirectories don't collide.
            foreach (var file in csFiles.Concat(razorFiles))
            {
                var relativePath = GetRelativeScriptPath(file);
                var dest = Path.Combine(srcDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }

            foreach (var dir in _scriptDirectories)
            {
                var imports = Path.Combine(dir, "_Imports.razor");
                if (File.Exists(imports))
                {
                    File.Copy(imports, Path.Combine(srcDir, "_Imports.razor"), overwrite: true);
                    break;
                }
            }

            var csprojPath = Path.Combine(projectDir, $"{RazorAssemblyName}.csproj");
            File.WriteAllText(csprojPath, GenerateProjectFile());

            var (exitCode, stdout, stderr) = RunDotnetBuild(projectDir);

            if (exitCode != 0)
            {
                ParseBuildErrors(stderr + "\n" + stdout, result);
                if (result.Errors.Count == 0)
                {
                    result.Errors.Add(new RuntimeCompilationError
                    {
                        FileName = "dotnet build",
                        Message = $"Build failed (exit code {exitCode}):\n{stderr}",
                    });
                }
                result.Success = false;
                result.Message = $"Razor build failed with {result.Errors.Count} error(s).";
                return null;
            }

            var outputDll = Path.Combine(projectDir, "bin", "Debug", "net10.0", $"{RazorAssemblyName}.dll");
            if (!File.Exists(outputDll))
                outputDll = Path.Combine(projectDir, "bin", "Release", "net10.0", $"{RazorAssemblyName}.dll");

            if (!File.Exists(outputDll))
            {
                result.Errors.Add(new RuntimeCompilationError
                {
                    FileName = "dotnet build",
                    Message = "Build succeeded but output DLL was not found.",
                });
                result.Success = false;
                result.Message = "Build output not found.";
                return null;
            }

            return outputDll;
        }
        catch (Exception ex)
        {
            result.Errors.Add(new RuntimeCompilationError
            {
                FileName = "dotnet build",
                Message = $"Build process failed: {ex.Message}",
            });
            result.Success = false;
            result.Message = $"Build process exception: {ex.Message}";
            return null;
        }
    }

    private string GetOrCreateRazorProjectDir()
    {
        if (_razorProjectDir != null && Directory.Exists(_razorProjectDir))
            return _razorProjectDir;
        _razorProjectDir = Path.Combine(Path.GetTempPath(), $"{RazorAssemblyName}_RazorBuild");
        Directory.CreateDirectory(_razorProjectDir);
        return _razorProjectDir;
    }

    private string GenerateProjectFile()
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<Project Sdk="Microsoft.NET.Sdk.Razor">""");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine("    <NoWarn>$(NoWarn);CS1591</NoWarn>");
        sb.AppendLine($"    <AssemblyName>{RazorAssemblyName}</AssemblyName>");
        sb.AppendLine($"    <RootNamespace>{RazorRootNamespace}</RootNamespace>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine("""    <FrameworkReference Include="Microsoft.AspNetCore.App" />""");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine();

        var userRefs = _userAssemblyPaths.Where(File.Exists).ToList();
        if (userRefs.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var refPath in userRefs)
            {
                var name = Path.GetFileNameWithoutExtension(refPath);
                sb.AppendLine($"""    <Reference Include="{name}">""");
                sb.AppendLine($"      <HintPath>{refPath}</HintPath>");
                sb.AppendLine("    </Reference>");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    private static (int ExitCode, string Stdout, string Stderr) RunDotnetBuild(string projectDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "build --nologo -v q",
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60_000);
        return (proc.ExitCode, stdout, stderr);
    }

    private static void ParseBuildErrors(string output, TResult result)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.Contains(": error "))
            {
                var errorIdx = trimmed.IndexOf(": error ", StringComparison.Ordinal);
                var prefix = trimmed[..errorIdx];
                var message = trimmed[(errorIdx + 2)..];

                var fileName = prefix;
                var errorLine = 0;
                var errorCol = 0;
                var parenIdx = prefix.LastIndexOf('(');
                if (parenIdx >= 0)
                {
                    fileName = prefix[..parenIdx];
                    var coords = prefix[(parenIdx + 1)..].TrimEnd(')');
                    var parts = coords.Split(',');
                    if (parts.Length >= 1) int.TryParse(parts[0], out errorLine);
                    if (parts.Length >= 2) int.TryParse(parts[1], out errorCol);
                }

                result.Errors.Add(new RuntimeCompilationError
                {
                    FileName = Path.GetFileName(fileName),
                    Message = message,
                    Line = errorLine,
                    Column = errorCol,
                });
            }
            else if (trimmed.Contains(": warning "))
            {
                var warnIdx = trimmed.IndexOf(": warning ", StringComparison.Ordinal);
                result.Warnings.Add(trimmed[(warnIdx + 2)..]);
            }
        }
    }

    private string GetRelativeScriptPath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        foreach (var dir in _scriptDirectories)
        {
            var fullDir = Path.GetFullPath(dir);
            if (fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
                return fullPath[(fullDir.Length + 1)..];
        }
        return Path.GetFileName(filePath);
    }
}

