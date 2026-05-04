using System.Diagnostics;

class Program
{
    static readonly string[] Builtins = { "echo", "exit", "type", "pwd", "cd" };

    static string? FindExecutableInPath(string cmd)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        var directories = pathEnv.Split(Path.PathSeparator);

        foreach (var dir in directories)
        {
            if (string.IsNullOrEmpty(dir))
                continue;

            try
            {
                var fullPath = Path.Combine(dir, cmd);

                if (!File.Exists(fullPath))
                    continue;

                if (IsExecutable(fullPath))
                    return fullPath;
            }
            catch (Exception)
            {
                // Silently skip inaccessible paths
            }
        }

        return null;
    }

    static bool IsExecutable(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);

            if (fileInfo.Attributes.HasFlag(FileAttributes.Directory))
                return false;

            if (!OperatingSystem.IsWindows())
            {
                var unixAttrs = fileInfo.UnixFileMode;
                return (unixAttrs & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
            }
            else
            {
                var ext = Path.GetExtension(filePath).ToLower();
                return ext == ".exe" || ext == ".bat" || ext == ".cmd" || ext == ".com" || ext == ".ps1";
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool IsBuiltin(string cmd)
    {
        return Array.Exists(Builtins, element => element == cmd);
    }

    // ── ExecuteExternalProgram ────────────────────────────────────────────────
    // FIX: argv[0] must be the bare command name (e.g. "custom_exe_1234"),
    // NOT the full path (e.g. "/tmp/dog/custom_exe_1234").
    //
    // In .NET, ProcessStartInfo.FileName becomes argv[0] automatically,
    // so setting FileName to the full path caused the wrong argv[0].
    //
    // The solution: run through /bin/sh using "exec -a <name> -- <fullpath> <args>".
    //   -a <name>   explicitly sets argv[0] to the bare command name
    //   --          separates the argv[0] override from the path and args
    // This matches real Unix shell behaviour where argv[0] is always the name
    // the user typed, not the resolved path.
    static void ExecuteExternalProgram(string cmd, string[] args)
    {
        var execPath = FindExecutableInPath(cmd);
        if (string.IsNullOrEmpty(execPath))
        {
            Console.WriteLine($"{cmd}: command not found");
            return;
        }

        try
        {
            // Shell-escape the command name and path to handle spaces/special chars.
            string escapedCmd      = ShellEscape(cmd);
            string escapedExecPath = ShellEscape(execPath);

            // Build the argument string for the child process.
            // Each argument is individually shell-escaped and joined with spaces.
            string argsStr = args.Length > 0
                ? " " + string.Join(" ", Array.ConvertAll(args, ShellEscape))
                : string.Empty;

            // exec -a sets argv[0]; -- prevents the path from being mistaken
            // for an option; the remaining tokens are argv[1], argv[2], ...
            string shellCommand = $"exec -a {escapedCmd} -- {escapedExecPath}{argsStr}";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = "/bin/sh",
                    Arguments              = $"-c \"{shellCommand}\"",
                    UseShellExecute        = false,
                    RedirectStandardInput  = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError  = false,
                }
            };

            process.Start();
            process.WaitForExit();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error executing {cmd}: {ex.Message}");
        }
    }

    // Wraps a string in single quotes and escapes any embedded single quotes.
    // This is the standard POSIX shell escaping technique.
    static string ShellEscape(string s)
    {
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    static void Main()
    {
        while (true)
        {
            Console.Write("$ ");

            var input = Console.ReadLine();
            if (input == null)
                break;

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            var cmd = parts[0];

            if (cmd == "exit")
                break;

            if (cmd == "echo")
            {
                if (parts.Length == 1)
                    Console.WriteLine();
                else
                    Console.WriteLine(string.Join(" ", parts, 1, parts.Length - 1));
                continue;
            }

            if (cmd == "type")
            {
                if (parts.Length < 2)
                {
                    Console.WriteLine("type: missing operand");
                    continue;
                }

                var targetCmd = parts[1];
                if (IsBuiltin(targetCmd))
                {
                    Console.WriteLine($"{targetCmd} is a shell builtin");
                }
                else
                {
                    var execPath = FindExecutableInPath(targetCmd);
                    if (!string.IsNullOrEmpty(execPath))
                        Console.WriteLine($"{targetCmd} is {execPath}");
                    else
                        Console.WriteLine($"{targetCmd}: not found");
                }
                continue;
            }

            if (cmd == "pwd")
            {
                Console.WriteLine(Directory.GetCurrentDirectory());
                continue;
            }

            if (cmd == "cd")
            {
                if (parts.Length < 2)
                {
                    // cd without arguments - could go to home, but for now just skip
                    Console.WriteLine("cd: missing operand");
                    continue;
                }

                var targetDir = parts[1];

                // Handle ~ (home directory)
                if (targetDir == "~" || targetDir.StartsWith("~/"))
                {
                    var homeDir = Environment.GetEnvironmentVariable("HOME");
                    if (string.IsNullOrEmpty(homeDir))
                    {
                        Console.WriteLine("cd: HOME not set");
                        continue;
                    }

                    // Replace ~ with home directory path
                    if (targetDir == "~")
                        targetDir = homeDir;
                    else
                        targetDir = homeDir + targetDir.Substring(1); // Replace ~ with home path
                }

                if (!Directory.Exists(targetDir))
                {
                    Console.WriteLine($"cd: {parts[1]}: No such file or directory");
                    continue;
                }

                try
                {
                    Directory.SetCurrentDirectory(targetDir);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"cd: {parts[1]}: {ex.Message}");
                }
                continue;
            }

            // External program
            var remainingArgs = parts.Length > 1 ? parts[1..] : Array.Empty<string>();
            ExecuteExternalProgram(cmd, remainingArgs);
        }
    }
}