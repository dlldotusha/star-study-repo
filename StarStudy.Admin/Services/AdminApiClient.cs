using System.Net.Http.Json;
using Microsoft.JSInterop;
using StarStudy.Admin.Models;

namespace StarStudy.Admin.Services;

public sealed class AdminApiClient(HttpClient http, IJSRuntime js)
{
    private const string TokenStorageKey = "star-study-admin-token";

    public async Task<AdminLoginResult> LoginAsync(AdminLoginData data)
    {
        var result = await PostAsync<AdminLoginData, AdminLoginResult>("api/admin/login", data);

        if (result.Success && !string.IsNullOrWhiteSpace(result.Token))
        {
            await SaveTokenAsync(result.Token);
        }

        return result;
    }

    public async Task<string?> GetTokenAsync()
    {
        return await js.InvokeAsync<string?>("sessionStorage.getItem", TokenStorageKey);
    }

    public async Task LogoutAsync()
    {
        await js.InvokeVoidAsync("sessionStorage.removeItem", TokenStorageKey);
    }

    public async Task<IReadOnlyList<AdminTestSummary>> GetTestsAsync()
    {
        var token = await RequireTokenAsync();
        return await PostAsync<AdminTokenData, List<AdminTestSummary>>("api/admin/tests", new AdminTokenData { Token = token });
    }

    public async Task<AdminTestEdit> GetTestAsync(string testId)
    {
        var token = await RequireTokenAsync();
        return await PostAsync<AdminTestRequest, AdminTestEdit>("api/admin/test", new AdminTestRequest { Token = token, TestId = testId });
    }

    public async Task<AdminTestEdit> CreateTestAsync(AdminCreateTestData data)
    {
        data.Token = await RequireTokenAsync();
        return await PostAsync<AdminCreateTestData, AdminTestEdit>("api/admin/test/create", data);
    }

    public async Task<AdminTestEdit> SaveTestAsync(AdminSaveTestData data)
    {
        data.Token = await RequireTokenAsync();
        return await PostAsync<AdminSaveTestData, AdminTestEdit>("api/admin/test/save", data);
    }

    public async Task<AdminTestEdit> SaveQuestionAsync(AdminSaveQuestionData data)
    {
        data.Token = await RequireTokenAsync();
        return await PostAsync<AdminSaveQuestionData, AdminTestEdit>("api/admin/question/save", data);
    }

    public async Task<AdminTestEdit> SaveStudentAsync(AdminSaveStudentData data)
    {
        data.Token = await RequireTokenAsync();
        return await PostAsync<AdminSaveStudentData, AdminTestEdit>("api/admin/student/save", data);
    }

    public async Task<IReadOnlyList<AdminGroupEdit>> GetGroupsAsync()
    {
        var token = await RequireTokenAsync();
        return await PostAsync<AdminTokenData, List<AdminGroupEdit>>("api/admin/groups", new AdminTokenData { Token = token });
    }

    public async Task<AdminGroupEdit> SaveGroupAsync(AdminSaveGroupData data)
    {
        data.Token = await RequireTokenAsync();
        return await PostAsync<AdminSaveGroupData, AdminGroupEdit>("api/admin/group/save", data);
    }

    public async Task<AdminGroupEdit> SaveGroupStudentAsync(AdminSaveGroupStudentData data)
    {
        data.Token = await RequireTokenAsync();
        return await PostAsync<AdminSaveGroupStudentData, AdminGroupEdit>("api/admin/group/student/save", data);
    }

    public async Task<AdminTestEdit> AssignGroupAsync(AdminAssignGroupData data)
    {
        data.Token = await RequireTokenAsync();
        return await PostAsync<AdminAssignGroupData, AdminTestEdit>("api/admin/group/assign", data);
    }

    public async Task<AdminPublishResult> PublishAsync(string testId)
    {
        var token = await RequireTokenAsync();
        return await PostAsync<AdminTestRequest, AdminPublishResult>("api/admin/test/publish", new AdminTestRequest { Token = token, TestId = testId });
    }

    public async Task<AdminTestEdit> CloseAsync(string testId)
    {
        var token = await RequireTokenAsync();
        return await PostAsync<AdminTestRequest, AdminTestEdit>("api/admin/test/close", new AdminTestRequest { Token = token, TestId = testId });
    }

    public async Task<IReadOnlyList<AdminAttemptSummary>> GetAttemptsAsync()
    {
        var token = await RequireTokenAsync();
        return await PostAsync<AdminTokenData, List<AdminAttemptSummary>>("api/admin/attempts", new AdminTokenData { Token = token });
    }

    public async Task<AdminAttemptDetails> GetAttemptAsync(string attemptId)
    {
        var token = await RequireTokenAsync();
        return await PostAsync<AdminGradeData, AdminAttemptDetails>("api/admin/attempt", new AdminGradeData { Token = token, AttemptId = attemptId });
    }

    public async Task<AdminAttemptDetails> GradeAsync(AdminGradeData data)
    {
        data.Token = await RequireTokenAsync();
        return await PostAsync<AdminGradeData, AdminAttemptDetails>("api/admin/grade", data);
    }

    public async Task<AdminCsvExport> ExportCsvAsync()
    {
        var token = await RequireTokenAsync();
        return await PostAsync<AdminTokenData, AdminCsvExport>("api/admin/export/csv", new AdminTokenData { Token = token });
    }

    private async Task SaveTokenAsync(string token)
    {
        await js.InvokeVoidAsync("sessionStorage.setItem", TokenStorageKey, token);
    }

    private async Task<string> RequireTokenAsync()
    {
        var token = await GetTokenAsync();
        return string.IsNullOrWhiteSpace(token)
            ? throw new InvalidOperationException("Нужно войти в панель управления.")
            : token;
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string url, TRequest request)
    {
        var response = await http.PostAsJsonAsync(url, request);

        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? $"Сервер вернул ошибку {response.StatusCode}."
                : message);
        }

        var result = await response.Content.ReadFromJsonAsync<TResponse>();
        return result ?? throw new InvalidOperationException("Сервер вернул пустой ответ.");
    }
}
