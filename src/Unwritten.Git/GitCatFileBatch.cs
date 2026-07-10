using System.Diagnostics;
using System.Text;

namespace Unwritten.Git;

/// <summary>
/// Long-lived <c>git cat-file --batch</c> session for bulk blob reads — member
/// training reads two blobs per (file × commit), and one process per read would
/// dominate build time. Not thread-safe; use one instance per training pass.
/// </summary>
public sealed class GitCatFileBatch : IDisposable
{
    private readonly Process _process;
    private readonly Stream _stdout;

    public GitCatFileBatch(string repoPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("cat-file");
        startInfo.ArgumentList.Add("--batch");

        try
        {
            _process = Process.Start(startInfo) ?? throw new GitException("Failed to start git cat-file.");
        }
        catch (Exception ex) when (ex is not GitException)
        {
            throw new GitException($"Failed to start git cat-file: {ex.Message}");
        }

        _stdout = _process.StandardOutput.BaseStream;
    }

    /// <summary>
    /// Blob content for a revision:path spec (e.g. "ab12cd:src/Foo.cs" or
    /// "ab12cd^:src/Foo.cs"), or null when the object does not exist there.
    /// </summary>
    public string? ReadBlob(string spec)
    {
        _process.StandardInput.WriteLine(spec);
        _process.StandardInput.Flush();

        string header = ReadHeaderLine();
        // Header: "<oid> <type> <size>" or "<spec> missing" / "<spec> ambiguous".
        var parts = header.Split(' ');
        if (parts.Length < 3 || !long.TryParse(parts[^1], out long size))
        {
            return null;
        }

        var buffer = new byte[size];
        int read = 0;
        while (read < size)
        {
            int n = _stdout.Read(buffer, read, (int)(size - read));
            if (n == 0)
            {
                throw new GitException("git cat-file stream ended unexpectedly.");
            }

            read += n;
        }

        _stdout.ReadByte(); // trailing newline after the object body
        return Encoding.UTF8.GetString(buffer);
    }

    private string ReadHeaderLine()
    {
        var line = new List<byte>(64);
        while (true)
        {
            int b = _stdout.ReadByte();
            if (b < 0)
            {
                throw new GitException("git cat-file exited unexpectedly.");
            }

            if (b == '\n')
            {
                return Encoding.UTF8.GetString(line.ToArray());
            }

            line.Add((byte)b);
        }
    }

    public void Dispose()
    {
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }
}
