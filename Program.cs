using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using QaDocBackend.Data;
using QaDocBackend.Infrastructure;
using QaDocBackend.Models;
using QaDocBackend.Repositories;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers(options => options.Filters.Add<GuestReadOnlyFilter>());
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // "Authorize" button in Swagger UI: paste the token from POST /api/auth/login.
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
    });
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IProjectMemberRepository, ProjectMemberRepository>();
builder.Services.AddScoped<IFolderRepository, FolderRepository>();
builder.Services.AddScoped<IAttachmentRepository, AttachmentRepository>();
builder.Services.AddScoped<ITicketRepository, TicketRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<IProjectAccessService, ProjectAccessService>();
// ---------- Test Case Generator ----------
// One interface, the provider named by config, and a named HttpClient shared by both providers:
// 90 s is long enough for a vision model to read several screenshots and answer, and the single
// retry on a transient failure lives in ProviderHttp so no extra policy package is needed.
var generatorSettings = builder.Configuration.GetSection("TestCaseGenerator").Get<TestCaseGeneratorSettings>() ?? new TestCaseGeneratorSettings();
if (!TestCaseProviders.IsKnown(generatorSettings.Provider))
    throw new InvalidOperationException(
        $"TestCaseGenerator:Provider must be one of {string.Join(", ", TestCaseProviders.All)} (got \"{generatorSettings.Provider}\").");
builder.Services.AddSingleton(generatorSettings);
builder.Services.AddSingleton(builder.Configuration.GetSection("Groq").Get<GroqSettings>() ?? new GroqSettings());
builder.Services.AddSingleton(builder.Configuration.GetSection("Gemini").Get<GeminiSettings>() ?? new GeminiSettings());
builder.Services.AddScoped<ITestSuiteRepository, TestSuiteRepository>();
// Per-user allowance for generate: in memory, an hour wide, configured rather than compiled.
builder.Services.AddSingleton<IGenerationRateLimiter, GenerationRateLimiter>();
builder.Services.AddHttpClient(GroqTestCaseGenerator.HttpClientName, client =>
    client.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddSingleton<GroqTestCaseGenerator>();
builder.Services.AddSingleton<GeminiTestCaseGenerator>();
// "Mock" answers from memory. It is what the test suites point at, so no test can reach a real key.
builder.Services.AddSingleton<MockTestCaseGenerator>();
builder.Services.AddSingleton<ITestCaseGenerator, ConfiguredTestCaseGenerator>();

// ---------- QC Generator (Product Documentation & User Manual) ----------
var docGeneratorSettings = builder.Configuration.GetSection("DocumentGenerator").Get<DocumentGeneratorSettings>() ?? new DocumentGeneratorSettings();
if (!DocumentProviders.IsKnown(docGeneratorSettings.Provider))
    throw new InvalidOperationException(
        $"DocumentGenerator:Provider must be one of {string.Join(", ", DocumentProviders.All)} (got \"{docGeneratorSettings.Provider}\").");
builder.Services.AddSingleton(docGeneratorSettings);
builder.Services.AddScoped<IQcRepository, QcRepository>();
builder.Services.AddSingleton<IQcGenerationRateLimiter, QcGenerationRateLimiter>();
builder.Services.AddSingleton<GroqDocumentGenerator>();
builder.Services.AddSingleton<GeminiDocumentGenerator>();
builder.Services.AddSingleton<MockDocumentGenerator>();
builder.Services.AddSingleton<IDocumentGenerator, ConfiguredDocumentGenerator>();
builder.Services.AddSingleton<IQcJobService, QcJobService>();
// ---------- Authentication (JWT) ----------
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtSettings>() ?? new JwtSettings();
var signingKey = jwt.SigningKey(); // fails fast at startup when the key is missing
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<IPasswordHasher<UserRecord>, PasswordHasher<UserRecord>>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = signingKey,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = JwtRegisteredClaimNames.UniqueName,
            RoleClaimType = ClaimTypes.Role
        };
        options.Events = new JwtBearerEvents
        {
            // A <video src> cannot send an Authorization header, so attachment downloads may carry
            // the token as ?access_token=. Deliberately confined to that one path: the token would
            // otherwise end up in browser history and server logs for every request.
            OnMessageReceived = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api/attachments")
                    && context.Request.Query.TryGetValue("access_token", out var fromQuery))
                {
                    context.Token = fromQuery;
                }
                return Task.CompletedTask;
            },
            // Re-check the account on every request: deactivation, password resets and
            // role changes take effect immediately instead of when the token expires.
            OnTokenValidated = async context =>
            {
                var principal = context.Principal!;
                // A guest token names no account, so there is nothing to re-check. It carries no
                // role either: GuestReadOnlyFilter and IProjectAccessService decide what it reaches.
                if (principal.IsGuest()) return;

                var users = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
                bool validId = int.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out int userId);
                var user = validId ? await users.GetByIdAsync(userId) : null;
                if (user == null || !user.IsActive || principal.FindFirstValue(QaDocClaims.TokenVersion) != user.TokenVersion.ToString())
                {
                    context.Fail("Session is no longer valid.");
                    return;
                }
                ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.Role, user.Role));
            }
        };
    });
// Every endpoint requires a signed-in user unless marked [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
// ---------- CORS ----------
// Allowed front-end origins come from config: "Cors:Origins" (e.g. Cors__Origins__0 in Railway variables).
// Defaults to the Angular dev server if nothing is configured.
// An entry may contain * for one label part, e.g. https://qadocfrontend-*-qad-oc.vercel.app to match
// every Vercel preview deployment, in addition to your stable production domain.
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() is { Length: > 0 } origins
    ? origins
    : ["http://localhost:4200"];
var corsPatterns = corsOrigins
    .Select(o => new System.Text.RegularExpressions.Regex(
        "^" + System.Text.RegularExpressions.Regex.Escape(o.Trim().TrimEnd('/')).Replace(@"\*", "[a-z0-9-]*") + "$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    .ToArray();
// In Development any page served from this machine may call the API, whatever its port: `ng serve`
// on 4200 or a fallback port, VS Code's preview, or 127.0.0.1 instead of localhost. Production
// never gets this: there only the configured origins are allowed.
var allowLoopback = builder.Environment.IsDevelopment();
static bool IsLoopback(string origin) =>
    Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback;
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngularApp", policy =>
    {
        policy.SetIsOriginAllowed(origin => (allowLoopback && IsLoopback(origin)) || corsPatterns.Any(p => p.IsMatch(origin)))
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});
var app = builder.Build();
// Create or update the database tables before serving requests.
await SchemaInitializer.ApplyAsync(app.Services);
app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Q Desk API v1");
    c.RoutePrefix = "swagger";
});
app.UseRouting();
app.UseCors("AllowAngularApp"); // MUST come after UseRouting, before UseAuthorization/MapControllers
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();