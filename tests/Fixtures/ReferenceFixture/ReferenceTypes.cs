namespace ReferenceFixture.Production;

public sealed class Service
{
    public int Called(int value) => value;

    public int Called(string value) => value.Length;

    public int MethodGroup(int value) => value;

    public int Property => 42;

    public int Field = 42;

    public int Unused => 0;
}

public sealed class ReferencedType
{
}

public interface IContract
{
    int Execute(int value);
}

public class BaseContract
{
    public virtual int Execute(int value) => value;
}

public sealed class Contract : BaseContract, IContract
{
    public override int Execute(int value) => base.Execute(value);
}

public static class ProductionCaller
{
    public static void Run()
    {
        var service = new Service();
        _ = service.Called(1);
        Func<int, int> action = service.MethodGroup;
        _ = action(2);
        _ = service.Property;
        _ = service.Field;
        _ = typeof(ReferencedType);
    }
}

public enum BaseKind
{
    None = 0,
    Selected = 7,
}

public enum DerivedKind
{
    None = (int)BaseKind.None,
    Selected = (int)BaseKind.Selected,
}

public delegate T Transformer<T>(T value);

public interface IGenericContract<T>
{
    T Convert(T value);
}

public abstract class GenericBase<T>
{
    public virtual T Convert(T value) => value;
}

public sealed class GenericContract<T> : GenericBase<T>, IGenericContract<T>
{
    public override T Convert(T value) => Local(value);

    private static T Local(T value) => value;

    public static T Run(T value)
    {
        return LocalFunction(value);

        static T LocalFunction(T localValue) => localValue;
    }
}
