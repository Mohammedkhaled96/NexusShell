using System.Windows.Input;

namespace NexusShell.App.Models
{
    public class Shortcut
    {
        public Key Key { get; set; }
        public ModifierKeys Modifiers { get; set; }
        public string CommandName { get; set; }

        public Shortcut(Key key, ModifierKeys modifiers, string commandName)
        {
            Key = key;
            Modifiers = modifiers;
            CommandName = commandName;
        }

        public string DisplayString
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
                if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
                if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
                if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
                
                parts.Add(Key.ToString());
                return string.Join(" + ", parts);
            }
        }

        public override string ToString() => $"{CommandName}: {DisplayString}";
    }
}
