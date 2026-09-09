using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace LanguageSurfaceFixture;

public sealed class PatternValue : IDisposable, IAsyncDisposable
{
    public PatternValue(int value) => Value = value;

    public int Value { get; }

    public int Length => 1;

    public int this[int index] => Value;

    public int this[Index index] => Value;

    public PatternValue this[Range range] => this;

    public void Deconstruct(out int first, out int second)
    {
        first = Value;
        second = Value + 1;
    }

    public PatternValue Slice(int start, int length) => this;

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static PatternValue operator +(PatternValue left, PatternValue right) => new(left.Value + right.Value);

    public static PatternValue operator ++(PatternValue value) => new(value.Value + 1);

    public static PatternValue operator !(PatternValue value) => new(-value.Value);

    public static implicit operator int(PatternValue value) => value.Value;

    public static explicit operator PatternValue(int value) => new(value);

    public static bool operator ==(PatternValue left, PatternValue right) => left.Value == right.Value;

    public static bool operator !=(PatternValue left, PatternValue right) => left.Value != right.Value;

    public override bool Equals(object? obj) => obj is PatternValue other && this == other;

    public override int GetHashCode() => Value;

    public int Mutable { get; set; }

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

public sealed class SurfaceEnumerable
{
    public SurfaceEnumerator GetEnumerator() => new();
}

public sealed class SurfaceEnumerator : IEnumerator<int>
{
    private bool _returned;

    public int Current => 7;

    object IEnumerator.Current => Current;

    public bool MoveNext()
    {
        if (_returned)
        {
            return false;
        }

        _returned = true;
        return true;
    }

    public void Reset() => _returned = false;

    public void Dispose()
    {
    }
}

public sealed class AsyncSurfaceEnumerable
{
    public AsyncSurfaceEnumerator GetAsyncEnumerator() => new();
}

public sealed class AsyncSurfaceEnumerator
{
    private bool _returned;

    public int Current => 13;

    public AsyncMoveNextAwaitable MoveNextAsync()
    {
        var result = !_returned;
        _returned = true;
        return new(result);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public sealed class AsyncMoveNextAwaitable
    {
        private readonly bool _result;

        public AsyncMoveNextAwaitable(bool result) => _result = result;

        public AsyncMoveNextAwaiter GetAwaiter() => new(_result);
    }

    public sealed class AsyncMoveNextAwaiter : INotifyCompletion
    {
        private readonly bool _result;

        public AsyncMoveNextAwaiter(bool result) => _result = result;

        public bool IsCompleted => true;

        public void OnCompleted(Action continuation) => continuation();

        public bool GetResult() => _result;
    }
}

public sealed class SurfaceCollection
    : IEnumerable<int>
{
    public int Count { get; private set; }

    public void Add(int value) => Count += value;

    public IEnumerator<int> GetEnumerator() => new SurfaceEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class SurfaceResource : IDisposable
{
    public int Value => 3;

    public void Dispose()
    {
    }
}

public sealed class Awaitable
{
    public SurfaceAwaiter GetAwaiter() => new();
}

public sealed class SurfaceAwaiter : INotifyCompletion
{
    public bool IsCompleted => true;

    public void OnCompleted(Action continuation) => continuation();

    public int GetResult() => 11;
}

public sealed class AsyncResource : IAsyncDisposable
{
    public Awaitable GetValueAsync() => new();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public ref struct PinnableValue
{
    private int _value;

    public PinnableValue(int value) => _value = value;

    public ref int GetPinnableReference() => ref _value;
}

[InterpolatedStringHandler]
public ref struct SurfaceHandler
{
    public SurfaceHandler(int literalLength, int formattedCount)
    {
    }

    public void AppendLiteral(string value)
    {
    }

    public void AppendFormatted(int value)
    {
    }
}

public static class SurfaceHandlerConsumer
{
    public static void Consume(SurfaceHandler handler)
    {
    }

    public static void Use(int value) => Consume($"value={value}");
}

public sealed record SurfaceRecord(int Value);

public static class SurfaceConsumer
{
    public static int Operators(PatternValue value)
    {
        var sum = value + value;
        sum++;
        var inverse = !sum;
        var number = (int)sum;
        var restored = (PatternValue)number;
        return sum == restored ? inverse.Value : number;
    }

    public static int Deconstruction(PatternValue value)
    {
        var (first, second) = value;
        (first, second) = value;
        return first + second;
    }

    public static int Patterns(PatternValue value)
    {
        if (value is PatternValue { Value: var matched })
        {
            return matched;
        }

        if (value is [var listFirst, ..])
        {
            return listFirst;
        }

        return value switch
        {
            PatternValue(var first, var second) => first + second,
            _ => 0,
        };
    }

    public static int Enumeration(SurfaceEnumerable enumerable)
    {
        var result = 0;
        foreach (var item in enumerable)
        {
            result += item;
        }

        return result;
    }

    public static async ValueTask<int> AsyncEnumeration(AsyncSurfaceEnumerable enumerable)
    {
        await foreach (var item in enumerable)
        {
            return item;
        }

        return 0;
    }

    public static int Initializers()
    {
        var collection = new SurfaceCollection { 1, 2 };
        return collection.Count;
    }

    public static int Ranges(PatternValue value)
    {
        var index = value[^1];
        var slice = value[0..1];
        return index + slice.Value;
    }

    public static int Accessors(PatternValue value)
    {
        value.Mutable = value.Value;
        var mutable = value.Mutable;
        value.Changed += HandleChanged;
        value.Changed -= HandleChanged;
        return mutable;
    }

    private static void HandleChanged(object? sender, EventArgs args)
    {
    }

    public static int Using(SurfaceResource resource)
    {
        using (resource)
        {
            return resource.Value;
        }
    }

    public static int UsingDeclaration(SurfaceResource resource)
    {
        using var local = resource;
        return local.Value;
    }

    public static async ValueTask<int> Awaiting(Awaitable awaitable) => await awaitable;

    public static async ValueTask<int> AwaitUsing(AsyncResource resource)
    {
        await using (resource)
        {
            return await resource.GetValueAsync();
        }
    }

    public static async ValueTask<int> AwaitUsingDeclaration(AsyncResource resource)
    {
        await using var local = resource;
        return await local.GetValueAsync();
    }

    public static unsafe int FixedPattern(PinnableValue value)
    {
        fixed (int* pointer = value)
        {
            return *pointer;
        }
    }

    private static int PointerTarget(int value) => value;

    public static unsafe int FunctionPointer()
    {
        delegate*<int, int> pointer = &PointerTarget;
        return pointer(1);
    }

    public static SurfaceRecord With(SurfaceRecord record) => record with { Value = 2 };
}
