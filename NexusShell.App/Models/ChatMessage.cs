namespace NexusShell.App.Models
{
    public class ChatMessage
    {
        public string Role    { get; set; } = "";
        public string Content { get; set; } = "";
        public bool   IsUser  { get; set; }

        public string SpeakerName { get; set; } = "";

        /// <summary>When the message was written (saved with the chat).</summary>
        public DateTimeOffset At { get; set; } = DateTimeOffset.Now;

        /// <summary>
        /// Screen-reader label that identifies the speaker.
        /// </summary>
        public string SpeechLabel => $"{SpeakerName} said: {Content}";
    }
}
