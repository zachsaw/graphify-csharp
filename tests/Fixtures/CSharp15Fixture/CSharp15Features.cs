using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CSharp15Fixture;

public closed record class ClosedState;

public sealed record class Closed : ClosedState;

public record class Open(int Percent) : ClosedState;

public static class ClosedHierarchyConsumer
{
    public static string Describe(ClosedState state) => state switch
    {
        Closed => "closed",
        Open open => open.Percent.ToString(),
    };
}

public static class SequenceIndexerExtensions
{
    extension(IEnumerable<int> sequence)
    {
        public int this[int index] => Enumerate(sequence, index);

        private static int Enumerate(IEnumerable<int> values, int index) =>
            values is IList<int> list ? list[index] : System.Linq.Enumerable.ElementAt(values, index);
    }
}

public static class ExtensionIndexerConsumer
{
    public static int Read(IEnumerable<int> values) => values[0];
}

[CollectionBuilder(typeof(BufferedValuesBuilder), nameof(BufferedValuesBuilder.Create))]
public sealed class BufferedValues : IEnumerable<int>
{
    public IEnumerator<int> GetEnumerator() =>
        ((IEnumerable<int>)System.Array.Empty<int>()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class BufferedValuesBuilder
{
    public static BufferedValues Create(int capacity, ReadOnlySpan<int> values) => new();
}

public static class CollectionArgumentConsumer
{
    public static BufferedValues Create(int capacity, int first) =>
        [with(capacity), first];

    public static CapacityValues CreateWithConstructor(int capacity, int first) =>
        [with(capacity), first];
}

public sealed class CapacityValues : IEnumerable<int>
{
    public CapacityValues(int capacity)
    {
    }

    public void Add(int value)
    {
    }

    public IEnumerator<int> GetEnumerator() =>
        ((IEnumerable<int>)System.Array.Empty<int>()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class LabeledJumpConsumer
{
    public static int Scan(int[][] grid)
    {
        var result = 0;
        outer: for (var row = 0; row < grid.Length; row++)
        {
            for (var column = 0; column < grid[row].Length; column++)
            {
                if (grid[row][column] < 0)
                {
                    continue outer;
                }

                if (grid[row][column] == 0)
                {
                    break outer;
                }

                result++;
            }
        }

        return result;
    }
}

public static class MemorySafetyConsumer
{
    [StructLayout(LayoutKind.Explicit)]
    public struct ExplicitLayoutValue
    {
        [FieldOffset(0)]
        public safe int Value;
    }

    [DllImport("libc")]
    public static extern safe int GetProcessId();

    public struct NativeValue
    {
        public int Value;
    }

    public static int ReadPointer()
    {
        var number = 42;
        int* pointer = &number;
        return unsafe(*pointer);
    }

    public static int ReadFixed(int[] values)
    {
        fixed (int* first = values)
        {
            return unsafe(*first);
        }
    }

    public static int SizeOfInt() => sizeof(int);

    public static int SizeOfNativeValue() => sizeof(NativeValue);
}
