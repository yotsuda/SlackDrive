using System.Collections;
using System.Management.Automation.Provider;

namespace SlackDrive;

public class SlackContentReader : IContentReader
{
    private readonly string[] _lines;
    private int _currentLine;

    public SlackContentReader(string markdown)
    {
        _lines = markdown.Split('\n');
        _currentLine = 0;
    }

    public IList Read(long readCount)
    {
        var result = new List<string>();
        var count = readCount <= 0 ? _lines.Length : (int)readCount;

        while (_currentLine < _lines.Length && result.Count < count)
        {
            result.Add(_lines[_currentLine]);
            _currentLine++;
        }

        return result;
    }

    public void Seek(long offset, SeekOrigin origin)
    {
        switch (origin)
        {
            case SeekOrigin.Begin:
                _currentLine = (int)offset;
                break;
            case SeekOrigin.Current:
                _currentLine += (int)offset;
                break;
            case SeekOrigin.End:
                _currentLine = _lines.Length + (int)offset;
                break;
        }
        _currentLine = Math.Max(0, Math.Min(_currentLine, _lines.Length));
    }

    public void Close() { }
    public void Dispose() { GC.SuppressFinalize(this); }
}
