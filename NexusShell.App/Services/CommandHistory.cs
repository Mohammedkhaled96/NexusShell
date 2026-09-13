using NexusShell.App.Interfaces;
using System.Collections.Generic;
using System.Linq;
namespace NexusShell.App.Services
{
    public class CommandHistory : ICommandHistory
    {
        private readonly List<string> _history = new List<string>();
        private int _position;
        public CommandHistory()
        {
            _position = 0;
        }
        public void Add(string command)
        {
            if (!string.IsNullOrWhiteSpace(command) && (_history.Count == 0 || _history.Last() != command))
            {
                _history.Add(command);
            }
            _position = _history.Count;
        }
        public string? GetPrevious()
        {
            if (_position > 0)
            {
                _position--;
                return _history[_position];
            }
            return _history.FirstOrDefault();
        }
        public IEnumerable<string> GetAll()
        {
            return _history;
        }

        public string GetNext()
        {
            if (_position < _history.Count - 1)
            {
                _position++;
                return _history[_position];
            }
            _position = _history.Count;
            return string.Empty;
        }
    }
}

