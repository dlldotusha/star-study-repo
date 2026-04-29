namespace StarStudy.Client.Models;

public static class TestScreenNames
{
    public const string Login = "login";
    public const string Tests = "tests";
    public const string Intro = "intro";
    public const string Taking = "taking";
    public const string Finished = "finished";
}

public static class QuestionTypes
{
    public const string Text = "text";
    public const string SingleChoice = "single";
    public const string MultiChoice = "multi";
}

public sealed class LoginData
{
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class LoginResult
{
    public bool Success { get; set; }
    public string? Token { get; set; }
    public string Message { get; set; } = "";
}

public sealed class TokenData
{
    public string Token { get; set; } = "";
}

public sealed class TestRequest
{
    public string Token { get; set; } = "";
    public string TestId { get; set; } = "";
}

public sealed class QuestionRequest
{
    public string Token { get; set; } = "";
    public string TestId { get; set; } = "";
    public int Number { get; set; }
}

public sealed class AnswerRequest
{
    public string Token { get; set; } = "";
    public string TestId { get; set; } = "";
    public int Number { get; set; }
    public string QuestionId { get; set; } = "";
    public string? Text { get; set; }
    public List<string> SelectedOptionIds { get; set; } = [];
}

public sealed class TestInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    public string Description { get; set; } = "";
    public int TimeLimitMinutes { get; set; }
    public int QuestionCount { get; set; }
}

public sealed class StudentState
{
    public bool IsLoggedIn { get; set; }
    public string Screen { get; set; } = TestScreenNames.Login;
    public string? UserName { get; set; }
    public string? TestId { get; set; }
    public TestInfo? CurrentTest { get; set; }
    public int CurrentQuestionNumber { get; set; } = 1;
    public int TotalQuestions { get; set; }
    public List<int> AnsweredQuestionNumbers { get; set; } = [];
}

public sealed class Question
{
    public string Id { get; set; } = "";
    public int Number { get; set; }
    public string Type { get; set; } = QuestionTypes.Text;
    public string Text { get; set; } = "";
    public List<Option> Options { get; set; } = [];
    public UserAnswer? SavedAnswer { get; set; }
}

public sealed class Option
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class UserAnswer
{
    public string? Text { get; set; }
    public List<string> SelectedOptionIds { get; set; } = [];
}

public sealed class AnswerResult
{
    public bool Accepted { get; set; }
    public string Message { get; set; } = "";
    public bool IsFinished { get; set; }
    public List<int> AnsweredQuestionNumbers { get; set; } = [];
}
