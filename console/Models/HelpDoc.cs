namespace b1_chat_console.Models;

public class HelpManifest
{
    public string Title { get; set; } = "";
    public List<HelpSection> Sections { get; set; } = new();
}

public class HelpSection
{
    public string Title { get; set; } = "";
    public List<HelpPage> Pages { get; set; } = new();
}

public class HelpPage
{
    public string Title { get; set; } = "";
    public string File { get; set; } = "";
}
