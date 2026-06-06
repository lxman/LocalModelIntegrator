namespace LocalModelIntegrator.Models
{
    public class FileSuggestion
    {
        public string Path { get; set; }
        public string Content { get; set; }

        public FileSuggestion(string path, string content)
        {
            Path = path;
            Content = content;
        }
    }
}
