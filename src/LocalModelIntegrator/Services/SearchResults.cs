using System.Collections.Generic;

namespace LocalModelIntegrator.Services
{
    /// <summary>A content-search hit within a solution file.</summary>
    public sealed class SearchMatch
    {
        public string DisplayPath { get; }
        public int Line { get; }   // 1-based
        public string Text { get; }

        public SearchMatch(string displayPath, int line, string text)
        {
            DisplayPath = displayPath;
            Line = line;
            Text = text;
        }
    }

    /// <summary>A symbol declaration located by name (Roslyn).</summary>
    public sealed class SymbolHit
    {
        public string Description { get; }   // e.g. "class MyApp.Services.OrderProcessor"
        public string FilePath { get; }
        public int Line { get; }             // 1-based

        public SymbolHit(string description, string filePath, int line)
        {
            Description = description;
            FilePath = filePath;
            Line = line;
        }
    }

    /// <summary>A single definition or reference location for a symbol (Roslyn).</summary>
    public sealed class ReferenceHit
    {
        public bool IsDefinition { get; }
        public string FilePath { get; }
        public int Line { get; }             // 1-based
        public string Snippet { get; }

        public ReferenceHit(bool isDefinition, string filePath, int line, string snippet)
        {
            IsDefinition = isDefinition;
            FilePath = filePath;
            Line = line;
            Snippet = snippet;
        }
    }

    /// <summary>Aggregated find-references result for a symbol name.</summary>
    public sealed class FindReferencesResult
    {
        public List<string> MatchedSymbols { get; } = new List<string>();
        public List<ReferenceHit> Hits { get; } = new List<ReferenceHit>();
        public bool Truncated { get; set; }
        public string Note { get; set; }     // set when nothing matched / invalid input
    }
}
