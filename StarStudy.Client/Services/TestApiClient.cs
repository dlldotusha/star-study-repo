using System.Net.Http.Json;
using Microsoft.JSInterop;
using StarStudy.Client.Models;

namespace StarStudy.Client.Services;

public sealed class TestApiClient(HttpClient http, IJSRuntime js)
{
    private const string TokenStorageKey = "star-study-test-token";

    public async Task<LoginResult> LoginAsync(LoginData loginData)
    {
        var result = await PostAsync<LoginData, LoginResult>("api/student/login", loginData);

        if (result.Success && !string.IsNullOrWhiteSpace(result.Token))
        {
            await SaveTokenAsync(result.Token);
        }

        return result;
    }

    public async Task<StudentState> GetStateAsync()
    {
        var token = await GetTokenAsync();

        if (string.IsNullOrWhiteSpace(token))
        {
            return new StudentState();
        }

        return await PostAsync<TokenData, StudentState>("api/student/state", new TokenData { Token = token });
    }

    public async Task<IReadOnlyList<TestInfo>> GetTestsAsync()
    {
        var token = await RequireTokenAsync();
        var result = await PostAsync<TokenData, List<TestInfo>>("api/student/tests", new TokenData { Token = token });
        return result;
    }

    public async Task<StudentState> OpenTestAsync(string testId)
    {
        var token = await RequireTokenAsync();
        return await PostAsync<TestRequest, StudentState>("api/tests/open", new TestRequest
        {
            Token = token,
            TestId = testId
        });
    }

    public async Task<StudentState> StartTestAsync(string testId)
    {
        var token = await RequireTokenAsync();
        return await PostAsync<TestRequest, StudentState>("api/tests/start", new TestRequest
        {
            Token = token,
            TestId = testId
        });
    }

    public async Task<Question> GetQuestionAsync(string testId, int number)
    {
        var token = await RequireTokenAsync();
        return await PostAsync<QuestionRequest, Question>("api/tests/question", new QuestionRequest
        {
            Token = token,
            TestId = testId,
            Number = number
        });
    }

    public async Task<AnswerResult> SubmitAnswerAsync(string testId, Question question, UserAnswer answer)
    {
        var token = await RequireTokenAsync();
        return await PostAsync<AnswerRequest, AnswerResult>("api/tests/answer", new AnswerRequest
        {
            Token = token,
            TestId = testId,
            Number = question.Number,
            QuestionId = question.Id,
            Text = answer.Text,
            SelectedOptionIds = answer.SelectedOptionIds
        });
    }

    public async Task<StudentState> FinishTestAsync(string testId)
    {
        var token = await RequireTokenAsync();
        return await PostAsync<TestRequest, StudentState>("api/tests/finish", new TestRequest
        {
            Token = token,
            TestId = testId
        });
    }

    public async Task LogoutAsync()
    {
        await js.InvokeVoidAsync("sessionStorage.removeItem", TokenStorageKey);
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

    private async Task<string?> GetTokenAsync()
    {
        return await js.InvokeAsync<string?>("sessionStorage.getItem", TokenStorageKey);
    }

    private async Task SaveTokenAsync(string token)
    {
        await js.InvokeVoidAsync("sessionStorage.setItem", TokenStorageKey, token);
    }

    private async Task<string> RequireTokenAsync()
    {
        var token = await GetTokenAsync();

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Нужно войти перед прохождением теста.");
        }

        return token;
    }
}
