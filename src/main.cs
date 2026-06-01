using System.Diagnostics;
using System.Text;
using ReadLine;

internal class Program
{
    // ========================================================================
    //  REGION: Builtins + History
    // ========================================================================

    #region Builtins

    private static readonly string[] Builtins = { "echo", "exit", "type", "pwd", "cd", "history", "declare", "complete" };

    private static readonly List<string> CommandHistory = new();
    private static int HistoryAppendCursor = 0;
    private static readonly Dictionary<string, string> ShellVariables = new();
    private static readonly Dictionary<string, string> CompletionSpecs = new();

    private static bool IsBuiltin(string cmd) => Builtins.Contains(cmd);

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        // First character must be a letter or underscore
        if (!char.IsLetter(name[0]) && name[0] != '_')
            return false;

        // Rest must be letters, digits, or underscores
        for (int i = 1; i < name.Length; i++)
        {
            if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
                return false;
        }

        return true;
    }

    // Expand variables inside a single token. Supports $NAME and ${NAME} forms.
    private static string ExpandToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return token;

        var sb = new StringBuilder();
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if (c != '$')
            {
                sb.Append(c);
                continue;
            }

            // Found '$'
            if (i + 1 >= token.Length)
            {
                // Trailing $, keep as literal
                sb.Append('$');
                continue;
            }

            char next = token[i + 1];
            if (next == '{')
            {
                // ${NAME}
                int end = token.IndexOf('}', i + 2);
                if (end == -1)
                {
                    // No closing brace, treat literally
                    sb.Append("${");
                    i += 1; // move past '{'
                    continue;
                }

                string name = token.Substring(i + 2, end - (i + 2));
                if (ShellVariables.TryGetValue(name, out var val))
                    sb.Append(val);
                // else append nothing (unset -> empty)

                i = end; // move past '}'
                continue;
            }

            // $NAME where NAME starts with letter or underscore
            if (char.IsLetter(next) || next == '_')
            {
                int j = i + 1;
                while (j < token.Length && (char.IsLetterOrDigit(token[j]) || token[j] == '_')) j++;
                string name = token.Substring(i + 1, j - (i + 1));
                if (ShellVariables.TryGetValue(name, out var val))
                    sb.Append(val);
                // else append nothing

                i = j - 1;
                continue;
            }

            // Not a valid variable name, leave the dollar sign as-is
            sb.Append('$');
        }

        return sb.ToString();
    }

    // Expand an array of tokens. Drops any tokens that expand to empty strings.
    private static List<string> ExpandWords(string[] tokens)
    {
        var result = new List<string>();
        foreach (var t in tokens)
        {
            string expanded = ExpandToken(t);
            if (!string.IsNullOrEmpty(expanded))
            {
                result.Add(expanded);
            }
        }

        return result;
    }

    private static string ExpandParameters(string token)
    {
        var result = new StringBuilder();
        int i = 0;

        while (i < token.Length)
        {
            if (token[i] == '$' && i + 1 < token.Length)
            {
                if (token[i + 1] == '{')
                {
                    int endBrace = token.IndexOf('}', i + 2);
                    if (endBrace > i + 2)
                    {
                        string varName = token.Substring(i + 2, endBrace - (i + 2));
                        if (IsValidIdentifier(varName) && ShellVariables.TryGetValue(varName, out var braceValue))
                            result.Append(braceValue);

                        i = endBrace + 1;
                        continue;
                    }

                    result.Append(token[i]);
                    i++;
                    continue;
                }

                // Check if next character starts a valid identifier
                int j = i + 1;
                if (char.IsLetter(token[j]) || token[j] == '_')
                {
                    // Extract the variable name
                    while (j < token.Length && (char.IsLetterOrDigit(token[j]) || token[j] == '_'))
                        j++;

                    string varName = token.Substring(i + 1, j - i - 1);

                    // Look up the variable
                    if (ShellVariables.TryGetValue(varName, out var value))
                    {
                        result.Append(value);
                    }

                    i = j;
                    continue;
                }
            }

            result.Append(token[i]);
            i++;
        }

        return result.ToString();
    }

    private static List<string> ExpandAndSplitArguments(string[] parts)
    {
        var result = new List<string>();

        foreach (var p in parts)
        {
            // Expand parameters in the token. Do NOT perform additional word-splitting
            // here so that quoted tokens that contain spaces remain a single argument.
            string expanded = ExpandParameters(p);
            // If expansion yields an empty string, drop the resulting word
            if (!string.IsNullOrEmpty(expanded))
                result.Add(expanded);
        }

        return result;
    }

    private static void AddToHistory(string entry)
    {
        // Never store empty lines
        if (string.IsNullOrWhiteSpace(entry))
            return;

        CommandHistory.Add(entry);
        ReadLine.ReadLine.Context.History.Add(entry);
    }

    private static void LoadHistoryFromFile(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    AddToHistory(line);
            }

            HistoryAppendCursor = CommandHistory.Count;
        }
        catch
        {
        }
    }

    private static void WriteHistoryToFile(string path)
    {
        try
        {
            using var sw = new StreamWriter(path, false, new UTF8Encoding(false));

            foreach (var line in CommandHistory)
                sw.WriteLine(line);
        }
        catch
        {
        }
    }

    private static void AppendHistoryToFile(string path)
    {
        try
        {
            using var sw = new StreamWriter(path, true, new UTF8Encoding(false));

            for (int i = HistoryAppendCursor; i < CommandHistory.Count; i++)
                sw.WriteLine(CommandHistory[i]);

            HistoryAppendCursor = CommandHistory.Count;
        }
        catch
        {
        }
    }

    private static void RunBuiltinToWriter(string[] parts, TextWriter output)
    {
        string cmd = parts[0];

        switch (cmd)
        {
            case "echo":
                var expandedEchoArgs = parts.Length > 1 ? ExpandAndSplitArguments(parts.Skip(1).ToArray()).ToArray() : Array.Empty<string>();
                output.WriteLine(expandedEchoArgs.Length > 0 ? string.Join(" ", expandedEchoArgs) : "");
                break;

            case "pwd":
                output.WriteLine(Directory.GetCurrentDirectory());
                break;

            case "type":
                if (parts.Length < 2)
                {
                    output.WriteLine("type: missing operand");
                    return;
                }

                string target = ExpandParameters(parts[1]);
                if (IsBuiltin(target))
                    output.WriteLine($"{target} is a shell builtin");
                else if (FindExecutableInPath(target) is string p)
                    output.WriteLine($"{target} is {p}");
                else
                    output.WriteLine($"{target}: not found");
                break;

            case "history":
                HandleHistoryBuiltin(parts, output);
                break;

            case "declare":
                HandleDeclareBuiltin(parts, output);
                break;

            case "cd":
                // cd inside pipelines does nothing
                break;

            case "complete":
                if (parts.Length > 3 && parts[1] == "-C")
                {
                    string scriptPath = ExpandParameters(parts[2]);
                    string cmdName = ExpandParameters(parts[3]);
                    CompletionSpecs[cmdName] = scriptPath;
                }
                else if (parts.Length > 2 && parts[1] == "-r")
                {
                    string completeName = ExpandParameters(parts[2]);
                    CompletionSpecs.Remove(completeName);
                }
                else if (parts.Length > 2 && parts[1] == "-p")
                {
                    string completeName = ExpandParameters(parts[2]);
                    if (CompletionSpecs.TryGetValue(completeName, out var spec))
                        output.WriteLine($"complete -C '{spec}' {completeName}");
                    else
                        output.WriteLine($"complete: {completeName}: no completion specification");
                }
                break;
        }
    }

    private static void HandleHistoryBuiltin(string[] parts, TextWriter output)
    {
        if (parts.Length > 2 && parts[1] == "-a")
        {
            AppendHistoryToFile(parts[2]);
            return;
        }

        if (parts.Length > 2 && parts[1] == "-w")
        {
            WriteHistoryToFile(parts[2]);
            return;
        }

        if (parts.Length > 2 && parts[1] == "-r")
        {
            LoadHistoryFromFile(parts[2]);
            return;
        }

        int limit = CommandHistory.Count;
        if (parts.Length > 1 && int.TryParse(parts[1], out int n) && n > 0)
            limit = Math.Min(n, CommandHistory.Count);

        int start = CommandHistory.Count - limit;

        for (int i = start; i < CommandHistory.Count; i++)
            output.WriteLine($"    {i + 1}  {CommandHistory[i]}");
    }

    private static void HandleDeclareBuiltin(string[] parts, TextWriter output)
    {
        // Handle declare -p variable_name
        if (parts.Length > 2 && parts[1] == "-p")
        {
            string varName = ExpandParameters(parts[2]);
            if (ShellVariables.TryGetValue(varName, out var value))
            {
                output.WriteLine($"declare -- {varName}=\"{value}\"");
            }
            else
            {
                output.WriteLine($"declare: {varName}: not found");
            }
            return;
        }

        // Handle declare NAME=VALUE
        if (parts.Length > 1)
        {
            string arg = parts[1];
            int eqIdx = arg.IndexOf('=');
            if (eqIdx > 0)
            {
                string varName = arg.Substring(0, eqIdx);
                string varValue = arg.Substring(eqIdx + 1);

                // Expand parameters in the value
                varValue = ExpandParameters(varValue);

                // Validate variable name
                if (!IsValidIdentifier(varName))
                {
                    output.WriteLine($"declare: `{arg}': not a valid identifier");
                    return;
                }

                ShellVariables[varName] = varValue;
            }
        }
    }

    #endregion

    // ========================================================================
    //  REGION: Autocompletion
    // ========================================================================

    #region Autocompletion

    private class BuiltinAutoComplete : IAutoCompleteHandler
    {
        public char[] Separators { get; set; } = new[] { ' ' };
        private static readonly string[] AutoBuiltins = { "echo", "exit", "history", "declare", "complete" };

        private string? lastPrefix = null;
        private string? lastCompleterPrefix = null;

        private static string LCP(string[] arr)
        {
            if (arr.Length == 0) return "";
            if (arr.Length == 1) return arr[0];

            string prefix = arr[0];
            for (int i = 1; i < arr.Length; i++)
            {
                int j = 0;
                while (j < prefix.Length && j < arr[i].Length && prefix[j] == arr[i][j])
                    j++;
                prefix = prefix[..j];
                if (prefix == "") break;
            }

            return prefix;
        }

        public string[] GetSuggestions(string text, int index)
        {
            var parts = text.Split(' ');
            string current = parts[0];

            // ---------------------------------------------------------------
            // Argument completion (parts.Length > 1): complete filenames
            // ---------------------------------------------------------------
            if (parts.Length > 1)
            {
                // Check if a completer script is registered for this command
                if (CompletionSpecs.TryGetValue(current, out var completerScript))
                {
                    try
                    {
                        // Determine the word being completed (the text after the last space)
                        string wordBeingCompleted = parts.Length > 0 ? parts[^1] : "";
                        string previousWord = parts.Length > 1 ? parts[^2] : "";

                        var p = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = completerScript,
                                UseShellExecute = false,
                                RedirectStandardOutput = true
                            }
                        };

                        // Pass the full command line and cursor byte position only to the completer process.
                        p.StartInfo.Environment["COMP_LINE"] = text;
                        p.StartInfo.Environment["COMP_POINT"] = Encoding.UTF8.GetByteCount(text).ToString();

                        // Pass argv[1]=command name, argv[2]=word being completed, argv[3]=previous word (or empty)
                        p.StartInfo.ArgumentList.Add(current);
                        p.StartInfo.ArgumentList.Add(wordBeingCompleted);
                        p.StartInfo.ArgumentList.Add(previousWord);

                        p.Start();
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit();

                        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.Trim())
                            .Where(l => !string.IsNullOrEmpty(l))
                            .OrderBy(l => l, StringComparer.Ordinal)
                            .ToArray();
                        if (lines.Length == 0)
                        {
                            // No candidates: ring bell and leave input unchanged
                            lastCompleterPrefix = null;
                            Console.Write("\x07");
                            return Array.Empty<string>();
                        }

                        if (lines.Length == 1)
                        {
                            lastCompleterPrefix = null;
                            return new[] { lines[0] + " " };
                        }

                        if (lines.Length > 1)
                        {
                            string lcp = LCP(lines);

                            // If the candidates share a prefix longer than what the user typed,
                            // complete to that LCP immediately and wait for further input.
                            if (lcp.Length > wordBeingCompleted.Length)
                            {
                                lastCompleterPrefix = null;
                                return new[] { lcp };
                            }

                            // Multiple candidates: first TAB rings the bell, second TAB prints them.
                            if (lastCompleterPrefix != text)
                            {
                                lastCompleterPrefix = text;
                                Console.Write("\x07");
                                return Array.Empty<string>();
                            }

                            Console.WriteLine();
                            Console.WriteLine(string.Join("  ", lines));
                            Console.Write("$ " + text);
                            lastCompleterPrefix = null;
                        }
                    }
                    catch
                    {
                        // If script execution fails, ring the bell and leave input unchanged
                        lastCompleterPrefix = null;
                        Console.Write("\x07");
                        return Array.Empty<string>();
                    }

                    return Array.Empty<string>();
                }

                string filePrefix = parts[^1]; // partial filename typed so far

                try
                {
                    // Determine the directory to search and the name prefix
                    string searchDir;
                    string namePrefix;

                    if (string.IsNullOrEmpty(filePrefix))
                    {
                        searchDir = ".";
                        namePrefix = "";
                    }
                    else
                    {
                        string? dirPart = Path.GetDirectoryName(filePrefix);
                        searchDir = string.IsNullOrEmpty(dirPart) ? "." : dirPart;
                        namePrefix = Path.GetFileName(filePrefix);
                    }

                    var matches = Directory.GetFileSystemEntries(searchDir)
                        .Select(e => Path.GetFileName(e)!)
                        .Where(name => name.StartsWith(namePrefix, StringComparison.Ordinal))
                        .OrderBy(name => name)
                        .ToArray();

                    if (matches.Length == 1)
                    {
                        // Build the completed argument, preserving any directory prefix
                        string completedArg = (searchDir == ".")
                            ? matches[0]
                            : Path.Combine(searchDir, matches[0]);

                        // Return only the completed filename + trailing space.
                        // ReadLine replaces the current word (after last separator) with this value.
                        return new[] { completedArg + " " };
                    }

                    // No unique match: ring the bell and leave the line unchanged.
                    Console.Write("\x07");
                }
                catch
                {
                    // If directory enumeration fails, ring the bell and leave the line unchanged.
                    Console.Write("\x07");
                }

                return Array.Empty<string>();
            }

            // ---------------------------------------------------------------
            // Command completion (first word)
            // ---------------------------------------------------------------
            var builtinMatches = AutoBuiltins.Where(b => b.StartsWith(current)).ToArray();
            if (builtinMatches.Length == 1)
                return new[] { builtinMatches[0] + " " };
            if (builtinMatches.Length > 1)
            {
                Console.Write("\x07");
                return Array.Empty<string>();
            }

            {
                var suggestions = new List<string>();
                string? pathEnv = Environment.GetEnvironmentVariable("PATH");

                if (!string.IsNullOrEmpty(pathEnv))
                {
                    foreach (var dir in pathEnv.Split(Path.PathSeparator))
                    {
                        if (!Directory.Exists(dir)) continue;

                        try
                        {
                            foreach (var file in Directory.GetFiles(dir))
                            {
                                string name = Path.GetFileName(file);
                                if (name.StartsWith(current) && IsExecutable(file))
                                    suggestions.Add(name);
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                suggestions.Sort();
                if (suggestions.Count == 0)
                {
                    Console.Write("\x07");
                    return Array.Empty<string>();
                }

                string lcp = LCP(suggestions.ToArray());
                if (lcp.Length > current.Length)
                    return new[] { suggestions.Count == 1 ? lcp + " " : lcp };

                if (suggestions.Count == 1)
                    return new[] { suggestions[0] + " " };

                if (lastPrefix != current)
                {
                    lastPrefix = current;
                    Console.Write("\x07");
                    return Array.Empty<string>();
                }

                Console.WriteLine();
                Console.WriteLine(string.Join("  ", suggestions));
                Console.Write("$ " + current);
                lastPrefix = null;
                return Array.Empty<string>();
            }
        }
    }

    #endregion

    // ========================================================================
    //  REGION: Redirection
    // ========================================================================

    #region Redirection

    private class RedirectionInfo
    {
        public string? StdoutFile { get; set; }
        public bool StdoutAppend { get; set; }
        public string? StderrFile { get; set; }
        public bool StderrAppend { get; set; }
    }

    private static (string[], RedirectionInfo) ParseRedirection(string[] tokens)
    {
        var cleaned = new List<string>();
        var info = new RedirectionInfo();

        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i];

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

    private static void WriteStdout(string text, RedirectionInfo redirect)
    {
        if (!string.IsNullOrEmpty(redirect.StdoutFile))
        {
            try
            {
                using var sw = new StreamWriter(
                    new FileStream(redirect.StdoutFile,
                        redirect.StdoutAppend ? FileMode.Append : FileMode.Create,
                        FileAccess.Write));
                sw.WriteLine(text);
            }
            catch
            {
            }
        }
        else
        {
            Console.WriteLine(text);
        }
    }

    private static void EnsureFileCreated(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            using var _ = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write);
        }
        catch
        {
        }
    }

    #endregion

    // ========================================================================
    //  REGION: External Execution
    // ========================================================================

    #region ExternalExecution

    private static string? FindExecutableInPath(string cmd)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                string full = Path.Combine(dir, cmd);
                if (File.Exists(full) && IsExecutable(full))
                    return full;
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool IsExecutable(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (info.Attributes.HasFlag(FileAttributes.Directory))
                return false;

            if (!OperatingSystem.IsWindows())
            {
                var mode = info.UnixFileMode;
                return (mode & (UnixFileMode.UserExecute |
                                UnixFileMode.GroupExecute |
                                UnixFileMode.OtherExecute)) != 0;
            }

            string ext = Path.GetExtension(filePath).ToLower();
            return ext is ".exe" or ".bat" or ".cmd" or ".com" or ".ps1";
        }
        catch
        {
            return false;
        }
    }

    private static string ShellEscape(string s) =>
        "'" + s.Replace("'", "'\\''") + "'";

    private static void ExecuteExternalProgram(string cmd, string[] args, RedirectionInfo? redirect)
    {
        string? execPath = FindExecutableInPath(cmd);
        if (execPath == null)
        {
            Console.WriteLine($"{cmd}: command not found");
            return;
        }

        try
        {
            if (redirect != null)
            {
                EnsureFileCreated(redirect.StdoutFile);
                EnsureFileCreated(redirect.StderrFile);
            }

            var sb = new StringBuilder();
            sb.Append("exec -a ").Append(ShellEscape(cmd)).Append(" -- ").Append(ShellEscape(execPath));

            foreach (var arg in args)
                sb.Append(' ').Append(ShellEscape(arg));

            if (redirect?.StdoutFile != null)
                sb.Append(redirect.StdoutAppend ? " >> " : " > ").Append(ShellEscape(redirect.StdoutFile));

            if (redirect?.StderrFile != null)
                sb.Append(redirect.StderrAppend ? " 2>> " : " 2> ").Append(ShellEscape(redirect.StderrFile));

            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    UseShellExecute = false
                }
            };

            p.StartInfo.ArgumentList.Add("-c");
            p.StartInfo.ArgumentList.Add(sb.ToString());

            p.Start();
            p.WaitForExit();
        }
        catch
        {
        }
    }

    #endregion

    // ========================================================================
    //  REGION: Pipelines
    // ========================================================================

    #region Pipelines

    private static async Task ExecutePipelineExternalExternalAsync(string[] left, string[] right)
    {
        string? leftExec = FindExecutableInPath(left[0]);
        string? rightExec = FindExecutableInPath(right[0]);

        if (leftExec == null)
        {
            Console.WriteLine($"{left[0]}: command not found");
            return;
        }

        if (rightExec == null)
        {
            Console.WriteLine($"{right[0]}: command not found");
            return;
        }

        var p1 = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = leftExec,
                UseShellExecute = false,
                RedirectStandardOutput = true
            }
        };
        foreach (var arg in left.Skip(1))
            p1.StartInfo.ArgumentList.Add(arg);

        var p2 = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = rightExec,
                UseShellExecute = false,
                RedirectStandardInput = true
            }
        };
        foreach (var arg in right.Skip(1))
            p2.StartInfo.ArgumentList.Add(arg);

        p1.Start();
        p2.Start();

        await p1.StandardOutput.BaseStream.CopyToAsync(p2.StandardInput.BaseStream);
        p2.StandardInput.Close();

        p2.WaitForExit();
        if (!p1.HasExited) p1.Kill();
        p1.WaitForExit();
    }

    private static void ExecutePipeline(string[] left, string[] right)
    {
        bool leftBuiltin = IsBuiltin(left[0]);
        bool rightBuiltin = IsBuiltin(right[0]);

        if (!leftBuiltin && !rightBuiltin)
        {
            ExecutePipelineExternalExternalAsync(left, right).GetAwaiter().GetResult();
            return;
        }

        if (leftBuiltin && !rightBuiltin)
        {
            string? exec = FindExecutableInPath(right[0]);
            if (exec == null)
            {
                Console.WriteLine($"{right[0]}: command not found");
                return;
            }

            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exec,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false
                }
            };

            foreach (var arg in right.Skip(1))
                p.StartInfo.ArgumentList.Add(arg);

            p.Start();

            // Write builtin output into wc stdin
            using (var writer = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                writer.AutoFlush = true;
                RunBuiltinToWriter(left, writer);
            }

            // Wait for wc to finish
            p.WaitForExit();
            return;
        }

        if (!leftBuiltin && rightBuiltin)
        {
            string? exec = FindExecutableInPath(left[0]);
            if (exec == null)
            {
                Console.WriteLine($"{left[0]}: command not found");
                return;
            }

            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exec,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                }
            };
            foreach (var arg in left.Skip(1))
                p.StartInfo.ArgumentList.Add(arg);

            p.Start();

            using (var reader = new StreamReader(p.StandardOutput.BaseStream))
                while (reader.ReadLine() != null)
                {
                }

            p.WaitForExit();

            RunBuiltinToWriter(right, Console.Out);
            return;
        }

        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.AutoFlush = true;
            RunBuiltinToWriter(left, writer);
        }

        ms.Position = 0;
        RunBuiltinToWriter(right, Console.Out);
    }

    private static void ExecuteMultiPipeline(string[] segments)
    {
        var commands = segments.Select(s => ParseCommandLine(s.Trim()))
            .Where(a => a.Length > 0)
            .Select(a => ExpandAndSplitArguments(a).ToArray())
            .ToArray();

        MemoryStream? prev = null;

        for (int i = 0; i < commands.Length; i++)
        {
            var cmd = commands[i];
            string? exec = FindExecutableInPath(cmd[0]);
            if (exec == null)
            {
                Console.WriteLine($"{cmd[0]}: command not found");
                return;
            }

            bool last = i == commands.Length - 1;

            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exec,
                    UseShellExecute = false,
                    RedirectStandardInput = prev != null,
                    RedirectStandardOutput = !last
                }
            };

            foreach (var arg in cmd.Skip(1))
                p.StartInfo.ArgumentList.Add(arg);

            p.Start();

            if (prev != null)
            {
                prev.Position = 0;
                prev.CopyTo(p.StandardInput.BaseStream);
                p.StandardInput.Close();
                prev.Dispose();
                prev = null;
            }

            if (!last)
            {
                prev = new MemoryStream();
                p.StandardOutput.BaseStream.CopyTo(prev);
            }

            p.WaitForExit();
        }
    }

    #endregion

    // ========================================================================
    //  REGION: Parsing
    // ========================================================================

    #region Parsing

    private static string[] ParseCommandLine(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inSingle = false;
        bool inDouble = false;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            if (c == '\\' && inDouble)
            {
                if (i + 1 < input.Length)
                {
                    char next = input[++i];
                    if (next != '\n')
                        current.Append(next);
                }

                continue;
            }

            if (c == '\\' && !inSingle && !inDouble)
            {
                if (i + 1 < input.Length)
                    current.Append(input[++i]);
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

            // Space ends token (only when not inside quotes)
            if (c == ' ' && !inSingle && !inDouble)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            // Normal character
            current.Append(c);
        }

        // Add last token
        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens.ToArray();
    }

    #endregion

    // ========================================================================
    //  REGION: Main Loop (REPL)
    // ========================================================================
    #region MainLoop

    private static void Main()
    {
        // Enable TAB autocompletion only when input is not redirected (interactive)
        if (!Console.IsInputRedirected)
            ReadLine.ReadLine.Context.AutoCompletionHandler = new BuiltinAutoComplete();

        // Load history from HISTFILE on startup
        string? histfile = Environment.GetEnvironmentVariable("HISTFILE");
        if (!string.IsNullOrEmpty(histfile) && File.Exists(histfile))
            LoadHistoryFromFile(histfile);

        while (true)
        {
            Console.Write("$ ");
            string? input;
            if (Console.IsInputRedirected)
            {
                // When tests pipe commands into stdin, ReadLine echoes the input back
                // which causes duplicated lines in test output. Use Console.ReadLine
                // to avoid that behavior in non-interactive mode.
                input = Console.ReadLine();
            }
            else
            {
                input = ReadLine.ReadLine.Read("");
            }
            if (input == null)
                break;

            if (string.IsNullOrWhiteSpace(input))
                continue;

            // Always add to history (including exit)
            AddToHistory(input);

            // -------------------------
            // PIPELINES
            // -------------------------
            if (input.Contains("|"))
            {
                var segments = input.Split('|');

                if (segments.Length == 2)
                {
                    var left = ParseCommandLine(segments[0].Trim());
                    var right = ParseCommandLine(segments[1].Trim());

                    var leftExpanded = ExpandAndSplitArguments(left).ToArray();
                    var rightExpanded = ExpandAndSplitArguments(right).ToArray();

                    if (leftExpanded.Length > 0 && rightExpanded.Length > 0)
                        ExecutePipeline(leftExpanded, rightExpanded);
                }
                else
                {
                    ExecuteMultiPipeline(segments);
                }

                continue;
            }

            // -------------------------
            // NORMAL COMMAND
            // -------------------------
            var parts = ParseCommandLine(input);
            if (parts.Length == 0)
                continue;

            var (cleaned, redirect) = ParseRedirection(parts);
            if (cleaned.Length == 0)
                continue;

            // Expand redirection targets as they may contain parameters
            if (!string.IsNullOrEmpty(redirect.StdoutFile))
                redirect.StdoutFile = ExpandParameters(redirect.StdoutFile);
            if (!string.IsNullOrEmpty(redirect.StderrFile))
                redirect.StderrFile = ExpandParameters(redirect.StderrFile);

            var expandedList = ExpandAndSplitArguments(cleaned);
            if (expandedList.Count == 0)
                continue;

            string cmd = expandedList[0];

            // -------------------------
            // EXIT (write history!)
            // -------------------------
            if (cmd == "exit")
            {
                if (!string.IsNullOrEmpty(histfile))
                    WriteHistoryToFile(histfile);

                break;
            }

            // -------------------------
            // BUILTINS
            // -------------------------
            if (cmd == "echo")
            {
                string output = expandedList.Skip(1).Any() ? string.Join(" ", expandedList.Skip(1)) : "";
                WriteStdout(output, redirect);
                EnsureFileCreated(redirect.StderrFile);
                continue;
            }

            if (cmd == "pwd")
            {
                WriteStdout(Directory.GetCurrentDirectory(), redirect);
                EnsureFileCreated(redirect.StderrFile);
                continue;
            }

            if (cmd == "type")
            {
                if (expandedList.Count < 2)
                {
                    Console.WriteLine("type: missing operand");
                    continue;
                }

                string target = expandedList[1];
                string result =
                    IsBuiltin(target) ? $"{target} is a shell builtin" :
                    FindExecutableInPath(target) is string p ? $"{target} is {p}" :
                    $"{target}: not found";

                WriteStdout(result, redirect);
                EnsureFileCreated(redirect.StderrFile);
                continue;
            }

            if (cmd == "cd")
            {
                if (expandedList.Count < 2)
                {
                    Console.WriteLine("cd: missing operand");
                    continue;
                }

                string target = expandedList[1];

                if (target == "~" || target.StartsWith("~/"))
                {
                    string? home = Environment.GetEnvironmentVariable("HOME");
                    if (string.IsNullOrEmpty(home))
                    {
                        Console.WriteLine("cd: HOME not set");
                        continue;
                    }

                    target = target == "~" ? home : home + target.Substring(1);
                }

                if (!Directory.Exists(target))
                {
                    Console.WriteLine($"cd: {expandedList[1]}: No such file or directory");
                    continue;
                }

                try { Directory.SetCurrentDirectory(target); }
                catch (Exception ex) { Console.WriteLine($"cd: {expandedList[1]}: {ex.Message}"); }

                continue;
            }

            if (cmd == "history")
            {
                RunBuiltinToWriter(expandedList.ToArray(), Console.Out);
                continue;
            }

            if (cmd == "declare")
            {
                RunBuiltinToWriter(expandedList.ToArray(), Console.Out);
                continue;
            }

            if (cmd == "complete")
            {
                if (expandedList.Count > 3 && expandedList[1] == "-C")
                {
                    CompletionSpecs[expandedList[3]] = expandedList[2];
                }
                else if (expandedList.Count > 2 && expandedList[1] == "-r")
                {
                    CompletionSpecs.Remove(expandedList[2]);
                }
                else if (expandedList.Count > 2 && expandedList[1] == "-p")
                {
                    string completeName = expandedList[2];
                    if (CompletionSpecs.TryGetValue(completeName, out var spec))
                        Console.WriteLine($"complete -C '{spec}' {completeName}");
                    else
                        Console.WriteLine($"complete: {completeName}: no completion specification");
                }
                continue;
            }

            // -------------------------
            // EXTERNAL COMMAND
            // -------------------------
            var externalArgs = expandedList.Skip(1).ToArray();
            ExecuteExternalProgram(cmd, externalArgs, redirect);
        }
    }

    #endregion
}