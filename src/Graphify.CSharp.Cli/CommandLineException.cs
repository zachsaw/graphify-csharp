namespace Graphify.CSharp.Cli;

public sealed class CommandLineException : Exception
{
    public CommandLineException(string message)
        : base(message)
    {
    }
}
