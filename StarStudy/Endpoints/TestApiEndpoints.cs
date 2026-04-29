using StarStudy.Client.Models;
using StarStudy.Services;

namespace StarStudy.Endpoints;

public static class TestApiEndpoints
{
    public static void MapTestApi(this WebApplication app)
    {
        var student = app.MapGroup("/api/student");

        student.MapPost("/login", (LoginData data, TestSessionStore store) =>
            store.Login(data));

        student.MapPost("/state", (TokenData data, TestSessionStore store) =>
            store.GetState(data.Token));

        student.MapPost("/tests", (TokenData data, TestSessionStore store) =>
            store.GetTests(data.Token));

        var tests = app.MapGroup("/api/tests");

        tests.MapPost("/open", (TestRequest request, TestSessionStore store) =>
            store.OpenTest(request.Token, request.TestId));

        tests.MapPost("/start", (TestRequest request, TestSessionStore store) =>
            store.StartTest(request.Token, request.TestId));

        tests.MapPost("/question", (QuestionRequest request, TestSessionStore store) =>
        {
            var question = store.GetQuestion(request.Token, request.TestId, request.Number);
            return question is null ? Results.NotFound("Вопрос не найден или недоступен.") : Results.Ok(question);
        });

        tests.MapPost("/answer", (AnswerRequest request, TestSessionStore store) =>
            store.SubmitAnswer(request));

        tests.MapPost("/finish", (TestRequest request, TestSessionStore store) =>
            store.FinishTest(request.Token, request.TestId));
    }
}
