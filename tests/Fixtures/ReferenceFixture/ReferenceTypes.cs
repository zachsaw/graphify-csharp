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
