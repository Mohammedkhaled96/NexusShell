using NexusShell.App.Interfaces;
using System.Collections.Generic;
namespace NexusShell.App.Services
{
    public class OutputHistoryService : IOutputHistoryService
    {
        private readonly List<string> _history = new();
        private int _index = -1;
        public void Add(string output)
        {
            _history.Add(output);
            _index = _history.Count;
        }
        public string GetPrevious()
        {
            if (_history.Count == 0)
            {
                return string.Empty;
            }
            _index--;
            if (_index < 0)
            {
                _index = 0;
            }
            return _history[_index];
        }
        public string GetNext()
        {
            if (_history.Count == 0)
            {
                return string.Empty;
            }
            _index++;
            if (_index >= _history.Count)
            {
                _index = _history.Count - 1;
                return _history[_index];
            }
            return _history[_index];
        }
        public void Clear()
        {
            _history.Clear();
            _index = -1;
        }
    }
}


