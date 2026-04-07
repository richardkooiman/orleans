using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;

namespace Benchmarks.ObjectHeaderContention;

/// <summary>
/// Demonstrates the performance impact of using an object both as a dictionary key
/// (which uses the object header for hash code) and as a lock target (which also uses
/// the object header). This dual use causes contention on the object header and
/// significant performance degradation.
/// See: https://github.com/dotnet/orleans/issues/9874
/// See: https://devblogs.microsoft.com/premier-developer/managed-object-internals-part-2-object-header-layout-and-the-cost-of-locking/
/// </summary>
[MemoryDiagnoser]
public class ObjectHeaderContentionBenchmark
{
    private const int OperationCount = 1_000_000;
    private const int DictionarySize = 1000;

    private object[] _lockObjects = null!;
    private ConcurrentDictionary<object, bool> _dictionary = null!;
#if NET9_0_OR_GREATER
    private Lock[] _dedicatedLocks = null!;
#else
    private object[] _dedicatedLocks = null!;
#endif

    [GlobalSetup]
    public void Setup()
    {
        _lockObjects = new object[DictionarySize];
        _dictionary = new ConcurrentDictionary<object, bool>(ReferenceEqualityComparer.Instance);
#if NET9_0_OR_GREATER
        _dedicatedLocks = new Lock[DictionarySize];
#else
        _dedicatedLocks = new object[DictionarySize];
#endif

        for (var i = 0; i < DictionarySize; i++)
        {
            _lockObjects[i] = new object();
            _dictionary[_lockObjects[i]] = true;
#if NET9_0_OR_GREATER
            _dedicatedLocks[i] = new Lock();
#else
            _dedicatedLocks[i] = new object();
#endif
        }
    }

    /// <summary>
    /// Baseline: Lock on the object WITHOUT any dictionary key usage.
    /// This only uses the object header for locking.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int LockOnly()
    {
        var sum = 0;
        for (var i = 0; i < OperationCount; i++)
        {
            var obj = _lockObjects[i % DictionarySize];
            lock (obj)
            {
                sum++;
            }
        }
        return sum;
    }

    /// <summary>
    /// Problem case: Lock on the object AND use it as a dictionary key.
    /// Both operations contend on the object header, causing ~3.5x slowdown.
    /// This simulates the current ActivationData behavior.
    /// </summary>
    [Benchmark]
    public int LockAndGetHashCode()
    {
        var sum = 0;
        for (var i = 0; i < OperationCount; i++)
        {
            var obj = _lockObjects[i % DictionarySize];
            lock (obj)
            {
                // Dictionary lookup triggers RuntimeHelpers.GetHashCode via the object header
                if (_dictionary.TryGetValue(obj, out _))
                {
                    sum++;
                }
            }
        }
        return sum;
    }

    /// <summary>
    /// Fixed case: Lock on a dedicated Lock object, use original as dictionary key.
    /// The object header is only used for one purpose at a time.
    /// This simulates the proposed fix for ActivationData.
    /// </summary>
    [Benchmark]
    public int DedicatedLockAndGetHashCode()
    {
        var sum = 0;
        for (var i = 0; i < OperationCount; i++)
        {
            var obj = _lockObjects[i % DictionarySize];
            var lk = _dedicatedLocks[i % DictionarySize];
            lock (lk)
            {
                if (_dictionary.TryGetValue(obj, out _))
                {
                    sum++;
                }
            }
        }
        return sum;
    }
}
