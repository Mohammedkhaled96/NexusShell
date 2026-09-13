namespace NexusShell.App.Models
{
    public class ChatMessage
    {
        public string Role    { get; set; } = "";
        public string Content { get; set; } = "";
        public bool   IsUser  { get; set; }

        public string SpeakerName { get; set; } = "";

        /// <summary>
        /// Screen-reader label that identifies the speaker.
        /// </summary>
        public string SpeechLabel => $"{SpeakerName} said: {Content}";
    }
}
