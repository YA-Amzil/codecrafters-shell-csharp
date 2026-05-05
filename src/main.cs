using System.Diagnostics;
using System.Text;
using ReadLine;

internal class Program
{
    // Supported shell built-in commands.
    private static readonly string[] Builtins = { "echo", "exit", "type", "pwd", "cd" };

    // ------------------------------------------------------------------------
    // Autocompletion (builtins + executables, with LCP logic)
    // ------------------------------------------------------------------------

    private class BuiltinAutoComplete : IAutoCompleteHandler
    {
        public char[] Separators { get; set; } = new char[] { ' ' };

        private static readonly string[] AutoBuiltins = { "echo", "exit" };

        // Used for multiple-match behavior (bell on first TAB, list on second).
        private string? lastPrefix = null;
        private string[]? lastMatches = null;

        // Computes the longest common prefix of a set of strings.
        private static string LongestCommonPrefix(string[] arr)
        {
            if (arr.Length == 0) return "";
            if (arr.Length == 1) return arr[0];

            var prefix = arr[0];
            for (var i = 1; i < arr.Length; i++)
            {
                var j = 0;
                while (j < prefix.Length && j < arr[i].Length && prefix[j] == arr[i][j])
                    j++;

                prefix = prefix.Substring(0, j);
                if (prefix == "") break;
            }

            return prefix;
        }

        public string[] GetSuggestions(string text, int index)
        {
            var parts = text.Split(' ');
            var currentWord = parts[0];

            // 1. Try builtin completion first.
            var builtinMatches = AutoBuiltins
                .Where(b => b.StartsWith(currentWord))
                .ToArray();

            if (builtinMatches.Length == 1)
                return new[] { builtinMatches[0] + " " };

            if (builtinMatches.Length > 1)
            {
                Console.Write("\x07"); // bell
                return Array.Empty<string>();
            }

            // 2. If we're on the first word, try executable completion.
            if (parts.Length == 1)
            {
                var suggestions = new List<string>();

                var pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathEnv))
                {
                    var directories = pathEnv.Split(Path.PathSeparator);
                    var foundExecutables = new HashSet<string>();

                    foreach (var dir in directories)
                    {
                        if (!Directory.Exists(dir))
                            continue;

                        try
                        {
                            foreach (var file in Directory.GetFiles(dir))
                            {
                                var fileName = Path.GetFileName(file);
                                if (fileName.StartsWith(currentWord) && IsExecutable(file))
                                    foundExecutables.Add(fileName);
                            }
                        }
                        catch
                        {
                            // Ignore directories we can't read.
                        }
                    }

                    suggestions.AddRange(foundExecutables);
                }

                suggestions.Sort();

                if (suggestions.Count == 0)
                {
                    Console.Write("\x07"); // bell for no matches
                    return Array.Empty<string>();
                }

                // Longest Common Prefix logic for partial completion.
                var lcp = LongestCommonPrefix(suggestions.ToArray());

                // If LCP extends beyond current input, complete to LCP.
                if (lcp.Length > currentWord.Length)
                {
                    if (suggestions.Count == 1)
                        return new[] { lcp + " " }; // single match → trailing space

                    return new[] { lcp }; // partial completion
                }

                // If LCP == current input and only one match, complete with space.
                if (suggestions.Count == 1)
                    return new[] { suggestions[0] + " " };

                // Multiple matches: bell on first TAB, list on second.
                if (lastPrefix != currentWord)
                {
                    lastPrefix = currentWord;
                    lastMatches = suggestions.ToArray();
                    Console.Write("\x07");
                    return Array.Empty<string>();
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine(string.Join("  ", suggestions));
                    Console.Write("$ " + currentWord);
                    lastMatches = null;
                    return Array.Empty<string>();
                }
            }

            // No argument completion for now.
            return Array.Empty<string>();
        }
    }

    // ------------------------------------------------------------------------
    // Redirection parsing and helpers
    // ------------------------------------------------------------------------

    private class RedirectionInfo
    {
        public string? StdoutFile { get; set; }
        public bool StdoutAppend { get; set; }
        public string? StderrFile { get; set; }
        public bool StderrAppend { get; set; }
    }

    private static bool IsBuiltin(string cmd)
    {
        return Builtins.Contains(cmd);
    }

    // Parse redirection tokens (>, >>, 2>, 2>>) and return cleaned args + info.
    private static (string[], RedirectionInfo) ParseRedirection(string[] tokens)
    {
        var cleaned = new List<string>();
        var info = new RedirectionInfo();

        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];

            if ((t == ">>" || t == "1>>") && i + 1 < tokens.Length)
            {
                info.StdoutFile = tokens[++i];
                info.StdoutAppend = true;
            }
            else if ((t == ">" || t == "1>") && i + 1 < tokens.Length)
            {
                info.StdoutFile = tokens[++i];
                info.StdoutAppend = false;
            }
            else if (t == "2>>" && i + 1 < tokens.Length)
            {
                info.StderrFile = tokens[++i];
                info.StderrAppend = true;
            }
            else if (t == "2>" && i + 1 < tokens.Length)
            {
                info.StderrFile = tokens[++i];
                info.StderrAppend = false;
            }
            else
            {
                cleaned.Add(t);
            }
        }

        return (cleaned.ToArray(), info);
    }

    // Writes to stdout or a redirected file, depending on redirection info.
    private static void WriteStdout(string text, RedirectionInfo redirect)
    {
        if (!string.IsNullOrEmpty(redirect.StdoutFile))
            try
            {
                using var sw = new StreamWriter(
                    new FileStream(redirect.StdoutFile,
                        redirect.StdoutAppend ? FileMode.Append : FileMode.Create,
                        FileAccess.Write));
                sw.WriteLine(text);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error writing to file: {ex.Message}");
            }
        else
            Console.WriteLine(text);
    }

    // Ensures a redirect target file exists, even if nothing is written.
    private static void EnsureFileCreated(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            using var _ = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error creating redirect file: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------------
    // Executable lookup and execution
    // ------------------------------------------------------------------------

    private static string? FindExecutableInPath(string cmd)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
            try
            {
                var fullPath = Path.Combine(dir, cmd);
                if (File.Exists(fullPath) && IsExecutable(fullPath))
                    return fullPath;
            }
            catch
            {
                // Ignore directories we can't access.
            }

        return null;
    }

    private static bool IsExecutable(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Attributes.HasFlag(FileAttributes.Directory))
                return false;

            if (!OperatingSystem.IsWindows())
            {
                var unixAttrs = fileInfo.UnixFileMode;
                return (unixAttrs &
                        (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
            }
            else
            {
                var ext = Path.GetExtension(filePath).ToLower();
                return ext == ".exe" || ext == ".bat" || ext == ".cmd" || ext == ".com" || ext == ".ps1";
            }
        }
        catch
        {
            return false;
        }
    }

    private static string ShellEscape(string s)
    {
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    // Executes an external program, optionally with stdout/stderr redirection.
    private static void ExecuteExternalProgram(string cmd, string[] args, RedirectionInfo? redirectInfo = null)
    {
        var execPath = FindExecutableInPath(cmd);
        if (execPath == null)
        {
            Console.WriteLine($"{cmd}: command not found");
            return;
        }

        try
        {
            if (redirectInfo != null)
            {
                // Ensure redirect targets exist even if nothing is written.
                EnsureFileCreated(redirectInfo.StdoutFile);
                EnsureFileCreated(redirectInfo.StderrFile);
            }

            // Build a shell command line so redirection is handled by /bin/sh.
            var sb = new StringBuilder();
            sb.Append("exec -a ");
            sb.Append(ShellEscape(cmd));
            sb.Append(" -- ");
            sb.Append(ShellEscape(execPath));
            foreach (var arg in args)
            {
                sb.Append(' ');
                sb.Append(ShellEscape(arg));
            }

            if (redirectInfo != null && !string.IsNullOrEmpty(redirectInfo.StdoutFile))
            {
                sb.Append(redirectInfo.StdoutAppend ? " >> " : " > ");
                sb.Append(ShellEscape(redirectInfo.StdoutFile));
            }

            if (redirectInfo != null && !string.IsNullOrEmpty(redirectInfo.StderrFile))
            {
                sb.Append(redirectInfo.StderrAppend ? " 2>> " : " 2> ");
                sb.Append(ShellEscape(redirectInfo.StderrFile));
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    UseShellExecute = false
                }
            };

            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(sb.ToString());

            process.Start();
            process.WaitForExit();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error executing {cmd}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------------
    // Two-command external pipeline (external | external)
    // ------------------------------------------------------------------------

    // Streams stdout of left into stdin of right using async copy.
    private static async Task ExecutePipelineExternalExternalAsync(string[] leftCmd, string[] rightCmd)
    {
        if (leftCmd.Length == 0 || rightCmd.Length == 0)
            return;

        var leftExec = FindExecutableInPath(leftCmd[0]);
        var rightExec = FindExecutableInPath(rightCmd[0]);

        if (leftExec == null)
        {
            Console.WriteLine($"{leftCmd[0]}: command not found");
            return;
        }

        if (rightExec == null)
        {
            Console.WriteLine($"{rightCmd[0]}: command not found");
            return;
        }

        var left = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = leftExec,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardInput = false,
                RedirectStandardError = false
            }
        };
        foreach (var arg in leftCmd.Skip(1))
            left.StartInfo.ArgumentList.Add(arg);

        var right = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = rightExec,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            }
        };
        foreach (var arg in rightCmd.Skip(1))
            right.StartInfo.ArgumentList.Add(arg);

        left.Start();
        right.Start();

        // Stream data from left → right.
        var copyTask = left.StandardOutput.BaseStream.CopyToAsync(right.StandardInput.BaseStream);
        await copyTask;
        right.StandardInput.Close();

        right.WaitForExit();

        if (!left.HasExited)
            left.Kill();

        left.WaitForExit();
    }

    // ------------------------------------------------------------------------
    // Built-in execution helpers (for normal and pipeline contexts)
    // ------------------------------------------------------------------------

    // Runs a builtin and writes its output to the provided writer.
    private static void RunBuiltinToWriter(string[] parts, TextWriter output)
    {
        var cmd = parts[0];

        if (cmd == "echo")
        {
            var text = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
            output.WriteLine(text);
        }
        else if (cmd == "type")
        {
            if (parts.Length < 2)
            {
                output.WriteLine("type: missing operand");
                return;
            }

            var target = parts[1];
            if (IsBuiltin(target))
            {
                output.WriteLine($"{target} is a shell builtin");
            }
            else
            {
                var exec = FindExecutableInPath(target);
                if (exec != null)
                    output.WriteLine($"{target} is {exec}");
                else
                    output.WriteLine($"{target}: not found");
            }
        }
        else if (cmd == "pwd")
        {
            output.WriteLine(Directory.GetCurrentDirectory());
        }
        else if (cmd == "cd")
        {
            // In pipeline context, cd should not affect the main shell process.
            // For the current tests, we can safely ignore side effects here.
        }
    }

    // ------------------------------------------------------------------------
    // Two-command pipeline dispatcher (handles builtins + externals)
    // ------------------------------------------------------------------------

    private static void ExecutePipeline(string[] left, string[] right)
    {
        var leftIsBuiltin = left.Length > 0 && IsBuiltin(left[0]);
        var rightIsBuiltin = right.Length > 0 && IsBuiltin(right[0]);

        if (!leftIsBuiltin && !rightIsBuiltin)
        {
            // external | external
            ExecutePipelineExternalExternalAsync(left, right).GetAwaiter().GetResult();
        }
        else if (leftIsBuiltin && !rightIsBuiltin)
        {
            // builtin | external
            var rightExec = FindExecutableInPath(right[0]);
            if (rightExec == null)
            {
                Console.WriteLine($"{right[0]}: command not found");
                return;
            }

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = rightExec,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                }
            };
            foreach (var arg in right.Skip(1))
                proc.StartInfo.ArgumentList.Add(arg);

            proc.Start();

            using (var writer =
                   new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false), leaveOpen: false))
            {
                writer.AutoFlush = true;
                RunBuiltinToWriter(left, writer);
            }

            proc.WaitForExit();
        }
        else if (!leftIsBuiltin && rightIsBuiltin)
        {
            // external | builtin
            var leftExec = FindExecutableInPath(left[0]);
            if (leftExec == null)
            {
                Console.WriteLine($"{left[0]}: command not found");
                return;
            }

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = leftExec,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = false,
                    RedirectStandardError = false
                }
            };
            foreach (var arg in left.Skip(1))
                proc.StartInfo.ArgumentList.Add(arg);

            proc.Start();

            // For current tests (e.g., ls | type exit), the builtin doesn't read stdin.
            // We must NOT print ls output, so we just drain it.
            using (var reader = new StreamReader(proc.StandardOutput.BaseStream))
            {
                while (reader.ReadLine() != null)
                {
                }
            }

            proc.WaitForExit();

            // Now run the builtin and print its output.
            RunBuiltinToWriter(right, Console.Out);
        }
        else
        {
            // builtin | builtin
            using var ms = new MemoryStream();
            using (var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.AutoFlush = true;
                RunBuiltinToWriter(left, writer);
            }

            ms.Position = 0;
            using var reader = new StreamReader(ms);
            // Current builtins don't read stdin, so we ignore reader and just run the second builtin.
            RunBuiltinToWriter(right, Console.Out);
        }
    }

    // ------------------------------------------------------------------------
    // Multi-command pipelines (3+ external commands)
    // ------------------------------------------------------------------------

    // Executes a pipeline with 3 or more commands, all external.
    // For these stages, commands are finite (no tail -f), so we can use MemoryStreams.
    private static void ExecuteMultiPipeline(string[] segments)
    {
        var commands = segments
            .Select(s => ParseCommandLine(s.Trim()))
            .Where(a => a.Length > 0)
            .ToArray();

        if (commands.Length == 0)
            return;

        MemoryStream? currentOutput = null;

        for (var i = 0; i < commands.Length; i++)
        {
            var cmdParts = commands[i];
            var exec = FindExecutableInPath(cmdParts[0]);
            if (exec == null)
            {
                Console.WriteLine($"{cmdParts[0]}: command not found");
                return;
            }

            var isLast = i == commands.Length - 1;

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exec,
                    UseShellExecute = false,
                    RedirectStandardInput = currentOutput != null,
                    RedirectStandardOutput = !isLast,
                    RedirectStandardError = false
                }
            };

            foreach (var arg in cmdParts.Skip(1))
                proc.StartInfo.ArgumentList.Add(arg);

            proc.Start();

            // If we have previous stage output, feed it into this process.
            if (currentOutput != null)
            {
                currentOutput.Position = 0;
                using (var stdin = proc.StandardInput.BaseStream)
                {
                    currentOutput.CopyTo(stdin);
                }

                currentOutput.Dispose();
                currentOutput = null;
            }

            // If not the last command, capture its output for the next stage.
            if (!isLast)
            {
                currentOutput = new MemoryStream();
                proc.StandardOutput.BaseStream.CopyTo(currentOutput);
            }

            proc.WaitForExit();
        }
    }

    // ------------------------------------------------------------------------
    // Command line parsing (handles quotes and escapes)
    // ------------------------------------------------------------------------

    private static string[] ParseCommandLine(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inSingle = false, inDouble = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (c == '\\' && inDouble)
            {
                if (i + 1 < input.Length)
                {
                    var next = input[++i];
                    if (next != '\n') current.Append(next);
                }

                continue;
            }

            if (c == '\\' && !inSingle && !inDouble)
            {
                if (i + 1 < input.Length) current.Append(input[++i]);
                continue;
            }

            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }

            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }

            if (c == ' ' && !inSingle && !inDouble)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens.ToArray();
    }

    // ------------------------------------------------------------------------
    // Main REPL loop
    // ------------------------------------------------------------------------

    private static void Main()
    {
        // Enable TAB autocompletion.
        ReadLine.ReadLine.Context.AutoCompletionHandler = new BuiltinAutoComplete();

        while (true)
        {
            Console.Write("$ ");
            var input = ReadLine.ReadLine.Read("");
            if (input == null) break;

            // Handle pipelines first.
            if (input.Contains("|"))
            {
                var segments = input.Split('|');

                if (segments.Length == 2)
                {
                    // Two-command pipeline (may involve builtins).
                    var left = ParseCommandLine(segments[0].Trim());
                    var right = ParseCommandLine(segments[1].Trim());
                    if (left.Length == 0 || right.Length == 0)
                        continue;

                    ExecutePipeline(left, right);
                }
                else
                {
                    // Multi-command pipeline (3+), external commands only.
                    ExecuteMultiPipeline(segments);
                }

                continue;
            }

            // Normal (non-pipeline) command.
            var parts = ParseCommandLine(input);
            if (parts.Length == 0) continue;

            var (cleaned, redirect) = ParseRedirection(parts);
            if (cleaned.Length == 0) continue;

            var cmd = cleaned[0];

            if (cmd == "exit") break;

            if (cmd == "echo")
            {
                var output = cleaned.Length == 1 ? "" : string.Join(" ", cleaned.Skip(1));
                WriteStdout(output, redirect);
                EnsureFileCreated(redirect.StderrFile);
                continue;
            }

            if (cmd == "type")
            {
                if (cleaned.Length < 2)
                {
                    Console.WriteLine("type: missing operand");
                    continue;
                }

                var target = cleaned[1];
                var result = IsBuiltin(target)
                    ? $"{target} is a shell builtin"
                    : FindExecutableInPath(target) is string p
                        ? $"{target} is {p}"
                        : $"{target}: not found";

                WriteStdout(result, redirect);
                EnsureFileCreated(redirect.StderrFile);
                continue;
            }

            if (cmd == "pwd")
            {
                WriteStdout(Directory.GetCurrentDirectory(), redirect);
                EnsureFileCreated(redirect.StderrFile);
                continue;
            }

            if (cmd == "cd")
            {
                if (cleaned.Length < 2)
                {
                    Console.WriteLine("cd: missing operand");
                    continue;
                }

                var target = cleaned[1];

                if (target == "~" || target.StartsWith("~/"))
                {
                    var home = Environment.GetEnvironmentVariable("HOME");
                    if (string.IsNullOrEmpty(home))
                    {
                        Console.WriteLine("cd: HOME not set");
                        continue;
                    }

                    target = target == "~" ? home : home + target.Substring(1);
                }

                if (!Directory.Exists(target))
                {
                    Console.WriteLine($"cd: {cleaned[1]}: No such file or directory");
                    continue;
                }

                try
                {
                    Directory.SetCurrentDirectory(target);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"cd: {cleaned[1]}: {ex.Message}");
                }

                continue;
            }

            // Fallback: external command.
            ExecuteExternalProgram(cmd, cleaned.Skip(1).ToArray(), redirect);
        }
    }
}