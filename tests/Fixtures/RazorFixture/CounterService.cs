namespace RazorFixture;

public static class CounterService
{
    public static int Get(int count) => count;

    public static int Get(string value) => value.Length;
}
