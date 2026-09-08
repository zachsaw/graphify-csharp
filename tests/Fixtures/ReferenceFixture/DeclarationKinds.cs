using ServiceAlias = ReferenceFixture.Production.Service;

namespace ReferenceFixture.AllDeclarations;

public enum CrossEnum
{
    None = (int)Production.BaseKind.None,
    Selected = (int)Production.BaseKind.Selected,
}

public record RecordDeclaration(int Id)
{
    public int Value => Id;
}

public readonly record struct ValueRecord(int Value);

public interface ITransform<T>
{
    T Transform(T value);
}

public struct StructDeclaration
{
    public int FirstField;
    public int SecondField;
    public int Property { get; set; }
}

public class DeclarationHost<T> : ITransform<T>
    where T : new()
{
    public const int Constant = 1;
    public static readonly int StaticField = 2;
    public int FirstField = 3, SecondField = 4;

    public static event EventHandler? StaticChanged;

    public event EventHandler? Changed;

    public event EventHandler? Custom
    {
        add => Changed += value;
        remove => Changed -= value;
    }

    public int Property { get; private set; }

    public T this[int index]
    {
        get => new T();
        set => Property = index;
    }

    public DeclarationHost()
    {
    }

    public DeclarationHost(int value)
    {
        Property = value;
    }

    ~DeclarationHost()
    {
    }

    public T Transform(T value) => value;

    public U Generic<U>(U value)
        where U : RecordDeclaration
        => value;

    public static DeclarationHost<T> operator +(DeclarationHost<T> left, DeclarationHost<T> right) => left;

    public static explicit operator int(DeclarationHost<T> value) => value.Property;

    public void Execute(ref int reference, out int output, in int input, params int[] rest)
    {
        const int localConstant = 1;
        int first = localConstant, second = 2;
        using var stream = new System.IO.MemoryStream();

        foreach (var item in new[] { first, second })
        {
            second += item;
        }

        for (var index = 0; index < rest.Length; index++)
        {
            second += rest[index];
        }

        switch (first)
        {
            case 0:
                second++;
                break;
            default:
                second--;
                break;
        }

        try
        {
            if (first is int patternValue)
            {
                second += patternValue;
            }

            if ((first, second) is (var left, var right))
            {
                second = left + right;
            }

            if (int.TryParse("1", out var parsed))
            {
                second += parsed;
            }
        }
        catch (Exception exception)
        {
            second = exception.HResult;
        }

        if (first < 0)
        {
            goto finished;
        }

    finished:
        var alias = new ServiceAlias();
        second += alias.Called(first);

        var lambda = (int lambdaParameter) => lambdaParameter + second;
        second += lambda(0);

        Func<int, int> implicitLambda = implicitParameter => implicitParameter + second;
        second += implicitLambda(0);

        var namedTuple = (First: first, Second: second);
        second += namedTuple.First;

        var anonymous = new { Value = first };
        second += anonymous.Value;

        var query =
            from item in new[] { first, second }
            let doubled = item * 2
            join other in new[] { first } on item equals other into matches
            from match in matches
            select doubled + match;
        second += query.FirstOrDefault();

        static int LocalFunction(int value) => value;

        output = LocalFunction(second) + input + reference + stream.Capacity;
        reference = output;
        Changed?.Invoke(this, EventArgs.Empty);
        StaticChanged?.Invoke(this, EventArgs.Empty);
    }
}
