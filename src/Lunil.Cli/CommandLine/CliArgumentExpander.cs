using System.Text;

namespace Lunil.Cli.CommandLine;

internal static class CliArgumentExpander
{
    private const int MaximumDepth = 8;
    private const int MaximumArgumentCount = 4_096;
    private const int MaximumResponseFileBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyList<string> Expand(IEnumerable<string> arguments, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        var result = new List<string>();
        var stack = new HashSet<string>(GetPathComparer());
        ExpandCore(arguments, Path.GetFullPath(currentDirectory), 0, stack, result);
        return result;
    }

    private static void ExpandCore(
        IEnumerable<string> arguments,
        string baseDirectory,
        int depth,
        HashSet<string> stack,
        List<string> result)
    {
        if (depth > MaximumDepth)
        {
            throw new CliUsageException($"Response-file nesting exceeds {MaximumDepth} levels.");
        }

        foreach (var argument in arguments)
        {
            if (result.Count >= MaximumArgumentCount)
            {
                throw new CliUsageException($"Expanded argument count exceeds {MaximumArgumentCount}.");
            }

            if (argument.StartsWith("@@", StringComparison.Ordinal))
            {
                result.Add(argument[1..]);
                continue;
            }

            if (argument.Length <= 1 || argument[0] != '@')
            {
                result.Add(argument);
                continue;
            }

            var path = Path.GetFullPath(argument[1..], baseDirectory);
            if (!stack.Add(path))
            {
                throw new CliUsageException($"Response-file cycle detected at '{path}'.");
            }

            try
            {
                byte[] bytes;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        throw new FileNotFoundException("Response file was not found.", path);
                    }

                    if (info.Length > MaximumResponseFileBytes)
                    {
                        throw new CliUsageException(
                            $"Response file '{path}' exceeds {MaximumResponseFileBytes} bytes.");
                    }

                    bytes = File.ReadAllBytes(path);
                }
                catch (CliUsageException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or
                    UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    throw new CliUsageException(
                        $"Cannot read response file '{path}': {exception.Message}");
                }

                string text;
                try
                {
                    text = StrictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new CliUsageException(
                        $"Response file '{path}' is not valid UTF-8: {exception.Message}");
                }

                var nested = Tokenize(text, path);
                ExpandCore(
                    nested,
                    Path.GetDirectoryName(path) ?? baseDirectory,
                    depth + 1,
                    stack,
                    result);
            }
            finally
            {
                stack.Remove(path);
            }
        }
    }

    private static List<string> Tokenize(string text, string path)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        char? quote = null;
        var escaping = false;
        var inComment = false;
        var hasToken = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inComment)
            {
                if (character is '\r' or '\n')
                {
                    inComment = false;
                }

                continue;
            }

            if (escaping)
            {
                token.Append(character);
                hasToken = true;
                escaping = false;
                continue;
            }

            if (character == '\\' && index + 1 < text.Length &&
                IsEscapable(text[index + 1]))
            {
                escaping = true;
                hasToken = true;
                continue;
            }

            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }
                else
                {
                    token.Append(character);
                }

                hasToken = true;
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                hasToken = true;
                continue;
            }

            if (character == '#' && !hasToken)
            {
                inComment = true;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                FlushToken();
                continue;
            }

            token.Append(character);
            hasToken = true;
        }

        if (escaping)
        {
            token.Append('\\');
        }

        if (quote is not null)
        {
            throw new CliUsageException($"Response file '{path}' contains an unterminated quote.");
        }

        FlushToken();
        return result;

        void FlushToken()
        {
            if (!hasToken)
            {
                return;
            }

            result.Add(token.ToString());
            token.Clear();
            hasToken = false;
        }

        static bool IsEscapable(char character) =>
            character is '\\' or '\'' or '"' or '#' || char.IsWhiteSpace(character);
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
