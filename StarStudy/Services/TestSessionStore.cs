using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;
using StarStudy.Admin.Models;
using StarStudy.Client.Models;

namespace StarStudy.Services;

public sealed class TestSessionStore
{
    private const string PasswordHashPrefix = "argon2id";
    private const string LegacyPbkdf2PasswordHashPrefix = "pbkdf2-sha256";
    private const int Argon2MemorySizeKb = 64 * 1024;
    private const int Argon2Iterations = 3;
    private const int Argon2Parallelism = 2;
    private const int PasswordSaltSize = 16;
    private const int PasswordHashSize = 32;

    private readonly object sync = new();
    private readonly string configPath;
    private readonly string? postgresConnectionString;
    private readonly Dictionary<string, TesterAccount> testers;
    private readonly Dictionary<string, StudentAccount> accounts;
    private readonly Dictionary<string, StudentGroupDefinition> groups;
    private readonly Dictionary<string, TestDefinition> tests;
    private readonly Dictionary<string, StudentSession> sessions;
    private readonly Dictionary<string, AdminSession> adminSessions;

    public TestSessionStore(IWebHostEnvironment environment, IConfiguration configuration)
    {
        configPath = Path.Combine(environment.ContentRootPath, "Data", "test-config.json");
        postgresConnectionString = configuration.GetConnectionString("Postgres")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? configuration["POSTGRES_CONNECTION_STRING"];

        if (environment.IsProduction() && string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            throw new InvalidOperationException("В Production нужно задать ConnectionStrings__Postgres или POSTGRES_CONNECTION_STRING.");
        }

        var isProduction = environment.IsProduction();
        var config = LoadState(configPath);
        EnsureBootstrapAdmin(config, configuration, requireWhenEmpty: false);

        if (!string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            config = LoadOrSeedPostgres(config);
        }

        EnsureBootstrapAdmin(config, configuration, isProduction);

        testers = config.Testers.ToDictionary(tester => tester.Login, StringComparer.OrdinalIgnoreCase);
        accounts = config.Users.ToDictionary(account => account.Login, StringComparer.OrdinalIgnoreCase);
        groups = config.Groups.ToDictionary(group => group.Id, StringComparer.OrdinalIgnoreCase);
        tests = config.Tests.ToDictionary(test => test.Id, StringComparer.OrdinalIgnoreCase);
        sessions = new Dictionary<string, StudentSession>(config.Sessions, StringComparer.Ordinal);
        adminSessions = new Dictionary<string, AdminSession>(config.AdminSessions, StringComparer.Ordinal);

        NormalizeAccessFromUsers();
        SaveConfig();
    }

    public LoginResult Login(LoginData data)
    {
        if (!accounts.TryGetValue(data.Login.Trim(), out var account) || !VerifyPassword(data.Password, account.Password))
        {
            return new LoginResult
            {
                Success = false,
                Message = "Неверный логин или пароль."
            };
        }

        var token = CreateToken();

        lock (sync)
        {
            if (!IsPasswordHash(account.Password))
            {
                account.Password = HashPassword(data.Password);
            }

            sessions[token] = new StudentSession(CreateShortId(), account.Login);
            SaveConfig();
        }

        return new LoginResult
        {
            Success = true,
            Token = token,
            Message = "Вход выполнен."
        };
    }

    public StudentState GetState(string token)
    {
        if (!TryGetSession(token, out var session))
        {
            return new StudentState();
        }

        lock (sync)
        {
            Touch(session);
            SaveConfig();
            return BuildState(session);
        }
    }

    public IReadOnlyList<TestInfo> GetTests(string token)
    {
        if (!TryGetSession(token, out var session))
        {
            return [];
        }

        lock (sync)
        {
            Touch(session);
            SaveConfig();

            return tests.Values
                .Where(test => CanUseTest(session.Login, test.Id, requirePublished: true))
                .Select(ToInfo)
                .OrderBy(test => test.Title)
                .ToArray();
        }
    }

    public StudentState OpenTest(string token, string testId)
    {
        if (!TryGetSession(token, out var session))
        {
            return new StudentState();
        }

        lock (sync)
        {
            Touch(session);

            if (session.Finished)
            {
                return BuildState(session);
            }

            if (session.Started)
            {
                return BuildState(session);
            }

            if (!CanUseTest(session.Login, testId, requirePublished: true))
            {
                return BuildState(session);
            }

            session.SelectedTestId = testId;
            session.Started = false;
            session.Finished = false;
            session.CurrentQuestionNumber = 1;
            session.Answers.Clear();
            session.QuestionOpenLog.Clear();
            session.Events.Clear();
            session.Events.Add(EventLine("Открыл описание теста"));
            SaveConfig();

            return BuildState(session);
        }
    }

    public StudentState StartTest(string token, string testId)
    {
        if (!TryGetSession(token, out var session))
        {
            return new StudentState();
        }

        lock (sync)
        {
            Touch(session);

            if (session.Finished)
            {
                return BuildState(session);
            }

            if (session.Started)
            {
                return BuildState(session);
            }

            if (!CanUseTest(session.Login, testId, requirePublished: true))
            {
                return BuildState(session);
            }

            if (!CanStartAttempt(session.Login, testId))
            {
                return BuildState(session);
            }

            session.SelectedTestId = testId;
            session.Started = true;
            session.CurrentQuestionNumber = 1;
            session.StartedAt ??= DateTimeOffset.UtcNow;
            session.Events.Add(EventLine("Начал тест"));
            SaveConfig();

            return BuildState(session);
        }
    }

    public Question? GetQuestion(string token, string testId, int number)
    {
        if (!TryGetSession(token, out var session))
        {
            return null;
        }

        lock (sync)
        {
            Touch(session);

            if (!CanOpenQuestion(session, testId, number, out _, out var question))
            {
                return null;
            }

            session.CurrentQuestionNumber = number;
            session.QuestionOpenLog.Add(new QuestionOpenLog(question.Id, number, DateTimeOffset.UtcNow));
            session.Events.Add(EventLine($"Открыл вопрос {number}"));
            session.Answers.TryGetValue(question.Id, out var savedAnswer);
            SaveConfig();

            return ToQuestion(question, number, savedAnswer);
        }
    }

    public AnswerResult SubmitAnswer(AnswerRequest request)
    {
        if (!TryGetSession(request.Token, out var session))
        {
            return Reject("Нужно войти заново.");
        }

        lock (sync)
        {
            Touch(session);

            if (!CanOpenQuestion(session, request.TestId, request.Number, out _, out var question))
            {
                return Reject("Этот вопрос сейчас недоступен.");
            }

            if (!string.Equals(question.Id, request.QuestionId, StringComparison.Ordinal))
            {
                return Reject("Ответ относится к другому вопросу.");
            }

            if (!HasValidAnswer(question, request))
            {
                return Reject("Заполните ответ перед отправкой.");
            }

            var isAutoChecked = question.Type != QuestionTypes.Text;
            var isCorrect = IsCorrect(question, request);
            var submitted = new SubmittedAnswer
            {
                QuestionId = request.QuestionId,
                Number = request.Number,
                Text = request.Text?.Trim(),
                SelectedOptionIds = request.SelectedOptionIds.Distinct(StringComparer.Ordinal).ToList(),
                IsCorrect = isCorrect,
                Points = isAutoChecked && isCorrect ? question.Points : 0,
                MaxPoints = question.Points,
                ReviewStatus = isAutoChecked ? "auto" : "needs-review",
                SubmittedAt = DateTimeOffset.UtcNow
            };

            session.Answers[request.QuestionId] = submitted;
            session.Events.Add(EventLine($"Отправил ответ на вопрос {request.Number}"));
            SaveConfig();

            return new AnswerResult
            {
                Accepted = true,
                Message = "Ответ принят сервером.",
                IsFinished = session.Finished,
                AnsweredQuestionNumbers = GetAnsweredNumbers(session)
            };
        }
    }

    public StudentState FinishTest(string token, string testId)
    {
        if (!TryGetSession(token, out var session))
        {
            return new StudentState();
        }

        lock (sync)
        {
            Touch(session);

            if (session.SelectedTestId == testId
                && session.Started
                && tests.TryGetValue(testId, out var test)
                && session.Answers.Count >= test.Questions.Count)
            {
                session.Finished = true;
                session.FinishedAt = DateTimeOffset.UtcNow;
                session.Events.Add(EventLine("Завершил тест"));
                SaveConfig();
            }

            return BuildState(session);
        }
    }

    public AdminLoginResult AdminLogin(AdminLoginData data)
    {
        if (!testers.TryGetValue(data.Login.Trim(), out var tester) || !VerifyPassword(data.Password, tester.Password))
        {
            return new AdminLoginResult
            {
                Success = false,
                Message = "Неверный логин или пароль."
            };
        }

        var token = CreateToken();

        lock (sync)
        {
            if (!IsPasswordHash(tester.Password))
            {
                tester.Password = HashPassword(data.Password);
            }

            adminSessions[token] = new AdminSession(tester.Login, tester.Role);
            SaveConfig();
        }

        return new AdminLoginResult
        {
            Success = true,
            Token = token,
            Role = tester.Role,
            Message = "Вход в панель выполнен."
        };
    }

    public IReadOnlyList<AdminTestSummary> GetAdminTests(string token)
    {
        if (!TryGetAdmin(token, out var admin))
        {
            return [];
        }

        lock (sync)
        {
            return tests.Values
                .Where(test => CanSeeAdmin(admin, test))
                .Select(ToSummary)
                .OrderByDescending(test => test.UpdatedAt)
                .ToArray();
        }
    }

    public AdminTestEdit? GetAdminTest(string token, string testId)
    {
        if (!TryGetAdmin(token, out var admin))
        {
            return null;
        }

        lock (sync)
        {
            return tests.TryGetValue(testId, out var test) && CanSeeAdmin(admin, test)
                ? ToAdminEdit(test)
                : null;
        }
    }

    public AdminTestEdit? CreateAdminTest(AdminCreateTestData data)
    {
        if (!TryGetAdmin(data.Token, out var admin) || !CanEdit(admin))
        {
            return null;
        }

        lock (sync)
        {
            var now = DateTimeOffset.UtcNow;
            var test = new TestDefinition
            {
                Id = CreateSlug(data.Title),
                OwnerLogin = admin.Login,
                Title = string.IsNullOrWhiteSpace(data.Title) ? "Новый тест" : data.Title.Trim(),
                ShortDescription = "",
                Description = data.Description.Trim(),
                TimeLimitMinutes = data.TimeLimitMinutes ?? 0,
                Status = AdminTestStatuses.Draft,
                AttemptLimit = 1,
                AllowResume = true,
                AllowBackNavigation = true,
                CreatedAt = now,
                UpdatedAt = now
            };

            while (tests.ContainsKey(test.Id))
            {
                test.Id = $"{test.Id}-{RandomNumberGenerator.GetInt32(100, 999)}";
            }

            tests[test.Id] = test;
            SaveConfig();
            return ToAdminEdit(test);
        }
    }

    public AdminTestEdit? SaveAdminTest(AdminSaveTestData data)
    {
        if (!TryGetAdmin(data.Token, out var admin) || !CanEdit(admin))
        {
            return null;
        }

        lock (sync)
        {
            if (!tests.TryGetValue(data.TestId, out var test) || !CanSeeAdmin(admin, test))
            {
                return null;
            }

            test.Title = data.Title.Trim();
            test.ShortDescription = data.Description.Trim().Split('\n').FirstOrDefault()?.Trim('#', ' ', '\r') ?? "";
            test.Description = data.Description.Trim();
            test.TimeLimitMinutes = data.TimeLimitMinutes ?? 0;
            test.AttemptLimit = Math.Max(1, data.AttemptLimit);
            test.AllowResume = data.AllowResume;
            test.AllowBackNavigation = data.AllowBackNavigation;
            test.ShuffleQuestions = data.ShuffleQuestions;
            test.ShuffleOptions = data.ShuffleOptions;
            test.ShowResultToStudent = data.ShowResultToStudent;
            test.ShowCorrectAnswers = data.ShowCorrectAnswers;
            test.UpdatedAt = DateTimeOffset.UtcNow;

            SaveConfig();
            return ToAdminEdit(test);
        }
    }

    public AdminTestEdit? SaveAdminQuestion(AdminSaveQuestionData data)
    {
        if (!TryGetAdmin(data.Token, out var admin) || !CanEdit(admin))
        {
            return null;
        }

        lock (sync)
        {
            if (!tests.TryGetValue(data.TestId, out var test) || !CanSeeAdmin(admin, test))
            {
                return null;
            }

            var source = data.Question;
            var question = test.Questions.FirstOrDefault(question => question.Id == source.Id);

            if (question is null)
            {
                question = new QuestionDefinition { Id = string.IsNullOrWhiteSpace(source.Id) ? CreateShortId() : source.Id.Trim() };
                test.Questions.Add(question);
            }

            question.Type = source.Type;
            question.Text = source.Text.Trim();
            question.CorrectText = source.CorrectText?.Trim();
            question.Points = Math.Max(1, source.Points);
            question.ReviewerComment = source.ReviewerComment;
            question.Options = source.Options
                .Where(option => !string.IsNullOrWhiteSpace(option.Text))
                .Select(option => new Option { Id = string.IsNullOrWhiteSpace(option.Id) ? CreateShortId() : option.Id.Trim(), Text = option.Text.Trim() })
                .ToList();
            question.CorrectOptionIds = source.CorrectOptionIds
                .Where(id => question.Options.Any(option => option.Id == id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            test.UpdatedAt = DateTimeOffset.UtcNow;
            SaveConfig();
            return ToAdminEdit(test);
        }
    }

    public AdminTestEdit? SaveAdminStudent(AdminSaveStudentData data)
    {
        if (!TryGetAdmin(data.Token, out var admin) || !CanEdit(admin))
        {
            return null;
        }

        lock (sync)
        {
            if (!tests.TryGetValue(data.TestId, out var test) || !CanSeeAdmin(admin, test))
            {
                return null;
            }

            var source = data.Student;

            if (string.IsNullOrWhiteSpace(source.Login) || string.IsNullOrWhiteSpace(source.Password))
            {
                return ToAdminEdit(test);
            }

            if (!accounts.TryGetValue(source.Login, out var account))
            {
                account = new StudentAccount { Login = source.Login.Trim(), Password = HashPassword(source.Password.Trim()) };
                accounts[account.Login] = account;
            }

            if (!string.IsNullOrWhiteSpace(source.Password))
            {
                account.Password = HashPassword(source.Password.Trim());
            }

            if (!account.AvailableTestIds.Contains(test.Id, StringComparer.OrdinalIgnoreCase))
            {
                account.AvailableTestIds = [.. account.AvailableTestIds, test.Id];
            }

            var access = test.StudentAccess.FirstOrDefault(access => string.Equals(access.Login, account.Login, StringComparison.OrdinalIgnoreCase));

            if (access is null)
            {
                access = new StudentAccessDefinition { Login = account.Login };
                test.StudentAccess.Add(access);
            }

            access.Password = account.Password;
            access.AvailableUntil = source.AvailableUntil;
            access.AttemptLimit = Math.Max(1, source.AttemptLimit);
            access.AllowResume = source.AllowResume;
            access.AllowBackNavigation = source.AllowBackNavigation;
            test.UpdatedAt = DateTimeOffset.UtcNow;

            SaveConfig();
            return ToAdminEdit(test);
        }
    }

    public IReadOnlyList<AdminGroupEdit> GetAdminGroups(string token)
    {
        if (!TryGetAdmin(token, out var admin))
        {
            return [];
        }

        lock (sync)
        {
            return groups.Values
                .Where(group => CanSeeGroup(admin, group))
                .Select(ToAdminGroup)
                .OrderBy(group => group.Name)
                .ToArray();
        }
    }

    public AdminGroupEdit? SaveAdminGroup(AdminSaveGroupData data)
    {
        if (!TryGetAdmin(data.Token, out var admin) || !CanEdit(admin))
        {
            return null;
        }

        lock (sync)
        {
            var name = string.IsNullOrWhiteSpace(data.Name) ? "Новая группа" : data.Name.Trim();
            var groupId = string.IsNullOrWhiteSpace(data.GroupId) ? CreateSlug(name) : data.GroupId.Trim();

            if (!groups.TryGetValue(groupId, out var group))
            {
                while (groups.ContainsKey(groupId))
                {
                    groupId = $"{groupId}-{RandomNumberGenerator.GetInt32(100, 999)}";
                }

                group = new StudentGroupDefinition { Id = groupId, OwnerLogin = admin.Login };
                groups[group.Id] = group;
            }

            if (!CanSeeGroup(admin, group))
            {
                return null;
            }

            group.Name = name;
            group.OwnerLogin = string.IsNullOrWhiteSpace(group.OwnerLogin) ? admin.Login : group.OwnerLogin;
            SaveConfig();
            return ToAdminGroup(group);
        }
    }

    public AdminGroupEdit? SaveAdminGroupStudent(AdminSaveGroupStudentData data)
    {
        if (!TryGetAdmin(data.Token, out var admin) || !CanEdit(admin))
        {
            return null;
        }

        lock (sync)
        {
            if (!groups.TryGetValue(data.GroupId, out var group) || !CanSeeGroup(admin, group))
            {
                return null;
            }

            var login = data.Student.Login.Trim();
            var password = data.Student.Password.Trim();

            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            {
                return ToAdminGroup(group);
            }

            if (!accounts.TryGetValue(login, out var account))
            {
                account = new StudentAccount { Login = login, Password = HashPassword(password) };
                accounts[account.Login] = account;
            }

            account.Password = HashPassword(password);

            if (!group.StudentLogins.Contains(account.Login, StringComparer.OrdinalIgnoreCase))
            {
                group.StudentLogins.Add(account.Login);
            }

            SaveConfig();
            return ToAdminGroup(group);
        }
    }

    public AdminTestEdit? AssignAdminGroup(AdminAssignGroupData data)
    {
        if (!TryGetAdmin(data.Token, out var admin) || !CanEdit(admin))
        {
            return null;
        }

        lock (sync)
        {
            if (!tests.TryGetValue(data.TestId, out var test) || !CanSeeAdmin(admin, test))
            {
                return null;
            }

            if (!groups.TryGetValue(data.Group.GroupId, out var group) || !CanSeeGroup(admin, group))
            {
                return ToAdminEdit(test);
            }

            var access = test.GroupAccess.FirstOrDefault(access => string.Equals(access.GroupId, group.Id, StringComparison.OrdinalIgnoreCase));

            if (access is null)
            {
                access = new GroupAccessDefinition { GroupId = group.Id };
                test.GroupAccess.Add(access);
            }

            access.AvailableUntil = data.Group.AvailableUntil;
            access.AttemptLimit = Math.Max(1, data.Group.AttemptLimit);
            access.AllowResume = data.Group.AllowResume;
            access.AllowBackNavigation = data.Group.AllowBackNavigation;
            test.UpdatedAt = DateTimeOffset.UtcNow;

            SaveConfig();
            return ToAdminEdit(test);
        }
    }

    public AdminPublishResult PublishAdminTest(string token, string testId)
    {
        if (!TryGetAdmin(token, out var admin) || !CanEdit(admin))
        {
            return new AdminPublishResult { Success = false, Message = "Нет доступа." };
        }

        lock (sync)
        {
            if (!tests.TryGetValue(testId, out var test) || !CanSeeAdmin(admin, test))
            {
                return new AdminPublishResult { Success = false, Message = "Тест не найден." };
            }

            var messages = ValidateTest(test);

            if (messages.Count > 0)
            {
                return new AdminPublishResult
                {
                    Success = false,
                    Message = "Публикация невозможна: исправьте ошибки.",
                    ValidationMessages = messages
                };
            }

            test.Status = AdminTestStatuses.Published;
            test.UpdatedAt = DateTimeOffset.UtcNow;
            SaveConfig();

            return new AdminPublishResult { Success = true, Message = "Тест опубликован." };
        }
    }

    public AdminTestEdit? CloseAdminTest(string token, string testId)
    {
        if (!TryGetAdmin(token, out var admin) || !CanEdit(admin))
        {
            return null;
        }

        lock (sync)
        {
            if (!tests.TryGetValue(testId, out var test) || !CanSeeAdmin(admin, test))
            {
                return null;
            }

            test.Status = AdminTestStatuses.Closed;
            test.UpdatedAt = DateTimeOffset.UtcNow;
            SaveConfig();
            return ToAdminEdit(test);
        }
    }

    public IReadOnlyList<AdminAttemptSummary> GetAdminAttempts(string token)
    {
        if (!TryGetAdmin(token, out var admin))
        {
            return [];
        }

        lock (sync)
        {
            return sessions.Values
                .Where(session => session.SelectedTestId is not null && tests.TryGetValue(session.SelectedTestId, out var test) && CanSeeAdmin(admin, test))
                .Select(ToAttemptSummary)
                .OrderByDescending(attempt => attempt.LastRequestAt)
                .ToArray();
        }
    }

    public AdminAttemptDetails? GetAdminAttempt(string token, string attemptId)
    {
        if (!TryGetAdmin(token, out var admin))
        {
            return null;
        }

        lock (sync)
        {
            var session = sessions.Values.FirstOrDefault(session => session.AttemptId == attemptId);

            if (session?.SelectedTestId is null || !tests.TryGetValue(session.SelectedTestId, out var test) || !CanSeeAdmin(admin, test))
            {
                return null;
            }

            return ToAttemptDetails(session, test);
        }
    }

    public AdminAttemptDetails? GradeAnswer(AdminGradeData data)
    {
        if (!TryGetAdmin(data.Token, out var admin) || admin.Role == AdminRoles.Observer)
        {
            return null;
        }

        lock (sync)
        {
            var session = sessions.Values.FirstOrDefault(session => session.AttemptId == data.AttemptId);

            if (session?.SelectedTestId is null || !tests.TryGetValue(session.SelectedTestId, out var test) || !CanSeeAdmin(admin, test))
            {
                return null;
            }

            if (session.Answers.TryGetValue(data.QuestionId, out var answer))
            {
                answer.Points = Math.Clamp(data.Points, 0, answer.MaxPoints);
                answer.ReviewerComment = data.ReviewerComment;
                answer.ReviewStatus = "manual";
                answer.GradedAt = DateTimeOffset.UtcNow;
                session.Events.Add(EventLine($"Проверяющий выставил {answer.Points}/{answer.MaxPoints} за вопрос {answer.Number}"));
                SaveConfig();
            }

            return ToAttemptDetails(session, test);
        }
    }

    public AdminCsvExport ExportCsv(string token)
    {
        if (!TryGetAdmin(token, out var admin))
        {
            return new AdminCsvExport();
        }

        lock (sync)
        {
            var builder = new StringBuilder();
            builder.AppendLine("student,test,attempt,status,start,finish,points,max_points,percent,answers");

            foreach (var session in sessions.Values.Where(session => session.SelectedTestId is not null))
            {
                var test = tests[session.SelectedTestId!];

                if (!CanSeeAdmin(admin, test))
                {
                    continue;
                }

                var summary = ToAttemptSummary(session);
                var answers = string.Join(" | ", session.Answers.Values.OrderBy(answer => answer.Number).Select(answer => $"Q{answer.Number}:{answer.ReviewStatus}:{answer.Points}/{answer.MaxPoints}"));

                builder
                    .Append(Csv(session.Login)).Append(',')
                    .Append(Csv(test.Title)).Append(',')
                    .Append(Csv(session.AttemptId)).Append(',')
                    .Append(Csv(summary.Status)).Append(',')
                    .Append(Csv(session.StartedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "")).Append(',')
                    .Append(Csv(session.FinishedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "")).Append(',')
                    .Append(summary.TotalPoints).Append(',')
                    .Append(summary.MaxPoints).Append(',')
                    .Append(summary.Percent.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(Csv(answers))
                    .AppendLine();
            }

            return new AdminCsvExport
            {
                FileName = $"star-study-results-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv",
                Content = builder.ToString()
            };
        }
    }

    private static AnswerResult Reject(string message)
    {
        return new AnswerResult
        {
            Accepted = false,
            Message = message
        };
    }

    private bool TryGetSession(string token, out StudentSession session)
    {
        lock (sync)
        {
            return sessions.TryGetValue(token, out session!);
        }
    }

    private bool TryGetAdmin(string token, out AdminSession session)
    {
        lock (sync)
        {
            return adminSessions.TryGetValue(token, out session!);
        }
    }

    private bool CanUseTest(string login, string testId, bool requirePublished)
    {
        if (!accounts.TryGetValue(login, out var account) || !tests.TryGetValue(testId, out var test))
        {
            return false;
        }

        if (requirePublished && test.Status != AdminTestStatuses.Published)
        {
            return false;
        }

        var directAccess = test.StudentAccess.FirstOrDefault(access => string.Equals(access.Login, login, StringComparison.OrdinalIgnoreCase));

        if (directAccess is not null)
        {
            return directAccess.AvailableUntil is null || directAccess.AvailableUntil > DateTimeOffset.UtcNow;
        }

        if (account.AvailableTestIds.Contains(testId, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var groupAccess = GetGroupAccessForStudent(test, login);
        return groupAccess is not null && (groupAccess.AvailableUntil is null || groupAccess.AvailableUntil > DateTimeOffset.UtcNow);
    }

    private bool CanStartAttempt(string login, string testId)
    {
        var test = tests[testId];
        var directAccess = test.StudentAccess.FirstOrDefault(access => string.Equals(access.Login, login, StringComparison.OrdinalIgnoreCase));
        var groupAccess = GetGroupAccessForStudent(test, login);
        var attemptLimit = directAccess?.AttemptLimit ?? groupAccess?.AttemptLimit ?? test.AttemptLimit;
        var usedAttempts = sessions.Values.Count(session =>
            string.Equals(session.Login, login, StringComparison.OrdinalIgnoreCase)
            && string.Equals(session.SelectedTestId, testId, StringComparison.OrdinalIgnoreCase)
            && session.Started);

        return usedAttempts < attemptLimit;
    }

    private GroupAccessDefinition? GetGroupAccessForStudent(TestDefinition test, string login)
    {
        return test.GroupAccess.FirstOrDefault(access =>
            groups.TryGetValue(access.GroupId, out var group)
            && group.StudentLogins.Contains(login, StringComparer.OrdinalIgnoreCase));
    }

    private bool CanOpenQuestion(StudentSession session, string testId, int number, out TestDefinition test, out QuestionDefinition question)
    {
        test = null!;
        question = null!;

        if (!session.Started
            || session.Finished
            || !string.Equals(session.SelectedTestId, testId, StringComparison.OrdinalIgnoreCase)
            || !tests.TryGetValue(testId, out var foundTest))
        {
            return false;
        }

        test = foundTest;

        if (number < 1 || number > test.Questions.Count)
        {
            return false;
        }

        question = test.Questions[number - 1];
        return true;
    }

    private StudentState BuildState(StudentSession session)
    {
        var state = new StudentState
        {
            IsLoggedIn = true,
            Screen = TestScreenNames.Tests,
            UserName = session.Login,
            CurrentQuestionNumber = session.CurrentQuestionNumber,
            AnsweredQuestionNumbers = GetAnsweredNumbers(session)
        };

        if (session.SelectedTestId is null || !tests.TryGetValue(session.SelectedTestId, out var test))
        {
            return state;
        }

        state.TestId = test.Id;
        state.CurrentTest = ToInfo(test);
        state.TotalQuestions = test.Questions.Count;
        state.Screen = session.Finished
            ? TestScreenNames.Finished
            : session.Started
                ? TestScreenNames.Taking
                : TestScreenNames.Intro;

        return state;
    }

    private static TestInfo ToInfo(TestDefinition test)
    {
        return new TestInfo
        {
            Id = test.Id,
            Title = test.Title,
            ShortDescription = test.ShortDescription,
            Description = test.Description,
            TimeLimitMinutes = test.TimeLimitMinutes,
            QuestionCount = test.Questions.Count
        };
    }

    private HashSet<string> GetAssignedStudentLogins(TestDefinition test)
    {
        var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var access in test.StudentAccess)
        {
            if (!string.IsNullOrWhiteSpace(access.Login))
            {
                logins.Add(access.Login);
            }
        }

        foreach (var access in test.GroupAccess)
        {
            if (!groups.TryGetValue(access.GroupId, out var group))
            {
                continue;
            }

            foreach (var login in group.StudentLogins)
            {
                if (!string.IsNullOrWhiteSpace(login))
                {
                    logins.Add(login);
                }
            }
        }

        return logins;
    }

    private static Question ToQuestion(QuestionDefinition question, int number, SubmittedAnswer? savedAnswer)
    {
        return new Question
        {
            Id = question.Id,
            Number = number,
            Type = question.Type,
            Text = question.Text,
            Options = question.Options.Select(option => new Option
            {
                Id = option.Id,
                Text = option.Text
            }).ToList(),
            SavedAnswer = savedAnswer is null
                ? null
                : new UserAnswer
                {
                    Text = savedAnswer.Text,
                    SelectedOptionIds = savedAnswer.SelectedOptionIds.ToList()
                }
        };
    }

    private AdminTestSummary ToSummary(TestDefinition test)
    {
        var attempts = sessions.Values.Where(session => string.Equals(session.SelectedTestId, test.Id, StringComparison.OrdinalIgnoreCase)).ToArray();

        return new AdminTestSummary
        {
            Id = test.Id,
            Title = test.Title,
            Status = test.Status,
            QuestionCount = test.Questions.Count,
            AssignedStudentCount = GetAssignedStudentLogins(test).Count,
            StartedCount = attempts.Count(session => session.Started),
            FinishedCount = attempts.Count(session => session.Finished),
            CreatedAt = test.CreatedAt,
            UpdatedAt = test.UpdatedAt
        };
    }

    private AdminGroupEdit ToAdminGroup(StudentGroupDefinition group)
    {
        return new AdminGroupEdit
        {
            Id = group.Id,
            Name = group.Name,
            Students = group.StudentLogins
                .Where(login => accounts.ContainsKey(login))
                .OrderBy(login => login)
                .Select(login => new AdminGroupStudentEdit
                {
                    Login = login,
                    Password = ""
                })
                .ToList()
        };
    }

    private AdminTestEdit ToAdminEdit(TestDefinition test)
    {
        var edit = new AdminTestEdit
        {
            Id = test.Id,
            Title = test.Title,
            Description = test.Description,
            TimeLimitMinutes = test.TimeLimitMinutes == 0 ? null : test.TimeLimitMinutes,
            Status = test.Status,
            AttemptLimit = test.AttemptLimit,
            AllowResume = test.AllowResume,
            AllowBackNavigation = test.AllowBackNavigation,
            ShuffleQuestions = test.ShuffleQuestions,
            ShuffleOptions = test.ShuffleOptions,
            ShowResultToStudent = test.ShowResultToStudent,
            ShowCorrectAnswers = test.ShowCorrectAnswers,
            Questions = test.Questions.Select(question => new AdminQuestionEdit
            {
                Id = question.Id,
                Type = question.Type,
                Text = question.Text,
                CorrectText = question.CorrectText,
                Points = question.Points,
                ReviewerComment = question.ReviewerComment,
                Options = question.Options.Select(option => new AdminOptionEdit { Id = option.Id, Text = option.Text }).ToList(),
                CorrectOptionIds = question.CorrectOptionIds.ToList()
            }).ToList(),
            Students = test.StudentAccess.Select(student => new AdminStudentAccessEdit
            {
                Login = student.Login,
                Password = "",
                AvailableUntil = student.AvailableUntil,
                AttemptLimit = student.AttemptLimit,
                AllowResume = student.AllowResume,
                AllowBackNavigation = student.AllowBackNavigation
            }).ToList(),
            Groups = test.GroupAccess.Select(access =>
            {
                groups.TryGetValue(access.GroupId, out var group);

                return new AdminGroupAccessEdit
                {
                    GroupId = access.GroupId,
                    GroupName = group?.Name ?? access.GroupId,
                    StudentCount = group?.StudentLogins.Count ?? 0,
                    AvailableUntil = access.AvailableUntil,
                    AttemptLimit = access.AttemptLimit,
                    AllowResume = access.AllowResume,
                    AllowBackNavigation = access.AllowBackNavigation
                };
            }).ToList(),
            ValidationMessages = ValidateTest(test)
        };

        return edit;
    }

    private AdminAttemptSummary ToAttemptSummary(StudentSession session)
    {
        var test = tests[session.SelectedTestId!];
        var maxPoints = test.Questions.Sum(question => question.Points);
        var totalPoints = session.Answers.Values.Sum(answer => answer.Points);

        return new AdminAttemptSummary
        {
            AttemptId = session.AttemptId,
            StudentLogin = session.Login,
            TestId = test.Id,
            TestTitle = test.Title,
            Status = session.Finished ? "finished" : session.Started ? "active" : "opened",
            CurrentQuestionNumber = session.CurrentQuestionNumber,
            OpenedQuestionCount = session.QuestionOpenLog.Select(log => log.QuestionId).Distinct().Count(),
            SubmittedAnswerCount = session.Answers.Count,
            StartedAt = session.StartedAt,
            FinishedAt = session.FinishedAt,
            LastRequestAt = session.LastRequestAt,
            TotalPoints = totalPoints,
            MaxPoints = maxPoints,
            Percent = maxPoints == 0 ? 0 : Math.Round(totalPoints * 100m / maxPoints, 2)
        };
    }

    private AdminAttemptDetails ToAttemptDetails(StudentSession session, TestDefinition test)
    {
        return new AdminAttemptDetails
        {
            Summary = ToAttemptSummary(session),
            Timeline = session.Events.ToList(),
            Answers = test.Questions.Select((question, index) =>
            {
                session.Answers.TryGetValue(question.Id, out var answer);
                var openedAt = session.QuestionOpenLog.LastOrDefault(log => log.QuestionId == question.Id)?.OpenedAt;

                return new AdminAnswerReview
                {
                    QuestionId = question.Id,
                    Number = index + 1,
                    Type = question.Type,
                    QuestionText = question.Text,
                    StudentAnswer = answer is null ? "" : FormatStudentAnswer(question, answer),
                    CorrectAnswer = FormatCorrectAnswer(question),
                    ReviewStatus = answer?.ReviewStatus ?? "not-submitted",
                    Points = answer?.Points ?? 0,
                    MaxPoints = question.Points,
                    ReviewerComment = answer?.ReviewerComment,
                    OpenedAt = openedAt,
                    SubmittedAt = answer?.SubmittedAt
                };
            }).ToList()
        };
    }

    private static bool HasValidAnswer(QuestionDefinition question, AnswerRequest request)
    {
        return question.Type switch
        {
            QuestionTypes.Text => !string.IsNullOrWhiteSpace(request.Text),
            QuestionTypes.SingleChoice => request.SelectedOptionIds.Count == 1
                && question.Options.Any(option => option.Id == request.SelectedOptionIds[0]),
            QuestionTypes.MultiChoice => request.SelectedOptionIds.Count > 0
                && request.SelectedOptionIds.All(id => question.Options.Any(option => option.Id == id)),
            _ => true
        };
    }

    private static bool IsCorrect(QuestionDefinition question, AnswerRequest request)
    {
        if (question.Type == QuestionTypes.Text)
        {
            return string.Equals(question.CorrectText?.Trim(), request.Text?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        var expected = question.CorrectOptionIds.Order(StringComparer.Ordinal).ToArray();
        var actual = request.SelectedOptionIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        return expected.SequenceEqual(actual);
    }

    private static List<int> GetAnsweredNumbers(StudentSession session)
    {
        return session.Answers.Values
            .Select(answer => answer.Number)
            .Distinct()
            .Order()
            .ToList();
    }

    private List<string> ValidateTest(TestDefinition test)
    {
        var messages = new List<string>();

        if (string.IsNullOrWhiteSpace(test.Title))
        {
            messages.Add("У теста должно быть название.");
        }

        if (test.Questions.Count == 0)
        {
            messages.Add("Добавьте хотя бы один вопрос.");
        }

        if (GetAssignedStudentLogins(test).Count == 0)
        {
            messages.Add("Назначьте хотя бы одного ученика или группу.");
        }

        var duplicateIds = test.Questions.GroupBy(question => question.Id).Where(group => group.Count() > 1).Select(group => group.Key);

        foreach (var duplicateId in duplicateIds)
        {
            messages.Add($"Дублируется id вопроса: {duplicateId}.");
        }

        foreach (var question in test.Questions)
        {
            if (string.IsNullOrWhiteSpace(question.Id))
            {
                messages.Add("У каждого вопроса должен быть id.");
            }

            if (string.IsNullOrWhiteSpace(question.Text))
            {
                messages.Add($"У вопроса {question.Id} должен быть текст.");
            }

            if ((question.Type == QuestionTypes.SingleChoice || question.Type == QuestionTypes.MultiChoice) && question.Options.Count == 0)
            {
                messages.Add($"У вопроса {question.Id} должны быть варианты ответа.");
            }

            if (question.Type == QuestionTypes.SingleChoice && question.CorrectOptionIds.Count != 1)
            {
                messages.Add($"У вопроса {question.Id} должен быть один правильный вариант.");
            }

            if (question.Type == QuestionTypes.MultiChoice && question.CorrectOptionIds.Count == 0)
            {
                messages.Add($"У вопроса {question.Id} должен быть хотя бы один правильный вариант.");
            }

            if (question.Type == QuestionTypes.Text && string.IsNullOrWhiteSpace(question.CorrectText))
            {
                messages.Add($"У текстового вопроса {question.Id} задайте правильный ответ или проверяйте вручную после публикации.");
            }
        }

        return messages;
    }

    private void NormalizeAccessFromUsers()
    {
        foreach (var group in groups.Values)
        {
            group.Id = string.IsNullOrWhiteSpace(group.Id) ? CreateShortId() : group.Id.Trim();
            group.Name = string.IsNullOrWhiteSpace(group.Name) ? group.Id : group.Name.Trim();
            group.OwnerLogin = string.IsNullOrWhiteSpace(group.OwnerLogin) ? testers.Keys.FirstOrDefault() ?? "admin" : group.OwnerLogin;
            group.StudentLogins = group.StudentLogins
                .Where(login => !string.IsNullOrWhiteSpace(login) && accounts.ContainsKey(login))
                .Select(login => accounts[login].Login)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(login => login)
                .ToList();
        }

        foreach (var tester in testers.Values)
        {
            tester.Password = NormalizePassword(tester.Password);
        }

        foreach (var account in accounts.Values)
        {
            account.Password = NormalizePassword(account.Password);
        }

        foreach (var test in tests.Values)
        {
            test.Status = string.IsNullOrWhiteSpace(test.Status) ? AdminTestStatuses.Published : test.Status;
            test.OwnerLogin = string.IsNullOrWhiteSpace(test.OwnerLogin) ? testers.Keys.FirstOrDefault() ?? "admin" : test.OwnerLogin;
            test.CreatedAt = test.CreatedAt == default ? DateTimeOffset.UtcNow : test.CreatedAt;
            test.UpdatedAt = test.UpdatedAt == default ? test.CreatedAt : test.UpdatedAt;
            test.AttemptLimit = Math.Max(1, test.AttemptLimit);
            test.GroupAccess ??= [];

            foreach (var access in test.GroupAccess)
            {
                access.AttemptLimit = Math.Max(1, access.AttemptLimit);
            }

            foreach (var question in test.Questions)
            {
                question.Points = Math.Max(1, question.Points);
                question.Options ??= [];
                question.CorrectOptionIds ??= [];
            }

            foreach (var access in test.StudentAccess)
            {
                access.Password = NormalizePassword(access.Password);
            }
        }

        foreach (var account in accounts.Values)
        {
            foreach (var testId in account.AvailableTestIds)
            {
                if (!tests.TryGetValue(testId, out var test))
                {
                    continue;
                }

                if (test.StudentAccess.Any(access => string.Equals(access.Login, account.Login, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                test.StudentAccess.Add(new StudentAccessDefinition
                {
                    Login = account.Login,
                    Password = account.Password,
                    AttemptLimit = 1,
                    AllowResume = true,
                    AllowBackNavigation = true
                });
            }
        }
    }

    private void SaveConfig()
    {
        var config = new StoreDocument
        {
            Testers = testers.Values.OrderBy(tester => tester.Login).ToList(),
            Users = accounts.Values.OrderBy(account => account.Login).ToList(),
            Groups = groups.Values.OrderBy(group => group.Name).ToList(),
            Tests = tests.Values.OrderBy(test => test.Title).ToList(),
            Sessions = new Dictionary<string, StudentSession>(sessions, StringComparer.Ordinal),
            AdminSessions = new Dictionary<string, AdminSession>(adminSessions, StringComparer.Ordinal)
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(config, options);

        if (!string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            SavePostgres(json);
            return;
        }

        File.WriteAllText(configPath, json, Encoding.UTF8);
    }

    private static StoreDocument LoadState(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("Не найден конфиг тестов.", configPath);
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<StoreDocument>(json, options);

        if (config is null)
        {
            throw new InvalidOperationException("Конфиг тестов пустой или повреждён.");
        }

        return config;
    }

    private static void EnsureBootstrapAdmin(StoreDocument config, IConfiguration configuration, bool requireWhenEmpty)
    {
        var login = configuration["STAR_STUDY_ADMIN_LOGIN"] ?? configuration["StarStudy:Admin:Login"];
        var password = configuration["STAR_STUDY_ADMIN_PASSWORD"] ?? configuration["StarStudy:Admin:Password"];
        var role = configuration["STAR_STUDY_ADMIN_ROLE"] ?? configuration["StarStudy:Admin:Role"] ?? AdminRoles.Owner;

        if (!string.IsNullOrWhiteSpace(login) && !string.IsNullOrWhiteSpace(password))
        {
            var tester = config.Testers.FirstOrDefault(tester => string.Equals(tester.Login, login, StringComparison.OrdinalIgnoreCase));

            if (tester is null)
            {
                config.Testers.Add(new TesterAccount { Login = login.Trim(), Password = HashPassword(password), Role = role });
            }
            else
            {
                tester.Password = HashPassword(password);
                tester.Role = role;
            }

            return;
        }

        if (config.Testers.Count == 0 && requireWhenEmpty)
        {
            throw new InvalidOperationException("Перед первым production-запуском задайте STAR_STUDY_ADMIN_LOGIN и STAR_STUDY_ADMIN_PASSWORD.");
        }
    }

    private StoreDocument LoadOrSeedPostgres(StoreDocument seed)
    {
        using var connection = new NpgsqlConnection(postgresConnectionString);
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                create table if not exists app_state (
                    id text primary key,
                    document jsonb not null,
                    updated_at timestamptz not null default now()
                );
                """;
            create.ExecuteNonQuery();
        }

        using (var select = connection.CreateCommand())
        {
            select.CommandText = "select document::text from app_state where id = 'main';";
            var existing = select.ExecuteScalar() as string;

            if (!string.IsNullOrWhiteSpace(existing))
            {
                return JsonSerializer.Deserialize<StoreDocument>(existing, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? seed;
            }
        }

        var seedJson = JsonSerializer.Serialize(seed, new JsonSerializerOptions { WriteIndented = true });

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                insert into app_state (id, document, updated_at)
                values ('main', @document, now())
                on conflict (id) do nothing;
                """;
            insert.Parameters.Add(new NpgsqlParameter("document", NpgsqlDbType.Jsonb) { Value = seedJson });
            insert.ExecuteNonQuery();
        }

        return seed;
    }

    private void SavePostgres(string json)
    {
        using var connection = new NpgsqlConnection(postgresConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into app_state (id, document, updated_at)
            values ('main', @document, now())
            on conflict (id) do update set document = excluded.document, updated_at = now();
            """;
        command.Parameters.Add(new NpgsqlParameter("document", NpgsqlDbType.Jsonb) { Value = json });
        command.ExecuteNonQuery();
    }

    private static bool CanEdit(AdminSession session)
    {
        return session.Role == AdminRoles.Owner;
    }

    private static bool CanSeeAdmin(AdminSession session, TestDefinition test)
    {
        return session.Role != AdminRoles.Owner || string.Equals(test.OwnerLogin, session.Login, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanSeeGroup(AdminSession session, StudentGroupDefinition group)
    {
        return session.Role != AdminRoles.Owner || string.Equals(group.OwnerLogin, session.Login, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatStudentAnswer(QuestionDefinition question, SubmittedAnswer answer)
    {
        if (question.Type == QuestionTypes.Text)
        {
            return answer.Text ?? "";
        }

        return string.Join(", ", question.Options
            .Where(option => answer.SelectedOptionIds.Contains(option.Id))
            .Select(option => option.Text));
    }

    private static string FormatCorrectAnswer(QuestionDefinition question)
    {
        if (question.Type == QuestionTypes.Text)
        {
            return question.CorrectText ?? "";
        }

        return string.Join(", ", question.Options
            .Where(option => question.CorrectOptionIds.Contains(option.Id))
            .Select(option => option.Text));
    }

    private static string Csv(string value)
    {
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string EventLine(string text)
    {
        return $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC · {text}";
    }

    private static void Touch(StudentSession session)
    {
        session.LastRequestAt = DateTimeOffset.UtcNow;
    }

    private static string NormalizePassword(string password)
    {
        return IsPasswordHash(password) || IsLegacyPbkdf2PasswordHash(password) ? password : HashPassword(password);
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(PasswordSaltSize);
        var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = Argon2MemorySizeKb,
            Iterations = Argon2Iterations,
            DegreeOfParallelism = Argon2Parallelism
        };
        var hash = argon2.GetBytes(PasswordHashSize);

        return string.Join('$',
            PasswordHashPrefix,
            "v=19",
            $"m={Argon2MemorySizeKb},t={Argon2Iterations},p={Argon2Parallelism}",
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    private static bool VerifyPassword(string password, string storedPassword)
    {
        if (string.IsNullOrWhiteSpace(storedPassword))
        {
            return false;
        }

        if (IsLegacyPbkdf2PasswordHash(storedPassword))
        {
            return VerifyLegacyPbkdf2Password(password, storedPassword);
        }

        if (!IsPasswordHash(storedPassword))
        {
            return string.Equals(password, storedPassword, StringComparison.Ordinal);
        }

        var parts = storedPassword.Split('$');

        if (parts.Length != 5 || parts[1] != "v=19")
        {
            return false;
        }

        try
        {
            var parameters = ParseArgon2Parameters(parts[2]);
            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            {
                Salt = salt,
                MemorySize = parameters.MemorySize,
                Iterations = parameters.Iterations,
                DegreeOfParallelism = parameters.Parallelism
            };
            var actual = argon2.GetBytes(expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsPasswordHash(string password)
    {
        return password.StartsWith($"{PasswordHashPrefix}$", StringComparison.Ordinal);
    }

    private static bool IsLegacyPbkdf2PasswordHash(string password)
    {
        return password.StartsWith($"{LegacyPbkdf2PasswordHashPrefix}$", StringComparison.Ordinal);
    }

    private static bool VerifyLegacyPbkdf2Password(string password, string storedPassword)
    {
        var parts = storedPassword.Split('$');

        if (parts.Length != 4 || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var iterations))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static (int MemorySize, int Iterations, int Parallelism) ParseArgon2Parameters(string value)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var pair in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2);

            if (parts.Length == 2 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var number))
            {
                result[parts[0]] = number;
            }
        }

        return (
            result.GetValueOrDefault("m", Argon2MemorySizeKb),
            result.GetValueOrDefault("t", Argon2Iterations),
            result.GetValueOrDefault("p", Argon2Parallelism));
    }

    private static string CreateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private static string CreateShortId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    }

    private static string CreateSlug(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "test" : value.Trim().ToLowerInvariant();
        var builder = new StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? $"test-{CreateShortId()}" : slug;
    }

    private sealed class StoreDocument
    {
        public List<TesterAccount> Testers { get; set; } = [];
        public List<StudentAccount> Users { get; set; } = [];
        public List<StudentGroupDefinition> Groups { get; set; } = [];
        public List<TestDefinition> Tests { get; set; } = [];
        public Dictionary<string, StudentSession> Sessions { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, AdminSession> AdminSessions { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class TesterAccount
    {
        public string Login { get; set; } = "";
        public string Password { get; set; } = "";
        public string Role { get; set; } = AdminRoles.Owner;
    }

    private sealed class StudentAccount
    {
        public string Login { get; set; } = "";
        public string Password { get; set; } = "";
        public string[] AvailableTestIds { get; set; } = [];
    }

    private sealed class StudentGroupDefinition
    {
        public string Id { get; set; } = "";
        public string OwnerLogin { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> StudentLogins { get; set; } = [];
    }

    private sealed class TestDefinition
    {
        public string Id { get; set; } = "";
        public string OwnerLogin { get; set; } = "";
        public string Title { get; set; } = "";
        public string ShortDescription { get; set; } = "";
        public string Description { get; set; } = "";
        public int TimeLimitMinutes { get; set; }
        public string Status { get; set; } = AdminTestStatuses.Published;
        public int AttemptLimit { get; set; } = 1;
        public bool AllowResume { get; set; } = true;
        public bool AllowBackNavigation { get; set; } = true;
        public bool ShuffleQuestions { get; set; }
        public bool ShuffleOptions { get; set; }
        public bool ShowResultToStudent { get; set; }
        public bool ShowCorrectAnswers { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<QuestionDefinition> Questions { get; set; } = [];
        public List<StudentAccessDefinition> StudentAccess { get; set; } = [];
        public List<GroupAccessDefinition> GroupAccess { get; set; } = [];
    }

    private sealed class QuestionDefinition
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = QuestionTypes.Text;
        public string Text { get; set; } = "";
        public List<Option> Options { get; set; } = [];
        public string? CorrectText { get; set; }
        public List<string> CorrectOptionIds { get; set; } = [];
        public int Points { get; set; } = 1;
        public string? ReviewerComment { get; set; }
    }

    private sealed class StudentAccessDefinition
    {
        public string Login { get; set; } = "";
        public string Password { get; set; } = "";
        public DateTimeOffset? AvailableUntil { get; set; }
        public int AttemptLimit { get; set; } = 1;
        public bool AllowResume { get; set; } = true;
        public bool AllowBackNavigation { get; set; } = true;
    }

    private sealed class GroupAccessDefinition
    {
        public string GroupId { get; set; } = "";
        public DateTimeOffset? AvailableUntil { get; set; }
        public int AttemptLimit { get; set; } = 1;
        public bool AllowResume { get; set; } = true;
        public bool AllowBackNavigation { get; set; } = true;
    }

    private sealed class StudentSession
    {
        public StudentSession()
        {
        }

        public StudentSession(string attemptId, string login)
        {
            AttemptId = attemptId;
            Login = login;
        }

        public string AttemptId { get; set; } = "";
        public string Login { get; set; } = "";
        public string? SelectedTestId { get; set; }
        public bool Started { get; set; }
        public bool Finished { get; set; }
        public int CurrentQuestionNumber { get; set; } = 1;
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public DateTimeOffset LastRequestAt { get; set; } = DateTimeOffset.UtcNow;
        public Dictionary<string, SubmittedAnswer> Answers { get; set; } = new(StringComparer.Ordinal);
        public List<QuestionOpenLog> QuestionOpenLog { get; set; } = [];
        public List<string> Events { get; set; } = [EventLine("Вошёл в систему")];
    }

    private sealed class AdminSession
    {
        public AdminSession()
        {
        }

        public AdminSession(string login, string role)
        {
            Login = login;
            Role = role;
        }

        public string Login { get; set; } = "";
        public string Role { get; set; } = AdminRoles.Observer;
    }

    private sealed record QuestionOpenLog(string QuestionId, int Number, DateTimeOffset OpenedAt);

    private sealed class SubmittedAnswer
    {
        public string QuestionId { get; set; } = "";
        public int Number { get; set; }
        public string? Text { get; set; }
        public List<string> SelectedOptionIds { get; set; } = [];
        public bool IsCorrect { get; set; }
        public int Points { get; set; }
        public int MaxPoints { get; set; }
        public string ReviewStatus { get; set; } = "new";
        public string? ReviewerComment { get; set; }
        public DateTimeOffset SubmittedAt { get; set; }
        public DateTimeOffset? GradedAt { get; set; }
    }
}
