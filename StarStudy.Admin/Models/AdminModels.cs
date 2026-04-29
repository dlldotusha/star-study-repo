namespace StarStudy.Admin.Models;

public static class AdminTestStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Closed = "closed";
}

public static class AdminRoles
{
    public const string Owner = "owner";
    public const string Reviewer = "reviewer";
    public const string Observer = "observer";
}

public sealed class AdminLoginData
{
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class AdminLoginResult
{
    public bool Success { get; set; }
    public string? Token { get; set; }
    public string Message { get; set; } = "";
    public string Role { get; set; } = AdminRoles.Observer;
}

public sealed class AdminTokenData
{
    public string Token { get; set; } = "";
}

public sealed class AdminTestRequest
{
    public string Token { get; set; } = "";
    public string TestId { get; set; } = "";
}

public sealed class AdminTestSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = AdminTestStatuses.Draft;
    public int QuestionCount { get; set; }
    public int AssignedStudentCount { get; set; }
    public int StartedCount { get; set; }
    public int FinishedCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AdminTestEdit
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int? TimeLimitMinutes { get; set; }
    public string Status { get; set; } = AdminTestStatuses.Draft;
    public int AttemptLimit { get; set; } = 1;
    public bool AllowResume { get; set; } = true;
    public bool AllowBackNavigation { get; set; } = true;
    public bool ShuffleQuestions { get; set; }
    public bool ShuffleOptions { get; set; }
    public bool ShowResultToStudent { get; set; }
    public bool ShowCorrectAnswers { get; set; }
    public List<AdminQuestionEdit> Questions { get; set; } = [];
    public List<AdminStudentAccessEdit> Students { get; set; } = [];
    public List<AdminGroupAccessEdit> Groups { get; set; } = [];
    public List<string> ValidationMessages { get; set; } = [];
}

public sealed class AdminSaveTestData
{
    public string Token { get; set; } = "";
    public string TestId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int? TimeLimitMinutes { get; set; }
    public int AttemptLimit { get; set; } = 1;
    public bool AllowResume { get; set; } = true;
    public bool AllowBackNavigation { get; set; } = true;
    public bool ShuffleQuestions { get; set; }
    public bool ShuffleOptions { get; set; }
    public bool ShowResultToStudent { get; set; }
    public bool ShowCorrectAnswers { get; set; }
}

public sealed class AdminCreateTestData
{
    public string Token { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int? TimeLimitMinutes { get; set; }
}

public sealed class AdminQuestionEdit
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "text";
    public string Text { get; set; } = "";
    public string? CorrectText { get; set; }
    public List<AdminOptionEdit> Options { get; set; } = [];
    public List<string> CorrectOptionIds { get; set; } = [];
    public int Points { get; set; } = 1;
    public string? ReviewerComment { get; set; }
}

public sealed class AdminSaveQuestionData
{
    public string Token { get; set; } = "";
    public string TestId { get; set; } = "";
    public AdminQuestionEdit Question { get; set; } = new();
}

public sealed class AdminOptionEdit
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class AdminStudentAccessEdit
{
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
    public DateTimeOffset? AvailableUntil { get; set; }
    public int AttemptLimit { get; set; } = 1;
    public bool AllowResume { get; set; } = true;
    public bool AllowBackNavigation { get; set; } = true;
}

public sealed class AdminSaveStudentData
{
    public string Token { get; set; } = "";
    public string TestId { get; set; } = "";
    public AdminStudentAccessEdit Student { get; set; } = new();
}

public sealed class AdminGroupEdit
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<AdminGroupStudentEdit> Students { get; set; } = [];
}

public sealed class AdminGroupStudentEdit
{
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class AdminSaveGroupData
{
    public string Token { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class AdminSaveGroupStudentData
{
    public string Token { get; set; } = "";
    public string GroupId { get; set; } = "";
    public AdminGroupStudentEdit Student { get; set; } = new();
}

public sealed class AdminGroupAccessEdit
{
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public int StudentCount { get; set; }
    public DateTimeOffset? AvailableUntil { get; set; }
    public int AttemptLimit { get; set; } = 1;
    public bool AllowResume { get; set; } = true;
    public bool AllowBackNavigation { get; set; } = true;
}

public sealed class AdminAssignGroupData
{
    public string Token { get; set; } = "";
    public string TestId { get; set; } = "";
    public AdminGroupAccessEdit Group { get; set; } = new();
}

public sealed class AdminPublishResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public List<string> ValidationMessages { get; set; } = [];
}

public sealed class AdminAttemptSummary
{
    public string AttemptId { get; set; } = "";
    public string StudentLogin { get; set; } = "";
    public string TestId { get; set; } = "";
    public string TestTitle { get; set; } = "";
    public string Status { get; set; } = "";
    public int CurrentQuestionNumber { get; set; }
    public int OpenedQuestionCount { get; set; }
    public int SubmittedAnswerCount { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset LastRequestAt { get; set; }
    public int TotalPoints { get; set; }
    public int MaxPoints { get; set; }
    public decimal Percent { get; set; }
}

public sealed class AdminAnswerReview
{
    public string QuestionId { get; set; } = "";
    public int Number { get; set; }
    public string QuestionText { get; set; } = "";
    public string Type { get; set; } = "";
    public string StudentAnswer { get; set; } = "";
    public string CorrectAnswer { get; set; } = "";
    public string ReviewStatus { get; set; } = "";
    public int Points { get; set; }
    public int MaxPoints { get; set; }
    public string? ReviewerComment { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
}

public sealed class AdminAttemptDetails
{
    public AdminAttemptSummary Summary { get; set; } = new();
    public List<string> Timeline { get; set; } = [];
    public List<AdminAnswerReview> Answers { get; set; } = [];
}

public sealed class AdminGradeData
{
    public string Token { get; set; } = "";
    public string AttemptId { get; set; } = "";
    public string QuestionId { get; set; } = "";
    public int Points { get; set; }
    public string? ReviewerComment { get; set; }
}

public sealed class AdminCsvExport
{
    public string FileName { get; set; } = "results.csv";
    public string Content { get; set; } = "";
}
