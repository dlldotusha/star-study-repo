using StarStudy.Admin.Models;
using StarStudy.Services;

namespace StarStudy.Endpoints;

public static class AdminApiEndpoints
{
    public static void MapAdminApi(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin");

        admin.MapPost("/login", (AdminLoginData data, TestSessionStore store) =>
            store.AdminLogin(data));

        admin.MapPost("/tests", (AdminTokenData data, TestSessionStore store) =>
            store.GetAdminTests(data.Token));

        admin.MapPost("/test", (AdminTestRequest request, TestSessionStore store) =>
        {
            var test = store.GetAdminTest(request.Token, request.TestId);
            return test is null ? Results.NotFound("Тест не найден или недоступен.") : Results.Ok(test);
        });

        admin.MapPost("/test/create", (AdminCreateTestData data, TestSessionStore store) =>
        {
            var test = store.CreateAdminTest(data);
            return test is null ? Results.Forbid() : Results.Ok(test);
        });

        admin.MapPost("/test/save", (AdminSaveTestData data, TestSessionStore store) =>
        {
            var test = store.SaveAdminTest(data);
            return test is null ? Results.Forbid() : Results.Ok(test);
        });

        admin.MapPost("/question/save", (AdminSaveQuestionData data, TestSessionStore store) =>
        {
            var test = store.SaveAdminQuestion(data);
            return test is null ? Results.Forbid() : Results.Ok(test);
        });

        admin.MapPost("/student/save", (AdminSaveStudentData data, TestSessionStore store) =>
        {
            var test = store.SaveAdminStudent(data);
            return test is null ? Results.Forbid() : Results.Ok(test);
        });

        admin.MapPost("/groups", (AdminTokenData data, TestSessionStore store) =>
            store.GetAdminGroups(data.Token));

        admin.MapPost("/group/save", (AdminSaveGroupData data, TestSessionStore store) =>
        {
            var group = store.SaveAdminGroup(data);
            return group is null ? Results.Forbid() : Results.Ok(group);
        });

        admin.MapPost("/group/student/save", (AdminSaveGroupStudentData data, TestSessionStore store) =>
        {
            var group = store.SaveAdminGroupStudent(data);
            return group is null ? Results.Forbid() : Results.Ok(group);
        });

        admin.MapPost("/group/assign", (AdminAssignGroupData data, TestSessionStore store) =>
        {
            var test = store.AssignAdminGroup(data);
            return test is null ? Results.Forbid() : Results.Ok(test);
        });

        admin.MapPost("/test/publish", (AdminTestRequest request, TestSessionStore store) =>
            store.PublishAdminTest(request.Token, request.TestId));

        admin.MapPost("/test/close", (AdminTestRequest request, TestSessionStore store) =>
        {
            var test = store.CloseAdminTest(request.Token, request.TestId);
            return test is null ? Results.Forbid() : Results.Ok(test);
        });

        admin.MapPost("/attempts", (AdminTokenData data, TestSessionStore store) =>
            store.GetAdminAttempts(data.Token));

        admin.MapPost("/attempt", (AdminGradeData data, TestSessionStore store) =>
        {
            var attempt = store.GetAdminAttempt(data.Token, data.AttemptId);
            return attempt is null ? Results.NotFound("Прохождение не найдено.") : Results.Ok(attempt);
        });

        admin.MapPost("/grade", (AdminGradeData data, TestSessionStore store) =>
        {
            var attempt = store.GradeAnswer(data);
            return attempt is null ? Results.Forbid() : Results.Ok(attempt);
        });

        admin.MapPost("/export/csv", (AdminTokenData data, TestSessionStore store) =>
            store.ExportCsv(data.Token));
    }
}
