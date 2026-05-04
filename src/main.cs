using System.Diagnostics;
using System.Text;

class Program
{
    static readonly string[] Builtins = { "echo", "exit", "type", "pwd", "cd" };

    static string? FindExecutableInPath(string cmd)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var directories = pathEnv.Split(Path.PathSeparator);

        foreach (var dir in directories)
        {
            if (string.IsNullOrEmpty(dir)) continue;

            try
            {
                var fullPath = Path.Combine(dir, cmd);
                if (!File.Exists(fullPath)) continue;
                if (IsExecutable(fullPath)) return fullPath;
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
            if (fileInfo.Attributes.HasFlag(FileAttributes.Directory)) return false;

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
        catch (Exception)
        {
            return false;
        }
    }

    // ── RedirectionInfo ───────────────────────────────────────────────────────
    // CHANGED: Added StderrAppend flag, mirroring StdoutAppend.
    class RedirectionInfo
    {
        public string? StdoutFile { get; set; }
        public bool StdoutAppend { get; set; } // true = >>,  false = >
        public string? StderrFile { get; set; }
        public bool StderrAppend { get; set; } // true = 2>>, false = 2>
    }

    static bool IsBuiltin(string cmd)
    {
        return Array.Exists(Builtins, element => element == cmd);
    }

    // ── ParseRedirection ──────────────────────────────────────────────────────
    // CHANGED: Recognises 2>> in addition to all previous operators.
    static (string[], RedirectionInfo) ParseRedirection(string[] tokens)
    {
        var cleanedTokens = new List<string>();
        var redirectInfo = new RedirectionInfo();

        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];

            if ((token == ">>" || token == "1>>") && i + 1 < tokens.Length)
            {
                redirectInfo.StdoutFile = tokens[i + 1];
                redirectInfo.StdoutAppend = true;
                i++;
            }
            else if ((token == ">" || token == "1>") && i + 1 < tokens.Length)
            {
                redirectInfo.StdoutFile = tokens[i + 1];
                redirectInfo.StdoutAppend = false;
                i++;
            }
            // Append stderr: 2>>
            else if (token == "2>>" && i + 1 < tokens.Length)
            {
                redirectInfo.StderrFile = tokens[i + 1];
                redirectInfo.StderrAppend = true; // preserve existing content
                i++;
            }
            // Overwrite stderr: 2>
            else if (token == "2>" && i + 1 < tokens.Length)
            {
                redirectInfo.StderrFile = tokens[i + 1];
                redirectInfo.StderrAppend = false; // truncate existing content
                i++;
            }
            else
            {
                cleanedTokens.Add(token);
            }
        }

        return (cleanedTokens.ToArray(), redirectInfo);
    }

    // ── WriteStdout ───────────────────────────────────────────────────────────
    static void WriteStdout(string text, RedirectionInfo redirect)
    {
        if (!string.IsNullOrEmpty(redirect.StdoutFile))
        {
            try
            {
                var mode = redirect.StdoutAppend ? FileMode.Append : FileMode.Create;
                using var sw = new StreamWriter(new FileStream(redirect.StdoutFile, mode, FileAccess.Write));
                sw.WriteLine(text);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error writing to file: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine(text);
        }
    }

    // Always create a redirect target file even if nothing is written to it.
    static void EnsureFileCreated(string? path)
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

    static void ExecuteExternalProgram(string cmd, string[] args, RedirectionInfo? redirectInfo = null)
    {
        var execPath = FindExecutableInPath(cmd);
        if (string.IsNullOrEmpty(execPath))
        {
            Console.WriteLine($"{cmd}: command not found");
            return;
        }

        try
        {
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

            // CHANGED: pass 2>> or 2> to the underlying shell based on StderrAppend
            if (redirectInfo != null && !string.IsNullOrEmpty(redirectInfo.StderrFile))
            {
                sb.Append(redirectInfo.StderrAppend ? " 2>> " : " 2> ");
                sb.Append(ShellEscape(redirectInfo.StderrFile));
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo { FileName = "/bin/sh", UseShellExecute = false, }
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

    static string ShellEscape(string s)
    {
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    static string[] ParseCommandLine(string input)
    {
        var tokens = new List<string>();
        var currentToken = new StringBuilder();
        bool inSingleQuotes = false;
        bool inDoubleQuotes = false;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            if (c == '\\' && inDoubleQuotes)
            {
                if (i + 1 < input.Length)
                {
                    char next = input[i + 1];
                    if (next == '"' || next == '\\' || next == '$' || next == '`' || next == '\n')
                    {
                        i++;
                        if (next != '\n') currentToken.Append(next);
                    }
                    else
                    {
                        currentToken.Append(c);
                    }
                }
                else
                {
                    currentToken.Append(c);
                }
            }
            else if (c == '\\' && !inSingleQuotes && !inDoubleQuotes)
            {
                if (i + 1 < input.Length)
                {
                    i++;
                    currentToken.Append(input[i]);
                }
                else
                {
                    currentToken.Append(c);
                }
            }
            else if (c == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
            }
            else if (c == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
            }
            else if (c == ' ' && !inSingleQuotes && !inDoubleQuotes)
            {
                if (currentToken.Length > 0)
                {
                    tokens.Add(currentToken.ToString());
                    currentToken.Clear();
                }
            }
            else
            {
                currentToken.Append(c);
            }
        }

        if (currentToken.Length > 0) tokens.Add(currentToken.ToString());
        return tokens.ToArray();
    }

    static void Main()
    {
        while (true)
        {
            Console.Write("$ ");

            var input = Console.ReadLine();
            if (input == null) break;

            var parts = ParseCommandLine(input);
            if (parts.Length == 0) continue;

            var (cleanedParts, redirectInfo) = ParseRedirection(parts);
            if (cleanedParts.Length == 0) continue;

            var cmd = cleanedParts[0];

            if (cmd == "exit") break;

            if (cmd == "echo")
            {
                string output = cleanedParts.Length == 1
                    ? ""
                    : string.Join(" ", cleanedParts, 1, cleanedParts.Length - 1);

                WriteStdout(output, redirectInfo);
                // echo never produces stderr — just ensure the file exists if specified
                EnsureFileCreated(redirectInfo.StderrFile);
                continue;
            }

            if (cmd == "type")
            {
                if (cleanedParts.Length < 2)
                {
                    Console.WriteLine("type: missing operand");
                    continue;
                }

                var targetCmd = cleanedParts[1];
                string typeOutput;
                if (IsBuiltin(targetCmd))
                    typeOutput = $"{targetCmd} is a shell builtin";
                else
                {
                    var execPath = FindExecutableInPath(targetCmd);
                    typeOutput = !string.IsNullOrEmpty(execPath)
                        ? $"{targetCmd} is {execPath}"
                        : $"{targetCmd}: not found";
                }

                WriteStdout(typeOutput, redirectInfo);
                EnsureFileCreated(redirectInfo.StderrFile);
                continue;
            }

            if (cmd == "pwd")
            {
                WriteStdout(Directory.GetCurrentDirectory(), redirectInfo);
                EnsureFileCreated(redirectInfo.StderrFile);
                continue;
            }

            if (cmd == "cd")
            {
                if (cleanedParts.Length < 2)
                {
                    Console.WriteLine("cd: missing operand");
                    continue;
                }

                var targetDir = cleanedParts[1];

                if (targetDir == "~" || targetDir.StartsWith("~/"))
                {
                    var homeDir = Environment.GetEnvironmentVariable("HOME");
                    if (string.IsNullOrEmpty(homeDir))
                    {
                        Console.WriteLine("cd: HOME not set");
                        continue;
                    }

                    targetDir = targetDir == "~" ? homeDir : homeDir + targetDir.Substring(1);
                }

                if (!Directory.Exists(targetDir))
                {
                    Console.WriteLine($"cd: {cleanedParts[1]}: No such file or directory");
                    continue;
                }

                try
                {
                    Directory.SetCurrentDirectory(targetDir);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"cd: {cleanedParts[1]}: {ex.Message}");
                }

                continue;
            }

            // External program
            var remainingArgs = cleanedParts.Length > 1 ? cleanedParts[1..] : Array.Empty<string>();
            ExecuteExternalProgram(cmd, remainingArgs, redirectInfo);
        }
    }
}