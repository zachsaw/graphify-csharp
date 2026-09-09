namespace CSharp15Fixture;

public record class Cat(string Name);

public record class Dog(string Name);

public record class Bird(string Name);

public union Pet(Cat, Dog, Bird);

public union Result<T>(T, System.Exception);

public static class UnionConsumer
{
    public static string Describe(Pet pet) => pet switch
    {
        Cat cat => cat.Name,
        Dog dog => dog.Name,
        Bird bird => bird.Name,
    };

    public static string DescribeResult<T>(Result<T> result) => result switch
    {
        System.Exception exception => exception.Message,
        null => "no result",
        _ => "value",
    };
}
