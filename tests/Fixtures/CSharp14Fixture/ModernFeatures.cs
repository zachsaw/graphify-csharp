using System.Collections.Generic;

namespace CSharp14Fixture;

public sealed class FieldBackedPropertyHost
{
    public FieldBackedPropertyHost()
    {
        Value = string.Empty;
    }

    public string Value
    {
        get;
        set => field = value.Trim();
    }

    public FieldBackedPropertyHost? Child { get; set; }

    public void Assign(FieldBackedPropertyHost? host, int[]? values)
    {
        host?.Value = "updated";
        values?[0] = 1;
    }
}

public partial class PartialHost
{
    public partial PartialHost(int value);

    public partial PartialHost(int value)
    {
        Value = value;
    }

    public partial event EventHandler? Changed;

    public partial event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public int Value { get; }
}

public sealed class Counter
{
    public int Value { get; private set; }

    public void operator +=(int value) => Value += value;

    public void operator ++() => Value++;

    public static void Use(Counter counter)
    {
        counter += 1;
        counter++;
    }
}

public delegate bool TryParse<T>(string text, out T result);

public sealed class GenericNameTarget<T>
{
}

public static class ModernFeatures
{
    public const string UnboundGenericName = nameof(List<>);
    public const string OwnUnboundGenericName = nameof(GenericNameTarget<>);

    public static ReadOnlySpan<char> ToSpan(string text) => text;

    public static int UseExtensions(IEnumerable<int> values) => values.FirstValue();

    public static int Parse()
    {
        TryParse<int> parser = (text, out result) => int.TryParse(text, out result);
        return parser("1", out var result) ? result : 0;
    }
}

public static class GenericExtensions
{
    extension<T>(IEnumerable<T> values)
    {
        public bool IsEmpty => !values.GetEnumerator().MoveNext();

        public T FirstValue() => values.First();
    }

    extension<T>(IEnumerable<T>)
    {
        public static IEnumerable<T> Identity => Array.Empty<T>();

        public static IEnumerable<T> Combine(IEnumerable<T> first, IEnumerable<T> second) => first.Concat(second);

        public static IEnumerable<T> operator +(IEnumerable<T> left, IEnumerable<T> right) => left.Concat(right);
    }
}
