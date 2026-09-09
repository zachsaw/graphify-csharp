namespace ExtensionBlockFixture;

public static class DateOnlyExtensions
{
    extension((DateOnly? StartDate, DateOnly? EndDate) timeline)
    {
        public bool IsValid => timeline.StartDate <= timeline.EndDate;
    }
}
