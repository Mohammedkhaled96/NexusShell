namespace NexusShell.App.Models
{
    public class EnvVariable
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;

        public EnvVariable() { }
        public EnvVariable(string name, string value)
        {
            Name = name;
            Value = value;
        }
    }
}
