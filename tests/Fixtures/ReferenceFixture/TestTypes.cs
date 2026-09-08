namespace ReferenceFixture.Tests;

public static class TestCaller
{
    public static void Run()
    {
        var service = new Production.Service();
        _ = service.Called("from tests");
    }
}
