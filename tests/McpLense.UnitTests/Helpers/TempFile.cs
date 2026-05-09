using System;
using System.IO;

namespace McpLense.UnitTests.Helpers;

internal sealed class TempFile : IDisposable
{
    public TempFile(string contents, string extension = ".json")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mcplense-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(Path, contents);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mcplense-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string WriteFile(string relativePath, string contents)
    {
        var fullPath = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}
