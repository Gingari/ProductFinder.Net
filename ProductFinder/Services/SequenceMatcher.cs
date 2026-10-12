using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace ProductFinder.Services;

internal sealed class SequenceMatcher
{
    private int[] _first = Array.Empty<int>();
    private readonly int[] _second;
    private readonly Dictionary<int, List<int>> _positions = new();
    private readonly Dictionary<int, int> _counts = new();
    private readonly CancellationToken _token;

    public SequenceMatcher(string first, string second, CancellationToken token = default)
    {
        _token = token;
        _second = Scalars(second);
        SetFirst(first);
        for (var j = 0; j < _second.Length; j++)
        {
            if (!_positions.TryGetValue(_second[j], out var positions)) _positions[_second[j]] = positions = new();
            positions.Add(j);
            _counts[_second[j]] = _counts.GetValueOrDefault(_second[j]) + 1;
        }
        if (_second.Length >= 200)
            foreach (var key in _positions.Where(p => p.Value.Count > _second.Length / 100 + 1).Select(p => p.Key).ToArray())
                _positions.Remove(key);
    }

    public void SetFirst(string value) => _first = Scalars(value);
    private static int[] Scalars(string value) => value.EnumerateRunes().Select(r => r.Value).ToArray();
    private double Calculate(int matches) => _first.Length + _second.Length == 0 ? 1 : 2.0 * matches / (_first.Length + _second.Length);
    public double RealQuickRatio() => Calculate(Math.Min(_first.Length, _second.Length));
    public double QuickRatio()
    {
        var available = new Dictionary<int, int>(_counts);
        var matches = 0;
        foreach (var scalar in _first)
        {
            var remaining = available.GetValueOrDefault(scalar);
            available[scalar] = remaining - 1;
            if (remaining > 0) matches++;
        }
        return Calculate(matches);
    }
    public double Ratio()
    {
        if (_first.AsSpan().SequenceEqual(_second)) return 1;
        var queue = new Stack<(int ALo, int AHi, int BLo, int BHi)>();
        queue.Push((0, _first.Length, 0, _second.Length));
        var blocks = new List<(int A, int B, int Size)>();
        while (queue.TryPop(out var bounds))
        {
            _token.ThrowIfCancellationRequested();
            var block = FindLongest(bounds.ALo, bounds.AHi, bounds.BLo, bounds.BHi);
            if (block.Size == 0) continue;
            blocks.Add(block);
            if (bounds.ALo < block.A && bounds.BLo < block.B)
                queue.Push((bounds.ALo, block.A, bounds.BLo, block.B));
            if (block.A + block.Size < bounds.AHi && block.B + block.Size < bounds.BHi)
                queue.Push((block.A + block.Size, bounds.AHi, block.B + block.Size, bounds.BHi));
        }
        return Calculate(blocks.Sum(b => b.Size));
    }
    private (int A, int B, int Size) FindLongest(int aLo, int aHi, int bLo, int bHi)
    {
        var bestA = aLo;
        var bestB = bLo;
        var bestSize = 0;
        var previous = new Dictionary<int, int>();
        for (var i = aLo; i < aHi; i++)
        {
            _token.ThrowIfCancellationRequested();
            var current = new Dictionary<int, int>();
            if (_positions.TryGetValue(_first[i], out var positions))
            {
                foreach (var j in positions)
                {
                    if (j < bLo) continue;
                    if (j >= bHi) break;
                    var size = previous.GetValueOrDefault(j - 1) + 1;
                    current[j] = size;
                    if (size <= bestSize) continue;
                    bestA = i - size + 1;
                    bestB = j - size + 1;
                    bestSize = size;
                }
            }
            previous = current;
        }
        while (bestA > aLo && bestB > bLo && _first[bestA - 1] == _second[bestB - 1])
        { bestA--; bestB--; bestSize++; }
        while (bestA + bestSize < aHi && bestB + bestSize < bHi && _first[bestA + bestSize] == _second[bestB + bestSize]) bestSize++;
        return (bestA, bestB, bestSize);
    }
}
