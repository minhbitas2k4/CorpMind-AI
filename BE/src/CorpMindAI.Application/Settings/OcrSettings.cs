namespace CorpMindAI.Application.Settings
{
    public class OcrSettings
    {
        public string ServiceUrl { get; set; } = "http://localhost:8000";
        public int TimeoutSeconds { get; set; } = 300;
    }
}
