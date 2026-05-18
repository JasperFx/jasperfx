namespace EventTests;

public class LetterCounts
{
    public User? User { get; set; }
    public Session? Session { get; set; }

    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }
}

public class Session
{
}

public record Assigned(string UserName);

public record AssignedToUser(User User);

public record FullStop(bool ShouldStop);

public record StartLetters(int A, int B);
