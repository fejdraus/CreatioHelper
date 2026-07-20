namespace CreatioHelper.Cli;

public class CliArgs
{
    public string? Command { get; set; }
    public string? SubCommand { get; set; }
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> MultiOptions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positional { get; } = new();

    public string? Get(string key) => Options.TryGetValue(key, out var v) ? v : null;
    public bool HasFlag(string key) => Flags.Contains(key);

    public string[] GetAll(string key) =>
        MultiOptions.TryGetValue(key, out var list) ? list.ToArray() : [];

    public static CliArgs Parse(string[] args)
    {
        var result = new CliArgs();
        int index = 0;

        if (args.Length > 0 && !args[0].StartsWith("-"))
        {
            result.Command = args[0];
            index = 1;
            if (args.Length > 1 && !args[1].StartsWith("-"))
            {
                result.SubCommand = args[1];
                index = 2;
            }
        }

        for (int i = index; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--"))
            {
                string token = a.Substring(2);
                int eq = token.IndexOf('=');
                if (eq >= 0)
                {
                    result.AddOption(token.Substring(0, eq), token.Substring(eq + 1));
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    result.AddOption(token, args[i + 1]);
                    i++;
                }
                else
                {
                    result.Flags.Add(token);
                }
            }
            else
            {
                result.Positional.Add(a);
            }
        }

        return result;
    }

    private void AddOption(string key, string value)
    {
        Options[key] = value;
        if (!MultiOptions.TryGetValue(key, out var list))
        {
            list = new List<string>();
            MultiOptions[key] = list;
        }
        list.Add(value);
    }
}
