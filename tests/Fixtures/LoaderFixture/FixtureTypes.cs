namespace LoaderFixture;

public sealed class Service
{
    static Service()
    {
    }

    public string Run(int value) => value.ToString();

    public string Run(string value) => value;

    public sealed class Nested<T>
    {
        public T Echo(T value) => value;
    }
}
